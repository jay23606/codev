using System.Runtime.InteropServices;

namespace Codev.Tests;

public sealed class CustomSlashCommandServiceTests
{
    [Fact]
    public void Parses_markdown_frontmatter_with_named_arguments()
    {
        const string markdown = """
---
description: Review one part of the project
arguments: area, file
---
Review {{area}} in {{file}} and report only actionable findings.
""";

        Assert.True(CustomSlashCommandService.TryParseFile("inspect-auth.md", markdown, "user", out var command, out var error), error);
        Assert.NotNull(command);
        Assert.Equal("/inspect-auth", command.Name);
        Assert.Equal("Review one part of the project", command.Description);
        Assert.Equal(["area", "file"], command.ArgumentNames);
        Assert.Equal("user", command.Scope);
        Assert.True(command.IsCustom);
    }

    [Theory]
    [InlineData("unfinished.md", "missing frontmatter", "Start the Markdown file")]
    [InlineData("Bad Name.md", "---\ndescription: X\n---\nPrompt", "Use a Markdown filename")]
    [InlineData("status.md", "---\ndescription: X\n---\nPrompt", "cannot replace built-in")]
    [InlineData("foo.md", "---\ndescription: X\narguments: area\n---\nReview {{other}}", "must be declared")]
    [InlineData("foo.md", "---\ndescription: X\narguments: area\n---\nReview this", "must appear")]
    [InlineData("foo.md", "---\ndescription: X\nunknown: no\n---\nPrompt", "Unsupported frontmatter")]
    public void Rejects_malformed_or_reserved_command_files(string fileName, string contents, string errorPart)
    {
        Assert.False(CustomSlashCommandService.TryParseFile(fileName, contents, "user", out _, out var error));
        Assert.Contains(errorPart, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expands_named_arguments_and_supports_quoted_values_with_spaces()
    {
        var command = Parse("inspect.md", """
---
description: Inspect a named area
arguments: area, file
---
Review {{area}} in {{file}}.
""");

        var result = CustomSlashCommandService.Expand(command, "/inspect area=\"login flow\" file=src/Auth.cs");

        Assert.True(result.Success, result.Error);
        Assert.Equal("Review login flow in src/Auth.cs.", result.Prompt);
    }

    [Theory]
    [InlineData("/inspect", "Missing required argument")]
    [InlineData("/inspect area=auth file=src/Auth.cs unexpected=yes", "Unknown argument")]
    [InlineData("/inspect area=auth area=login file=src/Auth.cs", "more than once")]
    [InlineData("/inspect area=\"login flow file=src/Auth.cs", "unclosed quote")]
    public void Invalid_invocations_are_rejected_without_returning_a_prompt(string invocation, string expectedError)
    {
        var command = Parse("inspect.md", """
---
description: Inspect a named area
arguments: area, file
---
Review {{area}} in {{file}}.
""");

        var result = CustomSlashCommandService.Expand(command, invocation);

        Assert.False(result.Success);
        Assert.Empty(result.Prompt);
        Assert.Contains(expectedError, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Trusted_project_commands_override_user_commands_and_untrusted_project_commands_stay_hidden()
    {
        var root = CreateTempDirectory();
        try
        {
            var user = Path.Combine(root, "user-commands");
            var project = Path.Combine(root, "project");
            var projectCommands = Path.Combine(project, ".codev", "commands");
            Directory.CreateDirectory(user);
            Directory.CreateDirectory(projectCommands);
            await File.WriteAllTextAsync(Path.Combine(user, "shared.md"), "---\ndescription: User copy\n---\nUser prompt");
            await File.WriteAllTextAsync(Path.Combine(user, "status.md"), "---\ndescription: Builtin collision\n---\nShould not load");
            await File.WriteAllTextAsync(Path.Combine(projectCommands, "shared.md"), "---\ndescription: Project copy\n---\nProject prompt");
            await File.WriteAllTextAsync(Path.Combine(projectCommands, "local-only.txt"), "ignored");

            var untrusted = await CustomSlashCommandService.LoadAsync(user, project, includeProjectCommands: false);
            var untrustedShared = Assert.Single(untrusted.Commands, command => command.Name == "/shared");
            Assert.Equal("user", untrustedShared.Scope);
            Assert.DoesNotContain(untrusted.Commands, command => command.Name == "/status");

            var trusted = await CustomSlashCommandService.LoadAsync(user, project, includeProjectCommands: true);
            var trustedShared = Assert.Single(trusted.Commands, command => command.Name == "/shared");
            Assert.Equal("project", trustedShared.Scope);
            Assert.Equal("Project prompt", trustedShared.Prompt);
            Assert.Contains(trusted.Warnings, warning => warning.Contains("built-in", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Does_not_load_a_project_slash_command_hard_linked_outside_the_project()
    {
        if (!FileHardLinkInspector.IsSupportedPlatform) return;
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var user = Path.Combine(root, "user-commands");
        var commands = Path.Combine(project, ".codev", "commands");
        var outside = Path.Combine(root, "private-prompt.md");
        var linked = Path.Combine(commands, "private-prompt.md");
        try
        {
            Directory.CreateDirectory(commands);
            Directory.CreateDirectory(user);
            await File.WriteAllTextAsync(outside, "---\ndescription: private\n---\nexternal-only-prompt-marker");
            if (!TryCreateHardLink(outside, linked)) return;

            var result = await CustomSlashCommandService.LoadAsync(user, project, includeProjectCommands: true);

            Assert.DoesNotContain(result.Commands, command => command.Name == "/private-prompt");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Suggestion_token_supports_named_argument_entry_and_rejects_multiline_input()
    {
        Assert.True(SlashCommandCatalog.TryGetCommandToken("/inspect area=login", 19, out var token, out var hasArguments));
        Assert.Equal("/inspect", token);
        Assert.True(hasArguments);
        Assert.False(SlashCommandCatalog.TryGetCommandToken("hello /inspect", 14, out _, out _));
        Assert.False(SlashCommandCatalog.TryGetCommandToken("/inspect\nnext", 12, out _, out _));
    }

    private static SlashCommandDefinition Parse(string fileName, string markdown)
    {
        Assert.True(CustomSlashCommandService.TryParseFile(fileName, markdown, "user", out var command, out var error), error);
        return Assert.IsType<SlashCommandDefinition>(command);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "codev-slash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool TryCreateHardLink(string existingPath, string newPath)
    {
        if (OperatingSystem.IsWindows()) return CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return CreateHardLinkUnix(existingPath, newPath) == 0;
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}

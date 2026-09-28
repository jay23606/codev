using Codev;

namespace Codev.Tests;

public sealed class ReadOnlyCommandClassifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-read-only-commands", Guid.NewGuid().ToString("N"));

    public ReadOnlyCommandClassifierTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "README.md"), "project readme");
    }

    [Theory]
    [InlineData("PowerShell", "Get-Location")]
    [InlineData("PowerShell", "pwd")]
    [InlineData("PowerShell", "Get-ChildItem src")]
    [InlineData("PowerShell", "Get-Content README.md")]
    [InlineData("PowerShell", "type README.md")]
    [InlineData("bash", "pwd")]
    [InlineData("bash", "ls src")]
    [InlineData("bash", "cat README.md")]
    public void Allows_simple_inspection_commands(string shell, string command) =>
        Assert.True(ReadOnlyCommandClassifier.IsReadOnly(command, _root, shell));

    [Theory]
    [InlineData("PowerShell", "Get-Content README.md; Remove-Item README.md")]
    [InlineData("bash", "cat README.md && rm README.md")]
    [InlineData("bash", "cat README.md > out.txt")]
    [InlineData("PowerShell", "Get-Content README.md | Out-File out.txt")]
    [InlineData("bash", "sh -c cat README.md")]
    [InlineData("bash", "cat ../outside.txt")]
    [InlineData("bash", "cat /etc/passwd")]
    [InlineData("bash", "cat *.txt")]
    [InlineData("bash", "cat README.md\nrm README.md")]
    [InlineData("bash", "cat README.md\u2028rm README.md")]
    [InlineData("bash", "cat README.md !history")]
    [InlineData("bash", "cat .env")]
    [InlineData("bash", "cat README.md\\; rm README.md")]
    [InlineData("bash", "git status")]
    [InlineData("PowerShell", "Get-Content README.md -Raw")]
    public void Ambiguous_or_mutating_commands_still_require_approval(string shell, string command) =>
        Assert.False(ReadOnlyCommandClassifier.IsReadOnly(command, _root, shell));

    [Fact]
    public void Missing_files_and_unknown_shells_fail_closed()
    {
        Assert.False(ReadOnlyCommandClassifier.IsReadOnly("cat missing.txt", _root, "bash"));
        Assert.False(ReadOnlyCommandClassifier.IsReadOnly("cat README.md", _root, "custom-shell"));
    }

    [Fact]
    public void Read_only_commands_respect_secret_names_ignored_folders_and_project_exclusions()
    {
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        File.WriteAllText(Path.Combine(_root, "my-secret-config.json"), "secret");

        Assert.False(ReadOnlyCommandClassifier.IsReadOnly("cat my-secret-config.json", _root, "bash"));
        Assert.False(ReadOnlyCommandClassifier.IsReadOnly("cat package.json", _root, "bash", ["README.md", "package.json"]));
        Assert.False(ReadOnlyCommandClassifier.IsReadOnly("cat node_modules", _root, "bash"));
        Assert.False(ReadOnlyCommandClassifier.IsReadOnly("ls node_modules", _root, "bash"));
    }

    [Fact]
    public async Task Read_only_command_runs_as_bounded_file_inspection_without_a_shell()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "secret");
        File.WriteAllText(Path.Combine(_root, "visible.txt"), "visible");
        File.WriteAllText(Path.Combine(_root, "package.json"), "excluded");
        var content = await ReadOnlyCommandClassifier.ExecuteAsync("cat README.md", _root, "bash");
        var listing = await ReadOnlyCommandClassifier.ExecuteAsync("ls", _root, "bash", contextExclusions: ["package.json"]);

        Assert.Contains("project readme", content, StringComparison.Ordinal);
        Assert.Contains("visible.txt", listing, StringComparison.Ordinal);
        Assert.DoesNotContain(".env", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("package.json", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_only_file_output_is_capped()
    {
        File.WriteAllText(Path.Combine(_root, "large.md"), new string('x', 20_000));
        var output = await ReadOnlyCommandClassifier.ExecuteAsync("cat large.md", _root, "bash");

        Assert.True(output.Length < 8100);
        Assert.Contains("read-only inspection output truncated", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_only_mode_auto_approves_only_classified_commands_and_keeps_deny_rules_first()
    {
        var registry = ProjectCommandPermissionRegistry.Load(Path.Combine(_root, "permissions.json"));
        await registry.SetModeAsync(_root, ProjectCommandPermissionMode.ReadOnly);
        Assert.Equal(ProjectCommandPermissionDecision.Allow, registry.Evaluate(_root, "cat README.md", "bash"));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_root, "cat README.md", "bash", contextExclusions: ["README.md"]));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_root, "cat README.md", "bash", allowReadOnly: false));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_root, "cat README.md; rm README.md", "bash"));
        await registry.SetRuleAsync(_root, "cat README.md", ProjectCommandPermissionDecision.Deny);
        Assert.Equal(ProjectCommandPermissionDecision.Deny, registry.Evaluate(_root, "cat README.md", "bash"));
    }

    [Fact]
    public void Refuses_symlinked_targets()
    {
        var outside = Path.Combine(Path.GetTempPath(), "Codev-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        var link = Path.Combine(_root, "linked.txt");
        File.WriteAllText(outside, "outside");
        try
        {
            File.CreateSymbolicLink(link, outside);
            Assert.False(ReadOnlyCommandClassifier.IsReadOnly("cat linked.txt", _root, "bash"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Symlink creation is unavailable in some Windows configurations.
        }
        finally { File.Delete(outside); }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

using Codev;
using System.Runtime.InteropServices;
namespace Codev.Tests;

public sealed class ProjectFormatterCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-formatters", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Formatter_configuration_is_opt_in_and_project_trust_gated()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".codev"));
        await File.WriteAllTextAsync(Path.Combine(_root, ProjectFormatterCatalog.RelativeConfigPath.Replace('/', Path.DirectorySeparatorChar)), ValidJson);

        var untrusted = await ProjectFormatterCatalog.LoadAsync(_root, isTrusted: false);
        var trusted = await ProjectFormatterCatalog.LoadAsync(_root, isTrusted: true);

        Assert.Empty(untrusted.Formatters);
        Assert.Single(trusted.Formatters);
        Assert.Null(trusted.Warning);
        Assert.Null(ProjectFormatterCatalog.ForPath(trusted.Formatters, "README.md"));
        Assert.Equal("prettier", ProjectFormatterCatalog.ForPath(trusted.Formatters, "src/app.TS")!.Name);
    }

    [Fact]
    public async Task Formatter_configuration_is_rejected_when_it_is_hard_linked_outside_the_project()
    {
        if (!FileHardLinkInspector.IsSupportedPlatform) return;
        Directory.CreateDirectory(Path.Combine(_root, ".codev"));
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + "-outside.json");
        var config = Path.Combine(_root, ProjectFormatterCatalog.RelativeConfigPath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            await File.WriteAllTextAsync(outside, ValidJson);
            if (!TryCreateHardLink(outside, config)) return;

            var loaded = await ProjectFormatterCatalog.LoadAsync(_root, isTrusted: true);

            Assert.Empty(loaded.Formatters);
            Assert.NotNull(loaded.Warning);
        }
        finally
        {
            try { File.Delete(config); } catch { }
            try { File.Delete(outside); } catch { }
        }
    }

    [Fact]
    public void Formatter_arguments_keep_file_path_as_one_literal_argument()
    {
        Assert.True(ProjectFormatterCatalog.ValidateJson(ValidJson, out var formatters, out var error), error);
        var formatter = Assert.Single(formatters);
        var path = Path.Combine(_root, "folder with spaces", "app.ts");

        var startInfo = ProjectFormatterCatalog.CreateStartInfo(formatter, path, _root);

        Assert.Equal("prettier", startInfo.FileName);
        Assert.Equal(new[] { "--write", path }, startInfo.ArgumentList.ToArray());
        Assert.Contains("$FILE", ValidJson);
    }

    [Theory]
    [InlineData("{\"formatters\":[{\"name\":\"bad\",\"extensions\":[\".ts\"],\"executable\":\"prettier\",\"arguments\":[\"--write\"]}]}", "exactly one $FILE")]
    [InlineData("{\"formatters\":[{\"name\":\"bad\",\"extensions\":[\".ts\",\".TS\"],\"executable\":\"prettier\",\"arguments\":[\"$FILE\"]}]}", "unique simple extensions")]
    [InlineData("{\"formatters\":[],\"unknown\":true}", "unsupported field")]
    public void Invalid_formatter_configuration_fails_closed(string json, string expected)
    {
        Assert.False(ProjectFormatterCatalog.ValidateJson(json, out var formatters, out var error));
        Assert.Empty(formatters);
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_requires_trust_and_valid_configuration()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProjectFormatterCatalog.SaveAsync(_root, ValidJson, isTrusted: false));
        await Assert.ThrowsAsync<InvalidDataException>(() => ProjectFormatterCatalog.SaveAsync(_root, "{}", isTrusted: true));

        await ProjectFormatterCatalog.SaveAsync(_root, ValidJson, isTrusted: true);

        Assert.Equal(ValidJson, await ProjectFormatterCatalog.ReadConfigurationTextAsync(_root, isTrusted: true));
    }

    [Fact]
    public async Task Formatter_rechecks_trust_after_command_approval()
    {
        Assert.True(ProjectFormatterCatalog.ValidateJson(ValidJson, out var formatters, out var error), error);
        var formatter = Assert.Single(formatters);
        var approvalCalls = 0;

        var result = await ProjectFormatterCatalog.RunAsync(formatter, Path.Combine(_root, "test.ts"), "test.ts", _root,
            _ => { approvalCalls++; return Task.FromResult(CommandApprovalOutcome.Approved); },
            status: null, isStillTrusted: () => false);

        Assert.Equal(0, approvalCalls);
        Assert.False(result.Ran);
        Assert.Contains("trust was revoked", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Formatter_deny_rule_prevents_process_launch()
    {
        Assert.True(ProjectFormatterCatalog.ValidateJson(ValidJson, out var formatters, out var error), error);
        var result = await ProjectFormatterCatalog.RunAsync(Assert.Single(formatters), Path.Combine(_root, "missing.ts"),
            "missing.ts", _root, proposal =>
            {
                Assert.Equal("formatter process", proposal.ShellName);
                Assert.Contains("prettier", proposal.Command);
                return Task.FromResult(CommandApprovalOutcome.Denied);
            }, status: null);

        Assert.False(result.Ran);
        Assert.Contains("deny rule", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Auto_approves_formatter_without_prompt_but_exact_deny_still_blocks()
    {
        Assert.True(ProjectFormatterCatalog.ValidateJson(ValidJson, out var formatters, out var error), error);
        var formatter = Assert.Single(formatters);
        var permissions = ProjectCommandPermissionRegistry.Load(Path.Combine(_root, "permissions.json"));
        await permissions.SetModeAsync(_root, ProjectCommandPermissionMode.Auto);
        var proposal = new CodeTaskCommandProposal(ProjectFormatterCatalog.DisplayCommand(formatter, "src/app.ts"), _root, "formatter process");
        var approvalUiCalls = 0;
        var policy = new ProjectCommandApprovalPolicy(permissions);
        var auto = await policy.ApproveAsync(proposal, requestApproval: _ =>
        {
            approvalUiCalls++;
            return Task.FromResult(ProjectCommandApprovalChoice.Cancel);
        });

        Assert.Equal(CommandApprovalOutcome.Approved, auto.Outcome);
        Assert.Equal(0, approvalUiCalls);

        await permissions.SetRuleAsync(_root, proposal.Command, ProjectCommandPermissionDecision.Deny);
        var denied = await policy.ApproveAsync(proposal, requestApproval: _ =>
        {
            approvalUiCalls++;
            return Task.FromResult(ProjectCommandApprovalChoice.RunOnce);
        });

        Assert.Equal(CommandApprovalOutcome.Denied, denied.Outcome);
        Assert.Equal(0, approvalUiCalls);
    }

    private const string ValidJson = """
        {
          "formatters": [
            {
              "name": "prettier",
              "extensions": [".js", ".ts"],
              "executable": "prettier",
              "arguments": ["--write", "$FILE"]
            }
          ]
        }
        """;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
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

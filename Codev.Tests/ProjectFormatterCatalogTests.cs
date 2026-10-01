using Codev;
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
}

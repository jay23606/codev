using Codev;

namespace Codev.Tests;

public sealed class ProjectMcpToolPermissionRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-mcp-permissions", Guid.NewGuid().ToString("N"));
    private readonly string _project = Path.Combine(Path.GetTempPath(), "Codev-mcp-project", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public ProjectMcpToolPermissionRegistryTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_project);
        _path = Path.Combine(_root, "permissions.json");
    }

    [Fact]
    public void Unconfigured_and_non_auto_modes_require_approval()
    {
        var registry = ProjectMcpToolPermissionRegistry.Load(_path);
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, ProjectCommandPermissionMode.AskEveryTime, "github", "search"));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, ProjectCommandPermissionMode.ReadOnly, "github", "search"));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, ProjectCommandPermissionMode.Allowlist, "github", "search"));
    }

    [Fact]
    public async Task Auto_runs_tools_except_exact_denies_and_allowlist_matches_exact_tool()
    {
        var registry = ProjectMcpToolPermissionRegistry.Load(_path);
        Assert.Equal(ProjectCommandPermissionDecision.Allow, registry.Evaluate(_project, ProjectCommandPermissionMode.Auto, "github", "search"));
        await registry.SetRuleAsync(_project, "github", "search", ProjectCommandPermissionDecision.Deny);
        Assert.Equal(ProjectCommandPermissionDecision.Deny, registry.Evaluate(_project, ProjectCommandPermissionMode.Auto, "github", "search"));
        Assert.Equal(ProjectCommandPermissionDecision.Allow, registry.Evaluate(_project, ProjectCommandPermissionMode.Auto, "github", "create_issue"));
        Assert.Equal(ProjectCommandPermissionDecision.Deny, registry.Evaluate(_project, ProjectCommandPermissionMode.Allowlist, "github", "search"));

        await registry.RemoveRuleAsync(_project, "github", "search", ProjectCommandPermissionDecision.Deny);
        await registry.SetRuleAsync(_project, "github", "search", ProjectCommandPermissionDecision.Allow);
        Assert.Equal(ProjectCommandPermissionDecision.Allow, registry.Evaluate(_project, ProjectCommandPermissionMode.Allowlist, "github", "search"));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, ProjectCommandPermissionMode.Allowlist, "github", "create_issue"));
    }

    [Fact]
    public async Task Rules_persist_and_apply_only_to_the_same_project()
    {
        var registry = ProjectMcpToolPermissionRegistry.Load(_path);
        await registry.SetRuleAsync(_project, "github", "search", ProjectCommandPermissionDecision.Allow);
        var reloaded = ProjectMcpToolPermissionRegistry.Load(_path);

        Assert.Equal(ProjectCommandPermissionDecision.Allow, reloaded.Evaluate(_project, ProjectCommandPermissionMode.Allowlist, "github", "search"));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, reloaded.Evaluate(_project + "-other", ProjectCommandPermissionMode.Allowlist, "github", "search"));
    }

    [Fact]
    public async Task Invalid_tool_identity_is_rejected()
    {
        var registry = ProjectMcpToolPermissionRegistry.Load(_path);
        await Assert.ThrowsAsync<ArgumentException>(() => registry.SetRuleAsync(_project, "bad id", "search", ProjectCommandPermissionDecision.Allow));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.SetRuleAsync(_project, "github", "", ProjectCommandPermissionDecision.Allow));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

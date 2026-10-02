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

    [Fact]
    public async Task Concurrent_policy_reads_remain_safe_while_project_rules_are_updated()
    {
        var registry = ProjectMcpToolPermissionRegistry.Load(_path);
        var projects = Enumerable.Range(0, 64).Select(index => Path.Combine(_root, $"concurrent-{index}")).ToArray();
        using var start = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (var iteration = 0; iteration < 5_000; iteration++)
            {
                var project = projects[iteration % projects.Length];
                Assert.NotNull(registry.GetRules(project));
                var decision = registry.Evaluate(project, ProjectCommandPermissionMode.Auto, "github", "search");
                Assert.True(Enum.IsDefined(decision));
            }
        })).ToArray();
        var writer = Task.Run(async () =>
        {
            start.Set();
            for (var index = 0; index < projects.Length; index++)
                await registry.SetRuleAsync(projects[index], "github", "search",
                    index % 2 == 0 ? ProjectCommandPermissionDecision.Deny : ProjectCommandPermissionDecision.Allow);
        });

        await Task.WhenAll(readers.Append(writer));
        Assert.Equal(projects.Length, projects.Sum(project => registry.GetRules(project).Count));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

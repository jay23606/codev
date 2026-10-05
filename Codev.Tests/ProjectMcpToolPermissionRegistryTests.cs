using Codev;

namespace Codev.Tests;

public sealed class ProjectMcpToolPermissionRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-mcp-permissions", Guid.NewGuid().ToString("N"));
    private readonly string _project = Path.Combine(Path.GetTempPath(), "Codev-mcp-project", Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

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
        await registry.SetRuleAsync(_project, "github", "search", ProjectCommandPermissionDecision.Allow, Fingerprint);
        Assert.Equal(ProjectCommandPermissionDecision.Allow, registry.Evaluate(_project, ProjectCommandPermissionMode.Allowlist, "github", "search", Fingerprint));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, ProjectCommandPermissionMode.Allowlist, "github", "create_issue"));
    }

    [Fact]
    public async Task Rules_persist_and_apply_only_to_the_same_project()
    {
        var registry = ProjectMcpToolPermissionRegistry.Load(_path);
        await registry.SetRuleAsync(_project, "github", "search", ProjectCommandPermissionDecision.Allow, Fingerprint);
        var reloaded = ProjectMcpToolPermissionRegistry.Load(_path);

        Assert.Equal(ProjectCommandPermissionDecision.Allow, reloaded.Evaluate(_project, ProjectCommandPermissionMode.Allowlist, "github", "search", Fingerprint));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, reloaded.Evaluate(_project + "-other", ProjectCommandPermissionMode.Allowlist, "github", "search"));
    }

    [Fact]
    public async Task Changing_http_endpoint_with_same_server_id_and_tool_requires_a_new_allow()
    {
        var original = new McpServerConfiguration("github", "GitHub", McpServerTransportKind.Http, true,
            Url: "https://api.example.test/mcp", OAuthEnabled: false);
        var replacement = original with { Url = "https://attacker.example.test/mcp" };
        var originalFingerprint = McpServerConfigurationStore.CreatePermissionFingerprint(original);
        var replacementFingerprint = McpServerConfigurationStore.CreatePermissionFingerprint(replacement);
        var registry = ProjectMcpToolPermissionRegistry.Load(_path);

        await registry.SetRuleAsync(_project, "github", "search", ProjectCommandPermissionDecision.Allow, originalFingerprint);

        Assert.Equal(ProjectCommandPermissionDecision.Allow,
            registry.Evaluate(_project, ProjectCommandPermissionMode.Allowlist, "github", "search", originalFingerprint));
        Assert.Equal(ProjectCommandPermissionDecision.Ask,
            registry.Evaluate(_project, ProjectCommandPermissionMode.Allowlist, "github", "search", replacementFingerprint));

        await registry.SetRuleAsync(_project, "github", "search", ProjectCommandPermissionDecision.Deny);
        Assert.Equal(ProjectCommandPermissionDecision.Deny,
            registry.Evaluate(_project, ProjectCommandPermissionMode.Auto, "github", "search", replacementFingerprint));
    }

    [Fact]
    public async Task Changing_advertised_tool_schema_with_same_server_and_name_requires_a_new_allow()
    {
        using var originalSchema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"}}}""");
        using var changedSchema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"},"delete_all":{"type":"boolean"}}}""");
        var original = new McpCodeTaskTool("github_search", "github", "GitHub", "search", "Search issues",
            originalSchema.RootElement.Clone(), null, ConfigurationFingerprint: Fingerprint);
        var changed = original with { InputSchema = changedSchema.RootElement.Clone() };
        var registry = ProjectMcpToolPermissionRegistry.Load(_path);

        await registry.SetRuleAsync(_project, original.ServerId, original.ToolName, ProjectCommandPermissionDecision.Allow,
            original.PermissionFingerprint);

        Assert.NotEqual(original.PermissionFingerprint, changed.PermissionFingerprint);
        Assert.Equal(ProjectCommandPermissionDecision.Allow,
            registry.Evaluate(_project, ProjectCommandPermissionMode.Allowlist, original.ServerId, original.ToolName,
                original.PermissionFingerprint));
        Assert.Equal(ProjectCommandPermissionDecision.Ask,
            registry.Evaluate(_project, ProjectCommandPermissionMode.Allowlist, changed.ServerId, changed.ToolName,
                changed.PermissionFingerprint));
    }

    [Fact]
    public void Legacy_allow_rule_without_fingerprint_fails_closed_but_legacy_deny_still_applies()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new ProjectMcpToolPermissions(_project,
            [
                new ProjectMcpToolPermissionRule("github", "search", ProjectCommandPermissionDecision.Allow),
                new ProjectMcpToolPermissionRule("github", "delete", ProjectCommandPermissionDecision.Deny)
            ])
        });
        File.WriteAllText(_path, json);
        var registry = ProjectMcpToolPermissionRegistry.Load(_path);

        Assert.Equal(ProjectCommandPermissionDecision.Ask,
            registry.Evaluate(_project, ProjectCommandPermissionMode.Allowlist, "github", "search", Fingerprint));
        Assert.Equal(ProjectCommandPermissionDecision.Deny,
            registry.Evaluate(_project, ProjectCommandPermissionMode.Auto, "github", "delete", Fingerprint));
    }

    [Fact]
    public void Malformed_rule_fails_closed_in_auto_and_preserves_the_registry_file()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new ProjectMcpToolPermissions(_project,
            [
                new ProjectMcpToolPermissionRule("github", "search", ProjectCommandPermissionDecision.Deny),
                null!
            ])
        });
        File.WriteAllText(_path, json);

        var loaded = ProjectMcpToolPermissionRegistry.Load(_path);

        Assert.False(loaded.CanPersist);
        Assert.NotNull(loaded.LoadError);
        Assert.Equal(ProjectCommandPermissionDecision.Ask,
            loaded.Evaluate(_project, ProjectCommandPermissionMode.Auto, "github", "search"));
        Assert.Equal(json, File.ReadAllText(_path));
    }

    [Fact]
    public void Duplicate_normalized_project_entries_fail_closed_and_preserve_the_registry_file()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new ProjectMcpToolPermissions(_project,
                [new ProjectMcpToolPermissionRule("github", "search", ProjectCommandPermissionDecision.Deny)]),
            new ProjectMcpToolPermissions(Path.Combine(_project, "."), [])
        });
        File.WriteAllText(_path, json);

        var loaded = ProjectMcpToolPermissionRegistry.Load(_path);

        Assert.False(loaded.CanPersist);
        Assert.NotNull(loaded.LoadError);
        Assert.Equal(ProjectCommandPermissionDecision.Ask,
            loaded.Evaluate(_project, ProjectCommandPermissionMode.Auto, "github", "search"));
        Assert.Equal(json, File.ReadAllText(_path));
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
                    index % 2 == 0 ? ProjectCommandPermissionDecision.Deny : ProjectCommandPermissionDecision.Allow,
                    Fingerprint);
        });

        await Task.WhenAll(readers.Append(writer));
        Assert.Equal(projects.Length, projects.Sum(project => registry.GetRules(project).Count));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

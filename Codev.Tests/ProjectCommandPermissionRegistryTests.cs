using Codev;
using System.Text.Json;

namespace Codev.Tests;

public sealed class ProjectCommandPermissionRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-command-permissions", Guid.NewGuid().ToString("N"));
    private readonly string _project = Path.Combine(Path.GetTempPath(), "Codev-projects", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public ProjectCommandPermissionRegistryTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_project);
        _path = Path.Combine(_root, "permissions.json");
    }

    [Fact]
    public void Unknown_projects_and_default_mode_always_ask()
    {
        var registry = ProjectCommandPermissionRegistry.Load(_path);

        Assert.True(registry.CanPersist);
        Assert.Equal(ProjectCommandPermissionMode.AskEveryTime, registry.GetMode(_project));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, "dotnet test"));
    }

    [Fact]
    public async Task Allow_rules_only_apply_in_allowlist_mode_and_match_exact_command_text()
    {
        var registry = ProjectCommandPermissionRegistry.Load(_path);
        const string command = "dotnet test Codev.Tests\\Codev.Tests.csproj";
        await registry.SetRuleAsync(_project, command, ProjectCommandPermissionDecision.Allow);

        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, command));
        await registry.SetModeAsync(_project, ProjectCommandPermissionMode.Allowlist);
        Assert.Equal(ProjectCommandPermissionDecision.Allow, registry.Evaluate(_project, command));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, command + " --filter Fast"));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project + "-other", command));
    }

    [Fact]
    public async Task Deny_rules_take_precedence_over_allow_rules_and_all_modes()
    {
        var registry = ProjectCommandPermissionRegistry.Load(_path);
        const string command = "Get-Content README.md";
        await registry.SetRuleAsync(_project, command, ProjectCommandPermissionDecision.Allow);
        await registry.SetRuleAsync(_project, command, ProjectCommandPermissionDecision.Deny);
        await registry.SetModeAsync(_project, ProjectCommandPermissionMode.Allowlist);

        Assert.Equal(ProjectCommandPermissionDecision.Deny, registry.Evaluate(_project, command));
        Assert.Equal(2, registry.GetRules(_project).Count);
        await registry.SetModeAsync(_project, ProjectCommandPermissionMode.AskEveryTime);
        Assert.Equal(ProjectCommandPermissionDecision.Deny, registry.Evaluate(_project, command));
        await registry.RemoveRuleAsync(_project, command, ProjectCommandPermissionDecision.Deny);
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, command));
    }

    [Fact]
    public async Task Mode_and_exact_rules_persist_outside_the_project_and_reload()
    {
        var registry = ProjectCommandPermissionRegistry.Load(_path);
        await registry.SetRuleAsync(_project, "  dotnet test Codev.Tests\\Codev.Tests.csproj  ", ProjectCommandPermissionDecision.Allow);
        await registry.SetModeAsync(_project, ProjectCommandPermissionMode.Allowlist);
        var reloaded = ProjectCommandPermissionRegistry.Load(_path);

        Assert.Equal(ProjectCommandPermissionDecision.Allow, reloaded.Evaluate(_project, "dotnet test Codev.Tests\\Codev.Tests.csproj"));
        Assert.Equal("dotnet test Codev.Tests\\Codev.Tests.csproj", Assert.Single(reloaded.GetRules(_project)).Command);
        Assert.False(File.Exists(Path.Combine(_project, "command-permissions.json")));
    }

    [Fact]
    public async Task Corrupt_permission_file_fails_closed_and_is_preserved()
    {
        const string contents = "not valid json";
        await File.WriteAllTextAsync(_path, contents);
        var registry = ProjectCommandPermissionRegistry.Load(_path);

        Assert.False(registry.CanPersist);
        Assert.NotNull(registry.LoadError);
        Assert.Equal(ProjectCommandPermissionMode.AskEveryTime, registry.GetMode(_project));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, "git status"));
        Assert.Equal(contents, await File.ReadAllTextAsync(_path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.SetModeAsync(_project, ProjectCommandPermissionMode.Allowlist));
    }

    [Fact]
    public async Task Failed_persistence_does_not_leave_an_in_memory_allow_rule()
    {
        var blocker = Path.Combine(_root, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "keep");
        var registry = ProjectCommandPermissionRegistry.Load(Path.Combine(blocker, "permissions.json"));

        await Assert.ThrowsAsync<IOException>(() => registry.SetRuleAsync(_project, "dotnet test", ProjectCommandPermissionDecision.Allow,
            ProjectCommandPermissionMode.Allowlist));

        Assert.Equal(ProjectCommandPermissionMode.AskEveryTime, registry.GetMode(_project));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, "git status"));
        Assert.Equal("keep", await File.ReadAllTextAsync(blocker));
    }

    [Fact]
    public async Task Protected_git_and_codev_data_commands_never_skip_approval()
    {
        var codevDataFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "avalonia-settings.json");
        Assert.False(ProjectCommandPermissionRegistry.CanCreateAllowRule("git status --short"));
        Assert.False(ProjectCommandPermissionRegistry.CanCreateAllowRule("Remove-Item .git/config"));
        Assert.False(ProjectCommandPermissionRegistry.CanCreateAllowRule("type " + codevDataFile));
        Assert.True(ProjectCommandPermissionRegistry.CanCreateAllowRule("dotnet test"));

        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(new[]
        {
            new ProjectCommandPermissions(_project, ProjectCommandPermissionMode.Allowlist,
                [new ProjectCommandPermissionRule("git status --short", ProjectCommandPermissionDecision.Allow)])
        }));
        var loaded = ProjectCommandPermissionRegistry.Load(_path);
        Assert.Equal(ProjectCommandPermissionDecision.Ask, loaded.Evaluate(_project, "git status --short"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => loaded.SetRuleAsync(_project, "git status --short", ProjectCommandPermissionDecision.Allow));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_project, recursive: true); } catch { }
    }
}

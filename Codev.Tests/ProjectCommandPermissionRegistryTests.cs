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
    public async Task Auto_allows_commands_and_allowlist_requires_an_exact_allow_rule()
    {
        var registry = ProjectCommandPermissionRegistry.Load(_path);
        const string command = "dotnet test Codev.Tests\\Codev.Tests.csproj";
        await registry.SetRuleAsync(_project, command, ProjectCommandPermissionDecision.Allow);

        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, command));
        await registry.SetModeAsync(_project, ProjectCommandPermissionMode.Auto);
        await registry.SetRuleAsync(_project, command, ProjectCommandPermissionDecision.Allow);
        Assert.Equal(ProjectCommandPermissionMode.Auto, registry.GetMode(_project));
        Assert.Equal(ProjectCommandPermissionDecision.Allow, registry.Evaluate(_project, command));
        Assert.Equal(ProjectCommandPermissionDecision.Allow, registry.Evaluate(_project, command + " --filter Fast"));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project + "-other", command));
        await registry.SetModeAsync(_project, ProjectCommandPermissionMode.Allowlist);
        Assert.Equal(ProjectCommandPermissionDecision.Allow, registry.Evaluate(_project, command));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, command + " --filter Fast"));
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project + "-other", command));
        await registry.SetModeAsync(_project, ProjectCommandPermissionMode.ReadOnly);
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, command));
    }

    [Fact]
    public async Task Auto_mode_approves_commands_unless_the_exact_command_is_denied()
    {
        var registry = ProjectCommandPermissionRegistry.Load(_path);
        Directory.CreateDirectory(Path.Combine(_project, ".git"));
        await registry.SetModeAsync(_project, ProjectCommandPermissionMode.Auto);

        foreach (var command in new[]
        {
            "git add game.js; git commit -m update; git push origin main",
            "Remove-Item -Recurse -Force space-invaders-game/signaling; git -C space-invaders-game status --short",
            "npm test",
            "dotnet test",
            "Invoke-Expression 'arbitrary command'"
        })
            Assert.Equal(ProjectCommandPermissionDecision.Allow, registry.Evaluate(_project, command, isVerification: command.Contains("test", StringComparison.OrdinalIgnoreCase)));

        const string deniedCommand = "Remove-Item -Recurse -Force space-invaders-game/signaling";
        await registry.SetRuleAsync(_project, deniedCommand, ProjectCommandPermissionDecision.Deny);
        Assert.Equal(ProjectCommandPermissionDecision.Deny, registry.Evaluate(_project, deniedCommand));
        Assert.Equal(ProjectCommandPermissionDecision.Allow, registry.Evaluate(_project, deniedCommand + "; Get-Location"));
        await registry.SetModeAsync(_project, ProjectCommandPermissionMode.AskEveryTime);
        Assert.Equal(ProjectCommandPermissionDecision.Ask, registry.Evaluate(_project, "dotnet test"));
    }

    [Fact]
    public async Task Auto_mode_survives_reload_and_allows_the_protected_rule_command_shape()
    {
        var registry = ProjectCommandPermissionRegistry.Load(_path);
        await registry.SetModeAsync(_project, ProjectCommandPermissionMode.Auto);
        var restarted = ProjectCommandPermissionRegistry.Load(_path);
        const string command = "Remove-Item -Recurse -Force space-invaders-game/signaling; git -C space-invaders-game status --short";

        Assert.Equal(ProjectCommandPermissionMode.Auto, restarted.GetMode(_project));
        Assert.False(ProjectCommandPermissionRegistry.CanCreateAllowRule(command));
        Assert.Equal(ProjectCommandPermissionDecision.Allow, restarted.Evaluate(_project, command));
    }

    [Fact]
    public async Task Auto_mode_routes_safe_external_checks_to_shell_and_filesystem_inspections_to_bounded_apis()
    {
        var registry = ProjectCommandPermissionRegistry.Load(_path);
        await File.WriteAllTextAsync(Path.Combine(_project, "game.js"), "const x = 1;");
        await registry.SetModeAsync(_project, ProjectCommandPermissionMode.Auto);

        var shellName = OperatingSystem.IsWindows() ? "PowerShell" : "bash";
        var fileInspection = registry.Evaluate(_project, "pwd", shellName);
        var githubInspection = registry.Evaluate(_project, "gh auth status");
        var nodeSyntaxCheck = registry.Evaluate(_project, "node --check game.js", isVerification: true);
        var gitWrite = registry.Evaluate(_project, "git add game.js");

        Assert.True(registry.ShouldUseBoundedFileInspection(_project, "pwd", fileInspection, shellName: shellName));
        Assert.False(registry.ShouldUseBoundedFileInspection(_project, "gh auth status", githubInspection));
        Assert.False(registry.ShouldUseBoundedFileInspection(_project, "node --check game.js", nodeSyntaxCheck, isVerification: true));
        Assert.False(registry.ShouldUseBoundedFileInspection(_project, "git add game.js", gitWrite));

        await registry.SetModeAsync(_project, ProjectCommandPermissionMode.ReadOnly);
        var readOnlyInspection = registry.Evaluate(_project, "pwd", shellName);
        Assert.True(registry.ShouldUseBoundedFileInspection(_project, "pwd", readOnlyInspection, shellName: shellName));
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
    public async Task Protected_commands_cannot_be_saved_as_allows_but_auto_still_honors_deny_rules()
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

        await loaded.SetModeAsync(_project, ProjectCommandPermissionMode.Auto);
        Assert.Equal(ProjectCommandPermissionDecision.Allow, loaded.Evaluate(_project, "git status --short"));
        await loaded.SetRuleAsync(_project, "git status --short", ProjectCommandPermissionDecision.Deny);
        Assert.Equal(ProjectCommandPermissionDecision.Deny, loaded.Evaluate(_project, "git status --short"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_project, recursive: true); } catch { }
    }
}

using Codev;

namespace Codev.Tests;

public sealed class ProjectCommandApprovalPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-command-approval-policy", Guid.NewGuid().ToString("N"));
    private readonly string _project = Path.Combine(Path.GetTempPath(), "Codev-command-approval-project", Guid.NewGuid().ToString("N"));
    private readonly ProjectCommandPermissionRegistry _permissions;

    public ProjectCommandApprovalPolicyTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_project);
        _permissions = ProjectCommandPermissionRegistry.Load(Path.Combine(_root, "permissions.json"));
    }

    [Theory]
    [InlineData("Remove-Item -Recurse -Force signaling; git -C game add game.js; git -C game commit -m \"Update game\"; git -C game push origin main")]
    [InlineData("Remove-Item -Recurse -Force space-invaders-game/signaling; git -C space-invaders-game status --short")]
    public async Task Auto_approves_even_protected_compound_commands_without_requesting_approval(string command)
    {
        await _permissions.SetModeAsync(_project, ProjectCommandPermissionMode.Auto);
        var policy = new ProjectCommandApprovalPolicy(_permissions);
        var callsToApprovalUi = 0;
        var proposal = new CodeTaskCommandProposal(command, _project, "PowerShell");

        var result = await policy.ApproveAsync(proposal, requestApproval: _ =>
        {
            callsToApprovalUi++;
            return Task.FromResult(ProjectCommandApprovalChoice.Cancel);
        });

        Assert.Equal(CommandApprovalOutcome.Approved, result.Outcome);
        Assert.Equal(ProjectCommandPermissionMode.Auto, result.Mode);
        Assert.Contains("without approval", result.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(0, callsToApprovalUi);
    }

    [Fact]
    public async Task Auto_starts_background_commands_without_a_prompt_even_for_read_only_shell_text()
    {
        await _permissions.SetModeAsync(_project, ProjectCommandPermissionMode.Auto);
        var callsToApprovalUi = 0;
        var proposal = new CodeTaskCommandProposal("Get-Location", _project, "PowerShell", IsBackground: true);

        var result = await new ProjectCommandApprovalPolicy(_permissions).ApproveAsync(proposal, requestApproval: _ =>
        {
            callsToApprovalUi++;
            return Task.FromResult(ProjectCommandApprovalChoice.Cancel);
        });

        Assert.Equal(CommandApprovalOutcome.Approved, result.Outcome);
        Assert.DoesNotContain("bounded file APIs", result.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(0, callsToApprovalUi);
    }

    [Fact]
    public async Task Read_only_mode_rejects_background_commands_without_launching_a_shell()
    {
        await _permissions.SetModeAsync(_project, ProjectCommandPermissionMode.ReadOnly);
        var callsToApprovalUi = 0;

        var result = await new ProjectCommandApprovalPolicy(_permissions).ApproveAsync(
            new CodeTaskCommandProposal("sleep 20", _project, "PowerShell", IsBackground: true), requestApproval: _ =>
            {
                callsToApprovalUi++;
                return Task.FromResult(ProjectCommandApprovalChoice.RunOnce);
            });

        Assert.Equal(CommandApprovalOutcome.Rejected, result.Outcome);
        Assert.Contains("does not allow long-running", result.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(0, callsToApprovalUi);
    }

    [Fact]
    public async Task Exact_deny_blocks_auto_without_requesting_approval()
    {
        const string command = "dotnet test Codev.Tests/Codev.Tests.csproj";
        await _permissions.SetModeAsync(_project, ProjectCommandPermissionMode.Auto);
        await _permissions.SetRuleAsync(_project, command, ProjectCommandPermissionDecision.Deny);
        var callsToApprovalUi = 0;

        var result = await new ProjectCommandApprovalPolicy(_permissions).ApproveAsync(
            new CodeTaskCommandProposal(command, _project, "PowerShell"), requestApproval: _ =>
            {
                callsToApprovalUi++;
                return Task.FromResult(ProjectCommandApprovalChoice.RunOnce);
            });

        Assert.Equal(CommandApprovalOutcome.Denied, result.Outcome);
        Assert.Equal(0, callsToApprovalUi);
    }

    [Fact]
    public async Task Isolated_attempt_uses_attached_project_policy_and_attempt_workspace_for_inspection()
    {
        await _permissions.SetModeAsync(_project, ProjectCommandPermissionMode.Auto);
        var attempt = Path.Combine(_root, "attempt");
        Directory.CreateDirectory(attempt);
        File.WriteAllText(Path.Combine(_project, "probe.txt"), "original project marker");
        File.WriteAllText(Path.Combine(attempt, "probe.txt"), "isolated attempt marker");
        var files = new WorkspaceFileService(attempt);
        var policy = new ProjectCommandApprovalPolicy(_permissions);
        var proposals = new List<CodeTaskCommandProposal>();
        var executor = new CodeTaskToolExecutor(files, new Conversation(), _ => Task.FromResult(true), _ => Task.FromResult(false),
            permissionApproval: async proposal =>
            {
                proposals.Add(proposal);
                return (await policy.ApproveAsync(proposal, files.ContextExclusions)).Outcome;
            }, permissionProjectPath: _project);
        var command = OperatingSystem.IsWindows() ? "Get-Content probe.txt" : "cat probe.txt";

        var result = await executor.ExecuteAsync("run_command", System.Text.Json.JsonSerializer.SerializeToElement(new { command }));

        Assert.Contains("isolated attempt marker", result, StringComparison.Ordinal);
        Assert.DoesNotContain("original project marker", result, StringComparison.Ordinal);
        var proposal = Assert.Single(proposals);
        Assert.Equal(attempt, proposal.ProjectPath);
        Assert.Equal(_project, proposal.PermissionProjectPath);
        Assert.Equal(ProjectCommandPermissionMode.Auto, _permissions.GetMode(proposal.PermissionProjectPath!));

        await _permissions.SetRuleAsync(_project, command, ProjectCommandPermissionDecision.Deny);
        var denied = await executor.ExecuteAsync("run_command", System.Text.Json.JsonSerializer.SerializeToElement(new { command }));
        Assert.Contains("Denied by a saved project command permission rule", denied, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_mode_uses_the_approval_callback_and_persists_an_exact_deny()
    {
        const string command = "cargo check --manifest-path signaling/Cargo.toml";
        var callsToApprovalUi = 0;

        var result = await new ProjectCommandApprovalPolicy(_permissions).ApproveAsync(
            new CodeTaskCommandProposal(command, _project, "PowerShell", IsVerification: true),
            requestApproval: _ =>
            {
                callsToApprovalUi++;
                return Task.FromResult(ProjectCommandApprovalChoice.DenyExactCommand);
            });

        Assert.Equal(CommandApprovalOutcome.Denied, result.Outcome);
        Assert.True(result.RulesChanged);
        Assert.Equal(1, callsToApprovalUi);
        Assert.Equal(ProjectCommandPermissionDecision.Deny, _permissions.Evaluate(_project, command));
    }

    [Fact]
    public async Task Profile_one_call_approval_skips_the_ui_only_when_project_policy_asks()
    {
        var callsToApprovalUi = 0;
        var proposal = new CodeTaskCommandProposal("cargo check", _project, "PowerShell",
            ProfileApprovalSatisfied: true);

        var result = await new ProjectCommandApprovalPolicy(_permissions).ApproveAsync(proposal, requestApproval: _ =>
        {
            callsToApprovalUi++;
            return Task.FromResult(ProjectCommandApprovalChoice.Cancel);
        });

        Assert.Equal(CommandApprovalOutcome.Approved, result.Outcome);
        Assert.Equal(0, callsToApprovalUi);

        await _permissions.SetRuleAsync(_project, proposal.Command, ProjectCommandPermissionDecision.Deny);
        var denied = await new ProjectCommandApprovalPolicy(_permissions).ApproveAsync(proposal, requestApproval: _ =>
        {
            callsToApprovalUi++;
            return Task.FromResult(ProjectCommandApprovalChoice.RunOnce);
        });

        Assert.Equal(CommandApprovalOutcome.Denied, denied.Outcome);
        Assert.Equal(0, callsToApprovalUi);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_project, recursive: true); } catch { }
    }
}

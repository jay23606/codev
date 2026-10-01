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

    [Fact]
    public async Task Auto_approves_even_protected_compound_commands_without_requesting_approval()
    {
        await _permissions.SetModeAsync(_project, ProjectCommandPermissionMode.Auto);
        var policy = new ProjectCommandApprovalPolicy(_permissions);
        var callsToApprovalUi = 0;
        var proposal = new CodeTaskCommandProposal(
            "Remove-Item -Recurse -Force signaling; git -C game add game.js; git -C game commit -m \"Update game\"; git -C game push origin main",
            _project, "PowerShell");

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

namespace Codev;

/// <summary>Result of applying the selected project's command policy before presenting approval UI.</summary>
public sealed record ProjectCommandApprovalResult(CommandApprovalOutcome Outcome, ProjectCommandPermissionMode Mode,
    string StatusMessage, bool RulesChanged = false);

/// <summary>Routes command proposals through the exact policy used by the Avalonia Code task approval callback.</summary>
public sealed class ProjectCommandApprovalPolicy(ProjectCommandPermissionRegistry permissions)
{
    public async Task<ProjectCommandApprovalResult> ApproveAsync(CodeTaskCommandProposal proposal,
        IReadOnlyList<string>? contextExclusions = null,
        Func<CodeTaskCommandProposal, Task<ProjectCommandApprovalChoice>>? requestApproval = null,
        ProjectCommandPermissionMode? modeWhenUnconfigured = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var permissionProjectPath = proposal.PermissionProjectPath ?? proposal.ProjectPath;
        var mode = permissions.HasProjectSettings(permissionProjectPath)
            ? permissions.GetMode(permissionProjectPath)
            : modeWhenUnconfigured ?? ProjectCommandPermissionMode.AskEveryTime;
        // Resolve saved modes and exact rules against the trusted source project, but classify and
        // execute relative inspection commands against ProjectPath (the isolated attempt workspace).
        var decision = permissions.Evaluate(permissionProjectPath, proposal.Command, proposal.ShellName,
            allowReadOnly: false, contextExclusions: contextExclusions,
            isVerification: proposal.IsVerification, modeWhenUnconfigured: modeWhenUnconfigured);

        var readOnlyInspection = !proposal.IsVerification && !proposal.IsBackground &&
            (mode is ProjectCommandPermissionMode.Auto or ProjectCommandPermissionMode.ReadOnly) &&
            ReadOnlyCommandClassifier.IsReadOnly(proposal.Command, proposal.ProjectPath, proposal.ShellName, contextExclusions);
        if (decision == ProjectCommandPermissionDecision.Ask && readOnlyInspection)
            decision = ProjectCommandPermissionDecision.Allow;

        if (decision == ProjectCommandPermissionDecision.Deny)
            return Result(CommandApprovalOutcome.Denied, "Project command permission denied this exact command; it was not run.");

        if (proposal.IsBackground && mode == ProjectCommandPermissionMode.ReadOnly)
            return Result(CommandApprovalOutcome.Rejected, "Read-only command mode does not allow long-running shell processes.");

        if (proposal.ProfileApprovalSatisfied && decision == ProjectCommandPermissionDecision.Ask)
            return Result(CommandApprovalOutcome.Approved,
                "The selected agent profile approved this command once; the project permission mode was left unchanged.");

        if (decision == ProjectCommandPermissionDecision.Allow)
        {
            if (readOnlyInspection)
                return Result(CommandApprovalOutcome.ApprovedReadOnly,
                    mode == ProjectCommandPermissionMode.Auto
                        ? "Auto mode · recognized read-only inspection; running through bounded file APIs without launching a shell."
                        : "Recognized read-only command; running through Codev's bounded file inspection, without launching a shell.");

            return Result(CommandApprovalOutcome.Approved,
                mode == ProjectCommandPermissionMode.Auto
                    ? "Auto mode · running command without approval. Commands are unsandboxed and have your account permissions."
                    : "Exact project allowlist match; running the previously approved command.");
        }

        var choice = requestApproval is null
            ? ProjectCommandApprovalChoice.Cancel
            : await requestApproval(proposal).ConfigureAwait(false);
        switch (choice)
        {
            case ProjectCommandApprovalChoice.RunOnce:
                return Result(CommandApprovalOutcome.Approved, "Command approved once; project permissions were left unchanged.");
            case ProjectCommandApprovalChoice.AllowExactCommand:
                try
                {
                    await permissions.SetRuleAsync(permissionProjectPath, proposal.Command, ProjectCommandPermissionDecision.Allow)
                        .ConfigureAwait(false);
                    return Result(CommandApprovalOutcome.Approved,
                        "Exact command saved and approved for this run; the current permission mode was kept.", rulesChanged: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    return Result(CommandApprovalOutcome.Rejected,
                        $"Could not save the project allow rule ({ex.GetType().Name}); command was not run.");
                }
            case ProjectCommandApprovalChoice.DenyExactCommand:
                try
                {
                    await permissions.SetRuleAsync(permissionProjectPath, proposal.Command, ProjectCommandPermissionDecision.Deny)
                        .ConfigureAwait(false);
                    return Result(CommandApprovalOutcome.Denied,
                        "Exact command denied for this project; it was not run.", rulesChanged: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    return Result(CommandApprovalOutcome.Rejected,
                        $"Could not save the project deny rule ({ex.GetType().Name}); command was not run.");
                }
            default:
                return Result(CommandApprovalOutcome.Rejected, "");
        }

        ProjectCommandApprovalResult Result(CommandApprovalOutcome outcome, string message, bool rulesChanged = false) =>
            new(outcome, mode, message, rulesChanged);
    }
}

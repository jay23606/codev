namespace Codev;

/// <summary>Transcript and host-owned data returned by one isolated model run.</summary>
public sealed record BestOfNAttemptOutput(string Transcript, object? Payload = null);

/// <summary>
/// An execution result retains its private snapshot until disposed, allowing a host to review or
/// apply the selected candidate after verification without ever copying candidate files in place.
/// </summary>
public sealed class BestOfNAttemptExecutionResult : IDisposable
{
    private readonly BestOfNAttemptWorkspaceManager _workspaces;
    private bool _disposed;

    internal BestOfNAttemptExecutionResult(BestOfNAttemptWorkspaceManager workspaces,
        BestOfNAttemptSnapshot snapshot, BestOfNRunResult result)
    {
        _workspaces = workspaces;
        Snapshot = snapshot;
        Result = result;
    }

    public BestOfNAttemptSnapshot Snapshot { get; }
    public BestOfNRunResult Result { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _workspaces.Delete(Snapshot);
        _disposed = true;
    }
}

/// <summary>Connects the bounded workspace manager to sequential attempt execution and verification.</summary>
public sealed class BestOfNAttemptExecutionService(BestOfNAttemptWorkspaceManager workspaces,
    BestOfNAttemptCoordinator coordinator)
{
    public async Task<BestOfNAttemptExecutionResult> RunAsync(string projectPath, bool optedIn, int requestedAttempts,
        Func<BestOfNAttemptWorkspace, CancellationToken, Task<BestOfNAttemptOutput>> runAttempt,
        Func<BestOfNAttemptWorkspace, BestOfNAttemptOutput, CancellationToken, Task<BestOfNAttemptVerification>> verifyCandidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(runAttempt);
        ArgumentNullException.ThrowIfNull(verifyCandidate);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requestedAttempts, BestOfNAttemptCoordinator.MaximumAttempts);

        var snapshot = await workspaces.CaptureAsync(projectPath, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await coordinator.RunAsync(optedIn, requestedAttempts, snapshot.BaselineId,
                async (context, token) =>
                {
                    var workspace = await workspaces.CreateAttemptWorkspaceAsync(snapshot, context.AttemptNumber, token)
                        .ConfigureAwait(false);
                    var output = await runAttempt(workspace, token).ConfigureAwait(false);
                    return new BestOfNAttemptCandidate(context.AttemptNumber, context.BaselineId,
                        workspace.IsolationId, output.Transcript, new AttemptPayload(workspace, output));
                },
                async (candidate, token) =>
                {
                    if (candidate.Payload is not AttemptPayload payload)
                        throw new InvalidOperationException("The isolated attempt output was not retained for verification.");
                    return await verifyCandidate(payload.Workspace, payload.Output, token).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);

            return new BestOfNAttemptExecutionResult(workspaces, snapshot, result);
        }
        catch
        {
            workspaces.Delete(snapshot);
            throw;
        }
    }

    private sealed record AttemptPayload(BestOfNAttemptWorkspace Workspace, BestOfNAttemptOutput Output);
}

namespace Codev;

public sealed record BestOfNAttemptContext(int AttemptNumber, string BaselineId);

/// <summary>A completed model attempt tied to the captured baseline and its private workspace.</summary>
public sealed record BestOfNAttemptCandidate(int AttemptNumber, string BaselineId, string IsolationId,
    string Transcript, object? Payload = null);

/// <summary>Result from running the normal verification loop against one isolated candidate.</summary>
public sealed record BestOfNAttemptVerification(bool Passed, string Summary);

public sealed record BestOfNAttemptRecord(int AttemptNumber, string? IsolationId, string Transcript,
    bool? VerificationPassed, string? VerificationSummary, string? Error);

public sealed record BestOfNRunResult(int AttemptLimit, IReadOnlyList<BestOfNAttemptRecord> Attempts,
    BestOfNAttemptCandidate? Winner, string? StopReason);

/// <summary>
/// Coordinates opt-in independent model attempts. Workspace creation and B5 verification remain host
/// responsibilities; this coordinator enforces a shared baseline, distinct isolation IDs, sequential
/// execution, and selection only from candidates that passed the supplied verification callback.
/// </summary>
public sealed class BestOfNAttemptCoordinator
{
    public const int MaximumAttempts = 3;
    private static StringComparer IsolationComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public async Task<BestOfNRunResult> RunAsync(bool optedIn, int requestedAttempts, string baselineId,
        Func<BestOfNAttemptContext, CancellationToken, Task<BestOfNAttemptCandidate>> runAttempt,
        Func<BestOfNAttemptCandidate, CancellationToken, Task<BestOfNAttemptVerification>> verifyCandidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineId);
        ArgumentNullException.ThrowIfNull(runAttempt);
        ArgumentNullException.ThrowIfNull(verifyCandidate);
        if (requestedAttempts is < 1 or > MaximumAttempts)
            throw new ArgumentOutOfRangeException(nameof(requestedAttempts), $"Attempt count must be between 1 and {MaximumAttempts}.");

        var attemptLimit = optedIn ? requestedAttempts : 1;
        var records = new List<BestOfNAttemptRecord>(attemptLimit);
        var isolationIds = new HashSet<string>(IsolationComparer);
        BestOfNAttemptCandidate? winner = null;
        for (var number = 1; number <= attemptLimit; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BestOfNAttemptCandidate candidate;
            try
            {
                candidate = await runAttempt(new BestOfNAttemptContext(number, baselineId), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                records.Add(new BestOfNAttemptRecord(number, null, "", false, null,
                    $"Attempt failed ({ex.GetType().Name}): {ex.Message}"));
                continue;
            }

            if (candidate.AttemptNumber != number || !string.Equals(candidate.BaselineId, baselineId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(candidate.IsolationId) || !isolationIds.Add(candidate.IsolationId))
            {
                records.Add(new BestOfNAttemptRecord(number, candidate.IsolationId, candidate.Transcript, false, null,
                    "Attempt workspace was not demonstrably isolated from the same captured baseline; remaining attempts were stopped."));
                return new BestOfNRunResult(attemptLimit, records, null, "Isolation validation failed; no candidate may be applied.");
            }

            try
            {
                var verification = await verifyCandidate(candidate, cancellationToken).ConfigureAwait(false);
                records.Add(new BestOfNAttemptRecord(number, candidate.IsolationId, candidate.Transcript,
                    verification.Passed, verification.Summary, null));
                if (verification.Passed && winner is null) winner = candidate;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                records.Add(new BestOfNAttemptRecord(number, candidate.IsolationId, candidate.Transcript, false, null,
                    $"Verification failed to complete ({ex.GetType().Name}): {ex.Message}"));
            }
        }

        return new BestOfNRunResult(attemptLimit, records, winner, winner is null
            ? "No attempt passed verification. Keep the attempts for explicit review or continuation; apply none automatically."
            : null);
    }
}

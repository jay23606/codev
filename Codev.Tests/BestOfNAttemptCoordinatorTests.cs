using Codev;

namespace Codev.Tests;

public sealed class BestOfNAttemptCoordinatorTests
{
    [Fact]
    public async Task Without_opt_in_runs_once_even_when_more_attempts_were_requested()
    {
        var calls = 0;
        var coordinator = new BestOfNAttemptCoordinator();

        var result = await coordinator.RunAsync(false, 3, "baseline-1",
            (context, _) =>
            {
                calls++;
                return Task.FromResult(Candidate(context, "private-1", "candidate"));
            },
            (_, _) => Task.FromResult(new BestOfNAttemptVerification(true, "tests passed")));

        Assert.Equal(1, calls);
        Assert.Equal(1, result.AttemptLimit);
        Assert.Single(result.Attempts);
        Assert.Equal(1, result.Winner?.AttemptNumber);
    }

    [Fact]
    public async Task Opted_in_attempts_share_a_baseline_and_only_a_verified_candidate_wins()
    {
        var started = new List<BestOfNAttemptContext>();
        var verified = new List<int>();
        var coordinator = new BestOfNAttemptCoordinator();

        var result = await coordinator.RunAsync(true, 3, "captured-tree-sha256",
            (context, _) =>
            {
                started.Add(context);
                return Task.FromResult(Candidate(context, $"private-{context.AttemptNumber}", $"attempt {context.AttemptNumber}"));
            },
            (candidate, _) =>
            {
                verified.Add(candidate.AttemptNumber);
                return Task.FromResult(new BestOfNAttemptVerification(candidate.AttemptNumber == 2,
                    candidate.AttemptNumber == 2 ? "tests passed" : "tests failed"));
            });

        Assert.Equal([1, 2, 3], started.Select(context => context.AttemptNumber));
        Assert.All(started, context => Assert.Equal("captured-tree-sha256", context.BaselineId));
        Assert.Equal([1, 2, 3], verified);
        Assert.Equal(3, result.Attempts.Count);
        Assert.Equal(2, result.Winner?.AttemptNumber);
        Assert.Equal("private-2", result.Winner?.IsolationId);
        Assert.True(result.Attempts[0].VerificationPassed == false);
        Assert.True(result.Attempts[1].VerificationPassed == true);
    }

    [Fact]
    public async Task Reused_workspace_stops_run_and_selects_no_candidate()
    {
        var coordinator = new BestOfNAttemptCoordinator();
        var result = await coordinator.RunAsync(true, 3, "baseline",
            (context, _) => Task.FromResult(Candidate(context, "same-private-workspace", $"attempt {context.AttemptNumber}")),
            (_, _) => Task.FromResult(new BestOfNAttemptVerification(true, "tests passed")));

        Assert.Null(result.Winner);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Contains("Isolation validation failed", result.StopReason);
        Assert.Contains("remaining attempts were stopped", result.Attempts[1].Error);
    }

    [Fact]
    public async Task Workspace_identity_comparison_uses_platform_path_case_rules()
    {
        var coordinator = new BestOfNAttemptCoordinator();
        var result = await coordinator.RunAsync(true, 2, "baseline",
            (context, _) => Task.FromResult(Candidate(context,
                context.AttemptNumber == 1 ? @"C:\Temp\Codev\attempt" : @"c:\temp\codev\attempt",
                $"attempt {context.AttemptNumber}")),
            (_, _) => Task.FromResult(new BestOfNAttemptVerification(true, "passed")));

        if (OperatingSystem.IsWindows())
        {
            Assert.Null(result.Winner);
            Assert.Contains("Isolation validation failed", result.StopReason);
        }
        else
        {
            Assert.NotNull(result.Winner);
        }
    }

    [Fact]
    public async Task No_passing_attempts_are_preserved_but_none_is_selected()
    {
        var coordinator = new BestOfNAttemptCoordinator();
        var result = await coordinator.RunAsync(true, 2, "baseline",
            (context, _) => Task.FromResult(Candidate(context, $"private-{context.AttemptNumber}", $"candidate {context.AttemptNumber}")),
            (_, _) => Task.FromResult(new BestOfNAttemptVerification(false, "verification failed")));

        Assert.Null(result.Winner);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Contains("apply none automatically", result.StopReason);
        Assert.Equal(["candidate 1", "candidate 2"], result.Attempts.Select(attempt => attempt.Transcript));
    }

    [Fact]
    public async Task Invalid_baseline_identity_fails_closed_before_verification()
    {
        var verificationCalled = false;
        var coordinator = new BestOfNAttemptCoordinator();
        var result = await coordinator.RunAsync(true, 2, "captured-baseline",
            (context, _) => Task.FromResult(new BestOfNAttemptCandidate(context.AttemptNumber, "different-baseline",
                $"private-{context.AttemptNumber}", "untrusted candidate")),
            (_, _) => { verificationCalled = true; return Task.FromResult(new BestOfNAttemptVerification(true, "passed")); });

        Assert.False(verificationCalled);
        Assert.Null(result.Winner);
        Assert.Single(result.Attempts);
        Assert.Contains("same captured baseline", result.Attempts[0].Error);
    }

    private static BestOfNAttemptCandidate Candidate(BestOfNAttemptContext context, string isolationId, string transcript) =>
        new(context.AttemptNumber, context.BaselineId, isolationId, transcript);
}

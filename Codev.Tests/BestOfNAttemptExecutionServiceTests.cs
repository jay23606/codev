using Codev;

namespace Codev.Tests;

public sealed class BestOfNAttemptExecutionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-best-of-n-service", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Runs_fresh_isolated_workspaces_retains_transcripts_and_keeps_only_verified_winner()
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "input.txt"), "baseline");
        var manager = new BestOfNAttemptWorkspaceManager(Path.Combine(_root, "appdata"));
        var service = new BestOfNAttemptExecutionService(manager, new BestOfNAttemptCoordinator());
        var observedPaths = new List<string>();

        using var execution = await service.RunAsync(project, optedIn: true, requestedAttempts: 3,
            async (workspace, token) =>
            {
                observedPaths.Add(workspace.WorkspacePath);
                Assert.Equal("baseline", await File.ReadAllTextAsync(Path.Combine(workspace.WorkspacePath, "input.txt"), token));
                await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "output.txt"), $"result-{workspace.AttemptNumber}", token);
                return new BestOfNAttemptOutput($"transcript-{workspace.AttemptNumber}");
            }, async (workspace, output, token) =>
            {
                var result = await File.ReadAllTextAsync(Path.Combine(workspace.WorkspacePath, "output.txt"), token);
                return new BestOfNAttemptVerification(result == "result-2", $"checked {result}; {output.Transcript}");
            });

        Assert.Equal(3, execution.Result.AttemptLimit);
        Assert.Equal(3, execution.Result.Attempts.Count);
        Assert.Equal(3, observedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(new[] { "transcript-1", "transcript-2", "transcript-3" }, execution.Result.Attempts.Select(attempt => attempt.Transcript));
        Assert.Equal(new bool?[] { false, true, false }, execution.Result.Attempts.Select(attempt => attempt.VerificationPassed));
        Assert.Equal(observedPaths[1], execution.Result.Winner!.IsolationId);
        Assert.Equal("baseline", await File.ReadAllTextAsync(Path.Combine(project, "input.txt")));
        Assert.False(File.Exists(Path.Combine(project, "output.txt")));
        Assert.True(Directory.Exists(execution.Snapshot.RootPath));

        var snapshotRoot = execution.Snapshot.RootPath;
        execution.Dispose();
        Assert.False(Directory.Exists(snapshotRoot));
    }

    [Fact]
    public async Task Opt_out_forces_one_attempt_even_when_a_higher_count_was_requested()
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "input.txt"), "baseline");
        var service = new BestOfNAttemptExecutionService(
            new BestOfNAttemptWorkspaceManager(Path.Combine(_root, "appdata")), new BestOfNAttemptCoordinator());
        var attempts = 0;

        using var execution = await service.RunAsync(project, optedIn: false, requestedAttempts: 3,
            (workspace, _) =>
            {
                attempts++;
                return Task.FromResult(new BestOfNAttemptOutput("single attempt"));
            }, (_, _, _) => Task.FromResult(new BestOfNAttemptVerification(false, "not verified")));

        Assert.Equal(1, attempts);
        Assert.Equal(1, execution.Result.AttemptLimit);
        Assert.Single(execution.Result.Attempts);
        Assert.Null(execution.Result.Winner);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}

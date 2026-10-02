using Codev;

namespace Codev.Tests;

public sealed class BestOfNAttemptWorkspaceManagerTests
{
    [Fact]
    public async Task Attempts_are_independent_copies_of_one_captured_uncommitted_project_tree()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-best-of-n-tests", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var appData = Path.Combine(root, "appdata");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Directory.CreateDirectory(Path.Combine(project, "src", "empty"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(project, ".git", "private-index"), "repository internals");
            await File.WriteAllTextAsync(Path.Combine(project, "src", "tracked.cs"), "committed version");
            await File.WriteAllTextAsync(Path.Combine(project, "untracked.txt"), "uncommitted version");
            var manager = new BestOfNAttemptWorkspaceManager(appData);
            var snapshot = await manager.CaptureAsync(project);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(snapshot.RootPath));

            await File.WriteAllTextAsync(Path.Combine(project, "untracked.txt"), "mutated after snapshot");
            var first = await manager.CreateAttemptWorkspaceAsync(snapshot, 1);
            var second = await manager.CreateAttemptWorkspaceAsync(snapshot, 2);

            Assert.Equal(snapshot.BaselineId, first.BaselineId);
            Assert.Equal(snapshot.BaselineId, second.BaselineId);
            Assert.NotEqual(first.IsolationId, second.IsolationId);
            Assert.Equal("uncommitted version", await File.ReadAllTextAsync(Path.Combine(first.WorkspacePath, "untracked.txt")));
            Assert.Equal("uncommitted version", await File.ReadAllTextAsync(Path.Combine(second.WorkspacePath, "untracked.txt")));
            Assert.True(Directory.Exists(Path.Combine(first.WorkspacePath, "src", "empty")));
            Assert.False(File.Exists(Path.Combine(first.WorkspacePath, ".git", "private-index")));

            await File.WriteAllTextAsync(Path.Combine(first.WorkspacePath, "untracked.txt"), "attempt one edit");
            Assert.Equal("uncommitted version", await File.ReadAllTextAsync(Path.Combine(second.WorkspacePath, "untracked.txt")));
            Assert.Equal("uncommitted version", await File.ReadAllTextAsync(Path.Combine(snapshot.RootPath, "baseline", "untracked.txt")));

            manager.Delete(snapshot);
            Assert.False(Directory.Exists(snapshot.RootPath));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Baseline_mutation_is_detected_before_any_attempt_is_created()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-best-of-n-tests", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(project, "source.txt"), "original");
            var manager = new BestOfNAttemptWorkspaceManager(Path.Combine(root, "appdata"));
            var snapshot = await manager.CaptureAsync(project);
            await File.WriteAllTextAsync(Path.Combine(snapshot.RootPath, "baseline", "source.txt"), "tampered");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateAttemptWorkspaceAsync(snapshot, 1));

            Assert.Contains("baseline changed", error.Message);
            Assert.False(Directory.Exists(Path.Combine(snapshot.RootPath, "attempt-1")));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Capture_refuses_symbolic_links_instead_of_copying_outside_the_project()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-best-of-n-tests", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "outside project");
            try { Directory.CreateSymbolicLink(Path.Combine(project, "linked"), outside); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return; // This OS/account does not permit creating test symlinks.
            }

            var manager = new BestOfNAttemptWorkspaceManager(Path.Combine(root, "appdata"));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CaptureAsync(project));
            Assert.Contains("symbolic link or reparse point", error.Message);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}

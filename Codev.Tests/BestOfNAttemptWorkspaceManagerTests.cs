using System.Diagnostics;
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

    [Fact]
    public async Task Winner_review_returns_only_changed_supported_source_files_and_ignores_build_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-best-of-n-tests", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        Directory.CreateDirectory(Path.Combine(project, "bin"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(project, "src", "Main.cs"), "class Main { }\n");
            await File.WriteAllBytesAsync(Path.Combine(project, "bin", "original.dll"), [0, 1, 2, 3]);
            var manager = new BestOfNAttemptWorkspaceManager(Path.Combine(root, "appdata"));
            var snapshot = await manager.CaptureAsync(project);
            var workspace = await manager.CreateAttemptWorkspaceAsync(snapshot, 1);
            await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "src", "Main.cs"), "class Main { static void Run() { } }\n");
            await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "src", "New.cs"), "class New { }\n");
            await File.WriteAllBytesAsync(Path.Combine(workspace.WorkspacePath, "bin", "generated.dll"), [4, 5, 6, 7]);

            var review = await manager.ReviewChangesAsync(snapshot, workspace, new WorkspaceFileService(project));

            Assert.True(review.CanApply, string.Join(Environment.NewLine, review.BlockingReasons));
            Assert.Equal(2, review.Proposals.Count);
            Assert.Contains(review.Proposals, proposal => proposal.RelativePath == "src/Main.cs" && proposal.Before.Contains("class Main") && proposal.After.Contains("Run"));
            Assert.Contains(review.Proposals, proposal => proposal.RelativePath == "src/New.cs" && proposal.IsNewFile);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Winner_review_fails_closed_for_deletions_and_unsupported_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-best-of-n-tests", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(project, "Main.cs"), "class Main { }\n");
            var manager = new BestOfNAttemptWorkspaceManager(Path.Combine(root, "appdata"));
            var snapshot = await manager.CaptureAsync(project);
            var workspace = await manager.CreateAttemptWorkspaceAsync(snapshot, 1);
            File.Delete(Path.Combine(workspace.WorkspacePath, "Main.cs"));
            await File.WriteAllBytesAsync(Path.Combine(workspace.WorkspacePath, "tool.bin"), [0, 1, 2]);

            var review = await manager.ReviewChangesAsync(snapshot, workspace, new WorkspaceFileService(project));

            Assert.False(review.CanApply);
            Assert.Empty(review.Proposals);
            Assert.Contains(review.BlockingReasons, reason => reason.Contains("File deletion"));
            Assert.Contains(review.BlockingReasons, reason => reason.Contains("excluded from reviewed source-file writes"));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Winner_review_refuses_to_overwrite_a_file_changed_after_snapshot_capture()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-best-of-n-tests", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        try
        {
            var originalFile = Path.Combine(project, "Main.cs");
            await File.WriteAllTextAsync(originalFile, "class Main { }\n");
            var manager = new BestOfNAttemptWorkspaceManager(Path.Combine(root, "appdata"));
            var snapshot = await manager.CaptureAsync(project);
            var workspace = await manager.CreateAttemptWorkspaceAsync(snapshot, 1);
            await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "Main.cs"), "class Main { void Attempt() {} }\n");
            await File.WriteAllTextAsync(originalFile, "class Main { void UserEdit() {} }\n");

            var review = await manager.ReviewChangesAsync(snapshot, workspace, new WorkspaceFileService(project));

            Assert.False(review.CanApply);
            Assert.Empty(review.Proposals);
            Assert.Contains(review.BlockingReasons, reason => reason.Contains("Original file changed"));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Git_attempt_context_is_private_and_uses_the_snapshot_worktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-best-of-n-git-tests", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "git project");
        Directory.CreateDirectory(project);
        try
        {
            await RunGitAsync(project, "init", "--quiet", "--initial-branch=main");
            await RunGitAsync(project, "config", "user.name", "Codev Test");
            await RunGitAsync(project, "config", "user.email", "codev-test@example.invalid");
            var file = Path.Combine(project, "Program.cs");
            await File.WriteAllTextAsync(file, "class Before {}\n");
            await RunGitAsync(project, "add", "Program.cs");
            await RunGitAsync(project, "commit", "--quiet", "-m", "baseline");
            await File.WriteAllTextAsync(file, "class ExistingUserChange {}\n");
            var originalStatus = await RunGitAsync(project, "status", "--short");
            var manager = new BestOfNAttemptWorkspaceManager(Path.Combine(root, "appdata"));
            var snapshot = await manager.CaptureAsync(project);

            try
            {
                var workspace = await manager.CreateAttemptWorkspaceAsync(snapshot, 1);
                Assert.True(Directory.Exists(Path.Combine(workspace.WorkspacePath, ".git")));
                Assert.Equal(Path.GetFullPath(workspace.WorkspacePath), Path.GetFullPath(
                    (await RunGitAsync(workspace.WorkspacePath, "rev-parse", "--show-toplevel")).Trim()));
                Assert.Contains("Program.cs", await RunGitAsync(workspace.WorkspacePath, "status", "--short"), StringComparison.Ordinal);
                Assert.Empty(await RunGitAsync(workspace.WorkspacePath, "remote", "-v"));

                await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "Program.cs"), "class AttemptChange {}\n");
                Assert.Contains("AttemptChange", await RunGitAsync(workspace.WorkspacePath, "diff", "--", "Program.cs"), StringComparison.Ordinal);
                await RunGitAsync(workspace.WorkspacePath, "branch", "attempt-only");

                Assert.Equal(originalStatus, await RunGitAsync(project, "status", "--short"));
                Assert.DoesNotContain("attempt-only", await RunGitAsync(project, "branch", "--list"), StringComparison.Ordinal);
            }
            finally { manager.Delete(snapshot); }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git failed to start in test.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr}");
        return stdout;
    }
}

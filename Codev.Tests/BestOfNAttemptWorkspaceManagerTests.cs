using System.Diagnostics;
using System.Runtime.InteropServices;
using Codev;

namespace Codev.Tests;

public sealed class BestOfNAttemptWorkspaceManagerTests
{
    [Fact]
    public async Task Capture_rejects_a_project_file_hard_linked_outside_the_project()
    {
        if (!FileHardLinkInspector.IsSupportedPlatform) return;
        var root = Path.Combine(Path.GetTempPath(), "codev-best-of-n-hardlink-tests", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var external = Path.Combine(root, "outside.cs");
        var linked = Path.Combine(project, "linked.cs");
        Directory.CreateDirectory(project);
        try
        {
            await File.WriteAllTextAsync(external, "outside-private-marker");
            if (!TryCreateHardLink(external, linked)) return;
            var manager = new BestOfNAttemptWorkspaceManager(Path.Combine(root, "appdata"));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.CaptureAsync(project));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

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
    public async Task Snapshot_and_attempt_workspaces_exclude_files_that_Codev_classifies_as_sensitive()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-best-of-n-sensitive-tests", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        BestOfNAttemptSnapshot? snapshot = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(project, "secrets"));
            await File.WriteAllTextAsync(Path.Combine(project, ".env"), "API_KEY=must-not-be-copied");
            await File.WriteAllTextAsync(Path.Combine(project, "api-credential.json"), "{\"token\":\"must-not-be-copied\"}");
            await File.WriteAllTextAsync(Path.Combine(project, "id_ed25519"), "private-key-material");
            await File.WriteAllTextAsync(Path.Combine(project, "secrets", "config.json"), "{\"token\":\"nested-secret\"}");
            await File.WriteAllTextAsync(Path.Combine(project, "settings.json"), "{\"theme\":\"dark\"}");
            var manager = new BestOfNAttemptWorkspaceManager(Path.Combine(root, "appdata"));

            snapshot = await manager.CaptureAsync(project);
            await File.WriteAllTextAsync(Path.Combine(project, ".env"), "API_KEY=rotated-after-capture");
            var workspace = await manager.CreateAttemptWorkspaceAsync(snapshot, 1);

            foreach (var relativePath in new[] { ".env", "api-credential.json", "id_ed25519" })
            {
                Assert.False(File.Exists(Path.Combine(snapshot.RootPath, "baseline", relativePath)));
                Assert.False(File.Exists(Path.Combine(workspace.WorkspacePath, relativePath)));
            }
            Assert.False(Directory.Exists(Path.Combine(snapshot.RootPath, "baseline", "secrets")));
            Assert.False(File.Exists(Path.Combine(workspace.WorkspacePath, "secrets", "config.json")));
            Assert.Equal("{\"theme\":\"dark\"}", await File.ReadAllTextAsync(Path.Combine(workspace.WorkspacePath, "settings.json")));
        }
        finally
        {
            if (snapshot is not null) new BestOfNAttemptWorkspaceManager(Path.Combine(root, "appdata")).Delete(snapshot);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Delete_removes_snapshot_containing_read_only_files_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "codev-best-of-n-readonly-tests", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var sourceFile = Path.Combine(project, "readonly.txt");
        Directory.CreateDirectory(project);
        BestOfNAttemptSnapshot? snapshot = null;
        try
        {
            await File.WriteAllTextAsync(sourceFile, "preserve this attribute in the snapshot");
            File.SetAttributes(sourceFile, File.GetAttributes(sourceFile) | FileAttributes.ReadOnly);
            var manager = new BestOfNAttemptWorkspaceManager(Path.Combine(root, "appdata"));
            snapshot = await manager.CaptureAsync(project);
            var capturedFile = Path.Combine(snapshot.RootPath, "baseline", "readonly.txt");
            Assert.True((File.GetAttributes(capturedFile) & FileAttributes.ReadOnly) != 0);

            manager.Delete(snapshot);

            Assert.False(Directory.Exists(snapshot.RootPath));
        }
        finally
        {
            if (File.Exists(sourceFile)) File.SetAttributes(sourceFile, FileAttributes.Normal);
            if (snapshot is not null && Directory.Exists(snapshot.RootPath))
            {
                foreach (var file in Directory.EnumerateFiles(snapshot.RootPath, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
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
    public async Task Workspace_reads_writes_and_winner_review_use_the_same_canonical_local_app_data_boundary()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Codev.Tests", "best-of-n-redirected-root", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var manager = new BestOfNAttemptWorkspaceManager(Path.Combine(root, "appdata"));
        BestOfNAttemptSnapshot? snapshot = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(project, "sum.js"), "function add(a, b) { return a - b; }\n");
            snapshot = await manager.CaptureAsync(project);
            var workspace = await manager.CreateAttemptWorkspaceAsync(snapshot, 1);
            await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "sum.js"),
                "function add(a, b) { return a + b; }\n");

            var files = new WorkspaceFileService(project);
            var review = await manager.ReviewChangesAsync(snapshot, workspace, files);

            Assert.True(review.CanApply, string.Join(Environment.NewLine, review.BlockingReasons));
            Assert.Contains(review.Proposals, proposal => proposal.RelativePath == "sum.js" &&
                proposal.After.Contains("return a + b", StringComparison.Ordinal));
            Assert.Contains(files.ListFiles(), path => path.EndsWith("sum.js", StringComparison.OrdinalIgnoreCase));
            await files.WriteFileAtomicAsync("sum.js", "function add(a, b) { return a + b; }\n");
            Assert.Contains("return a + b", (await files.ReadFileSnapshotAsync("sum.js")).Content, StringComparison.Ordinal);
        }
        finally
        {
            if (snapshot is not null) manager.Delete(snapshot);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
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

    [Fact]
    public async Task Git_attempt_context_is_private_when_source_is_a_linked_worktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-best-of-n-worktree-git-tests", Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "repository");
        var project = Path.Combine(root, "project worktree");
        Directory.CreateDirectory(repository);
        try
        {
            await RunGitAsync(repository, "init", "--quiet", "--initial-branch=main");
            await RunGitAsync(repository, "config", "user.name", "Codev Test");
            await RunGitAsync(repository, "config", "user.email", "codev-test@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(repository, "Program.cs"), "class Baseline {}\n");
            await RunGitAsync(repository, "add", "Program.cs");
            await RunGitAsync(repository, "commit", "--quiet", "-m", "baseline");
            await RunGitAsync(repository, "worktree", "add", "--quiet", "--detach", project, "HEAD");

            var manager = new BestOfNAttemptWorkspaceManager(Path.Combine(root, "appdata"));
            var snapshot = await manager.CaptureAsync(project);
            try
            {
                var workspace = await manager.CreateAttemptWorkspaceAsync(snapshot, 1);

                Assert.Equal(Path.GetFullPath(workspace.WorkspacePath), Path.GetFullPath(
                    (await RunGitAsync(workspace.WorkspacePath, "rev-parse", "--show-toplevel")).Trim()));
                Assert.True(string.IsNullOrWhiteSpace(
                    await RunGitAsync(workspace.WorkspacePath, "rev-parse", "--show-prefix")));
                Assert.Empty(await RunGitAsync(workspace.WorkspacePath, "remote", "-v"));
                await File.WriteAllTextAsync(Path.Combine(workspace.WorkspacePath, "Program.cs"), "class AttemptOnly {}\n");
                Assert.Contains("AttemptOnly", await RunGitAsync(workspace.WorkspacePath, "diff", "--", "Program.cs"), StringComparison.Ordinal);
                Assert.DoesNotContain("AttemptOnly", await File.ReadAllTextAsync(Path.Combine(project, "Program.cs")), StringComparison.Ordinal);
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

    private static bool TryCreateHardLink(string existingPath, string newPath)
    {
        if (OperatingSystem.IsWindows()) return CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return CreateHardLinkUnix(existingPath, newPath) == 0;
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}

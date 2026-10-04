using System.Diagnostics;
using Codev;

namespace Codev.Tests;

public sealed class GitChildWorktreeManagerTests
{
    [Fact]
    public async Task Creates_a_child_worktree_on_a_new_branch_without_copying_uncommitted_source_changes()
    {
        var temp = Path.Combine(Path.GetTempPath(), "codev-child-worktree-tests", Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(temp, "repo");
        var appData = Path.Combine(temp, "appdata");
        Directory.CreateDirectory(repo);
        try
        {
            await RunGitAsync(repo, "init", "-b", "main");
            await RunGitAsync(repo, "config", "user.name", "Codev Tests");
            await RunGitAsync(repo, "config", "user.email", "codev-tests@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "committed\n");
            await RunGitAsync(repo, "add", "--", "tracked.txt");
            await RunGitAsync(repo, "commit", "-m", "initial");
            await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "local edit\n");

            var parentId = Guid.NewGuid();
            var childId = Guid.NewGuid();
            var manager = new GitChildWorktreeManager(appData);
            var child = await manager.CreateAsync(repo, parentId, childId);

            Assert.Equal(parentId, child.ParentConversationId);
            Assert.Equal(childId, child.ChildConversationId);
            Assert.StartsWith("codev/child-", child.Branch, StringComparison.Ordinal);
            Assert.True(manager.IsManagedWorktreePath(child.WorktreePath));
            Assert.Equal("committed\n", Normalize(await File.ReadAllTextAsync(Path.Combine(child.WorktreePath, "tracked.txt"))));
            Assert.Equal("local edit\n", Normalize(await File.ReadAllTextAsync(Path.Combine(repo, "tracked.txt"))));
            Assert.Equal(child.Branch, (await RunGitAsync(child.WorktreePath, "branch", "--show-current")).Trim());
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Creates_concurrent_child_worktrees_with_independent_branches_and_files()
    {
        var temp = Path.Combine(Path.GetTempPath(), "codev-child-worktree-tests", Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(temp, "repo");
        var appData = Path.Combine(temp, "appdata");
        Directory.CreateDirectory(repo);
        try
        {
            await InitializeRepositoryAsync(repo);
            var manager = new GitChildWorktreeManager(appData);
            var parentId = Guid.NewGuid();
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();

            var children = await Task.WhenAll(
                manager.CreateAsync(repo, parentId, firstId),
                manager.CreateAsync(repo, parentId, secondId));

            Assert.Equal(2, children.Select(child => child.WorktreePath).Distinct().Count());
            Assert.Equal(2, children.Select(child => child.Branch).Distinct(StringComparer.Ordinal).Count());
            Assert.All(children, child =>
            {
                Assert.True(manager.IsManagedWorktreePath(child.WorktreePath));
                Assert.Equal(parentId, child.ParentConversationId);
                Assert.Contains(child.Branch, new[] { "codev/child-" + firstId.ToString("N"), "codev/child-" + secondId.ToString("N") });
            });

            await File.WriteAllTextAsync(Path.Combine(children[0].WorktreePath, "first.txt"), "first child\n");
            await File.WriteAllTextAsync(Path.Combine(children[1].WorktreePath, "second.txt"), "second child\n");

            Assert.True(File.Exists(Path.Combine(children[0].WorktreePath, "first.txt")));
            Assert.False(File.Exists(Path.Combine(children[0].WorktreePath, "second.txt")));
            Assert.True(File.Exists(Path.Combine(children[1].WorktreePath, "second.txt")));
            Assert.False(File.Exists(Path.Combine(children[1].WorktreePath, "first.txt")));
            Assert.False(File.Exists(Path.Combine(repo, "first.txt")));
            Assert.False(File.Exists(Path.Combine(repo, "second.txt")));
            await RunGitAsync(children[0].WorktreePath, "add", "--", "first.txt");
            await RunGitAsync(children[0].WorktreePath, "commit", "-m", "first child change");
            await RunGitAsync(children[1].WorktreePath, "add", "--", "second.txt");
            await RunGitAsync(children[1].WorktreePath, "commit", "-m", "second child change");
            foreach (var child in children) Directory.Delete(child.WorktreePath, recursive: true);

            var recovered = await Task.WhenAll(children.Select(child => manager.RecoverAsync(repo, parentId,
                child.ChildConversationId, child.Branch, child.StartCommit)));

            Assert.All(recovered, child => Assert.True(manager.IsManagedWorktreePath(child.WorktreePath)));
            Assert.Equal("first child\n", Normalize(await File.ReadAllTextAsync(Path.Combine(recovered[0].WorktreePath, "first.txt"))));
            Assert.Equal("second child\n", Normalize(await File.ReadAllTextAsync(Path.Combine(recovered[1].WorktreePath, "second.txt"))));
            Assert.False((await new GitRepositoryService(repo).GetStatusAsync()).HasChanges);
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Child_worktree_creation_and_recovery_skip_repository_hooks_and_smudge_filters()
    {
        var temp = Path.Combine(Path.GetTempPath(), "codev-child-worktree-tests", Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(temp, "repo");
        Directory.CreateDirectory(repo);
        try
        {
            await RunGitAsync(repo, "init", "-b", "main");
            await RunGitAsync(repo, "config", "user.name", "Codev Tests");
            await RunGitAsync(repo, "config", "user.email", "codev-tests@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(repo, ".gitattributes"), "tracked.txt filter=codevtest\n");
            await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "committed\n");
            await RunGitAsync(repo, "config", "filter.codevtest.smudge", "printf 'filter ran\\n' > smudge-filter-ran; cat");
            await RunGitAsync(repo, "add", "--", ".gitattributes", "tracked.txt");
            await RunGitAsync(repo, "commit", "-m", "initial");
            var hooks = Path.Combine(repo, ".git", "hooks");
            Directory.CreateDirectory(hooks);
            await File.WriteAllTextAsync(Path.Combine(hooks, "post-checkout"), "#!/bin/sh\nprintf 'hook ran\\n' > post-checkout-hook-ran\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path.Combine(hooks, "post-checkout"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var parentId = Guid.NewGuid();
            var childId = Guid.NewGuid();
            var manager = new GitChildWorktreeManager(Path.Combine(temp, "appdata"));
            var child = await manager.CreateAsync(repo, parentId, childId);

            Assert.Contains("codevtest", child.DisabledFilters!);
            Assert.False(File.Exists(Path.Combine(child.WorktreePath, "post-checkout-hook-ran")));
            Assert.False(File.Exists(Path.Combine(child.WorktreePath, "smudge-filter-ran")));
            Assert.Equal("committed\n", Normalize(await File.ReadAllTextAsync(Path.Combine(child.WorktreePath, "tracked.txt"))));
            Directory.Delete(child.WorktreePath, recursive: true);
            var recovered = await manager.RecoverAsync(repo, parentId, childId, child.Branch, child.StartCommit);
            Assert.Contains("codevtest", recovered.DisabledFilters!);
            Assert.False(File.Exists(Path.Combine(recovered.WorktreePath, "post-checkout-hook-ran")));
            Assert.False(File.Exists(Path.Combine(recovered.WorktreePath, "smudge-filter-ran")));
            Assert.Equal("committed\n", Normalize(await File.ReadAllTextAsync(Path.Combine(recovered.WorktreePath, "tracked.txt"))));
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Review_does_not_run_repository_fsmonitor_or_textconv_helpers()
    {
        var temp = Path.Combine(Path.GetTempPath(), "codev-child-worktree-tests", Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(temp, "repo");
        var appData = Path.Combine(temp, "appdata");
        var marker = Path.Combine(temp, "review-helper-ran.txt");
        var helper = Path.Combine(temp, "review-helper.cjs");
        Directory.CreateDirectory(repo);
        try
        {
            await RunGitAsync(repo, "init", "-b", "main");
            await RunGitAsync(repo, "config", "user.name", "Codev Tests");
            await RunGitAsync(repo, "config", "user.email", "codev-tests@example.invalid");
            var helperSource = "require('fs').writeFileSync('" + marker.Replace('\\', '/') + "', 'executed'); " +
                "process.stdout.write(require('fs').readFileSync(process.argv[2]));";
            await File.WriteAllTextAsync(helper, helperSource);
            var helperPath = helper.Replace('\\', '/');
            await RunGitAsync(repo, "config", "core.fsmonitor", $"node \"{helperPath}\"");
            await RunGitAsync(repo, "config", "diff.codev-audit.textconv", $"node \"{helperPath}\"");
            await File.WriteAllTextAsync(Path.Combine(repo, ".gitattributes"), "fixture.txt diff=codev-audit\n");
            await File.WriteAllTextAsync(Path.Combine(repo, "fixture.txt"), "baseline\n");
            await RunGitAsync(repo, "add", "--", ".gitattributes", "fixture.txt");
            await RunGitAsync(repo, "commit", "-m", "initial");

            var manager = new GitChildWorktreeManager(appData);
            var child = await manager.CreateAsync(repo, Guid.NewGuid(), Guid.NewGuid());
            await File.WriteAllTextAsync(Path.Combine(child.WorktreePath, "fixture.txt"), "child change\n");
            await RunGitAsync(child.WorktreePath, "add", "--", "fixture.txt");
            await RunGitAsync(child.WorktreePath, "commit", "-m", "child change");
            File.Delete(marker);

            var review = await manager.GetReviewAsync(repo, child.Branch, child.StartCommit);

            Assert.False(File.Exists(marker));
            Assert.Contains("child change", review.Diff, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { } }
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public async Task Rejects_non_git_folders_without_creating_a_child_checkout()
    {
        var temp = Path.Combine(Path.GetTempPath(), "codev-child-worktree-tests", Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(temp, "not-a-repo");
        var appData = Path.Combine(temp, "appdata");
        Directory.CreateDirectory(repo);
        try
        {
            var manager = new GitChildWorktreeManager(appData);
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateAsync(repo, Guid.NewGuid(), Guid.NewGuid()));
            Assert.False(Directory.Exists(Path.Combine(appData, "Codev", "child-worktrees")));
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Reviews_and_merges_a_committed_child_branch_into_the_clean_parent_branch()
    {
        var temp = Path.Combine(Path.GetTempPath(), "codev-child-worktree-tests", Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(temp, "repo");
        var appData = Path.Combine(temp, "appdata");
        Directory.CreateDirectory(repo);
        try
        {
            await InitializeRepositoryAsync(repo);
            var manager = new GitChildWorktreeManager(appData);
            var child = await manager.CreateAsync(repo, Guid.NewGuid(), Guid.NewGuid());
            await File.WriteAllTextAsync(Path.Combine(child.WorktreePath, "child.txt"), "child change\n");
            await RunGitAsync(child.WorktreePath, "add", "--", "child.txt");
            await RunGitAsync(child.WorktreePath, "commit", "-m", "child change");

            var review = await manager.GetReviewAsync(repo, child.Branch, child.StartCommit);
            Assert.Contains("child.txt", review.Files);
            Assert.Contains("child change", review.Diff, StringComparison.Ordinal);
            Assert.False(review.HasUncommittedChanges);

            await RunGitAsync(repo, "branch", "review-twin");
            await RunGitAsync(repo, "switch", "review-twin");
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.MergeAsync(repo, review));
            await RunGitAsync(repo, "switch", "main");

            await File.AppendAllTextAsync(Path.Combine(child.WorktreePath, "child.txt"), "changed after review\n");
            await RunGitAsync(child.WorktreePath, "add", "--", "child.txt");
            await RunGitAsync(child.WorktreePath, "commit", "-m", "change after review");
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.MergeAsync(repo, review));
            review = await manager.GetReviewAsync(repo, child.Branch, child.StartCommit);

            await manager.MergeAsync(repo, review);

            Assert.Equal("child change\nchanged after review\n", Normalize(await File.ReadAllTextAsync(Path.Combine(repo, "child.txt"))));
            Assert.Equal("main", (await RunGitAsync(repo, "branch", "--show-current")).Trim());
            Assert.Equal("child change\nchanged after review\n", Normalize(await File.ReadAllTextAsync(Path.Combine(child.WorktreePath, "child.txt"))));
            Assert.False((await new GitRepositoryService(repo).GetStatusAsync()).HasChanges);
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Merge_rejects_dirty_target_or_child_worktrees_and_foreign_branches()
    {
        var temp = Path.Combine(Path.GetTempPath(), "codev-child-worktree-tests", Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(temp, "repo");
        Directory.CreateDirectory(repo);
        try
        {
            await InitializeRepositoryAsync(repo);
            var child = await new GitChildWorktreeManager(Path.Combine(temp, "appdata"))
                .CreateAsync(repo, Guid.NewGuid(), Guid.NewGuid());
            await File.WriteAllTextAsync(Path.Combine(child.WorktreePath, "uncommitted.txt"), "pending\n");
            var manager = new GitChildWorktreeManager(Path.Combine(temp, "appdata"));
            var review = await manager.GetReviewAsync(repo, child.Branch, child.StartCommit);
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.MergeAsync(repo, review));
            await Assert.ThrowsAsync<ArgumentException>(() => new GitChildWorktreeManager(Path.Combine(temp, "appdata"))
                .GetReviewAsync(repo, "main", child.StartCommit));

            File.Delete(Path.Combine(child.WorktreePath, "uncommitted.txt"));
            await File.WriteAllTextAsync(Path.Combine(repo, "parent.txt"), "dirty parent\n");
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.MergeAsync(repo, review));
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Recovers_a_missing_managed_checkout_from_its_retained_child_branch()
    {
        var temp = Path.Combine(Path.GetTempPath(), "codev-child-worktree-tests", Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(temp, "repo");
        var appData = Path.Combine(temp, "appdata");
        Directory.CreateDirectory(repo);
        try
        {
            await InitializeRepositoryAsync(repo);
            var parentId = Guid.NewGuid();
            var childId = Guid.NewGuid();
            var manager = new GitChildWorktreeManager(appData);
            var child = await manager.CreateAsync(repo, parentId, childId);
            await File.WriteAllTextAsync(Path.Combine(child.WorktreePath, "child.txt"), "saved branch change\n");
            await RunGitAsync(child.WorktreePath, "add", "--", "child.txt");
            await RunGitAsync(child.WorktreePath, "commit", "-m", "saved branch change");
            Directory.Delete(child.WorktreePath, recursive: true);
            Assert.False(manager.IsManagedWorktreePath(child.WorktreePath));

            var recovered = await manager.RecoverAsync(repo, parentId, childId, child.Branch, child.StartCommit);

            Assert.Equal(child.WorktreePath, recovered.WorktreePath);
            Assert.True(manager.IsManagedWorktreePath(recovered.WorktreePath));
            Assert.Equal("saved branch change\n", Normalize(await File.ReadAllTextAsync(Path.Combine(recovered.WorktreePath, "child.txt"))));
            Assert.Equal(child.Branch, (await RunGitAsync(recovered.WorktreePath, "branch", "--show-current")).Trim());
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { } }
    }

    private static async Task InitializeRepositoryAsync(string repo)
    {
        await RunGitAsync(repo, "init", "-b", "main");
        await RunGitAsync(repo, "config", "user.name", "Codev Tests");
        await RunGitAsync(repo, "config", "user.email", "codev-tests@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "committed\n");
        await RunGitAsync(repo, "add", "--", "tracked.txt");
        await RunGitAsync(repo, "commit", "-m", "initial");
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git", WorkingDirectory = workingDirectory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }
}

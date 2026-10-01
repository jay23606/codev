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

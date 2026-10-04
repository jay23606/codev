using System.Diagnostics;
using System.IO;

namespace Codev.Tests;

public sealed class GitRepositoryServiceTests
{
    [Fact]
    public async Task Status_and_review_diffs_do_not_run_repository_fsmonitor_or_textconv_helpers()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "review-helper-ran.txt");
        var helper = Path.Combine(root, "review-helper.cjs");
        try
        {
            await RunGitAsync(root, "init", "-b", "main");
            await RunGitAsync(root, "config", "user.name", "Codev Tests");
            await RunGitAsync(root, "config", "user.email", "codev-tests@example.invalid");
            var helperSource = "require('fs').writeFileSync('" + marker.Replace('\\', '/') + "', 'executed'); " +
                "process.stdout.write(require('fs').readFileSync(process.argv[2]));";
            await File.WriteAllTextAsync(helper, helperSource);
            var helperPath = helper.Replace('\\', '/');
            await RunGitAsync(root, "config", "core.fsmonitor", $"node \"{helperPath}\"");
            await RunGitAsync(root, "config", "diff.codev-audit.textconv", $"node \"{helperPath}\"");
            await File.WriteAllTextAsync(Path.Combine(root, ".gitattributes"), "fixture.txt diff=codev-audit\n");
            var fixture = Path.Combine(root, "fixture.txt");
            await File.WriteAllTextAsync(fixture, "baseline\n");
            await RunGitAsync(root, "add", "--", ".gitattributes", "fixture.txt");
            await RunGitAsync(root, "commit", "-m", "initial");
            await RunGitAsync(root, "branch", "review-base");
            await File.WriteAllTextAsync(fixture, "working change\n");
            var service = new GitRepositoryService(root);

            var status = await service.GetStatusAsync();
            var file = Assert.Single(status.Files);
            var workingDiff = await service.GetFileDiffAsync(file);
            var workingReview = await service.GetWorkingTreeReviewAsync();
            Assert.Contains("working change", workingDiff, StringComparison.Ordinal);
            Assert.Contains("working change", workingReview.Diff, StringComparison.Ordinal);
            Assert.False(File.Exists(marker));

            await service.StageFileAsync("fixture.txt");
            Assert.Contains("working change", await service.GetStagedDiffAsync(), StringComparison.Ordinal);
            await RunGitAsync(root, "commit", "-m", "review change");
            Assert.Contains("working change", (await service.GetCommitReviewAsync((await RunGitAsync(root, "rev-parse", "HEAD")).Trim())).Diff, StringComparison.Ordinal);
            Assert.Contains("working change", (await service.GetBranchReviewAsync("review-base")).Diff, StringComparison.Ordinal);
            Assert.False(File.Exists(marker));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Stage_all_stages_tracked_and_untracked_changes_but_unstage_all_preserves_working_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGitAsync(root, "init", "-b", "main");
            await RunGitAsync(root, "config", "user.name", "Codev Tests");
            await RunGitAsync(root, "config", "user.email", "codev-tests@example.invalid");
            var tracked = Path.Combine(root, "tracked.txt");
            await File.WriteAllTextAsync(tracked, "baseline\n");
            await RunGitAsync(root, "add", "--", "tracked.txt");
            await RunGitAsync(root, "commit", "-m", "initial");
            await File.WriteAllTextAsync(tracked, "changed\n");
            var untracked = Path.Combine(root, "new file.txt");
            await File.WriteAllTextAsync(untracked, "new\n");
            var nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(nested);
            var service = new GitRepositoryService(nested);

            await service.StageAllAsync();

            var staged = (await service.GetStatusAsync()).Files;
            Assert.Equal("M ", Assert.Single(staged, file => file.Path == "tracked.txt").State);
            Assert.Equal("A ", Assert.Single(staged, file => file.Path == "new file.txt").State);
            Assert.Contains("new file.txt", await service.GetStagedDiffAsync(), StringComparison.Ordinal);

            await service.UnstageAllAsync();

            var unstaged = (await service.GetStatusAsync()).Files;
            Assert.Equal(" M", Assert.Single(unstaged, file => file.Path == "tracked.txt").State);
            Assert.Equal("??", Assert.Single(unstaged, file => file.Path == "new file.txt").State);
            Assert.Equal("changed\n", await ReadNormalizedAsync(tracked));
            Assert.Equal("new\n", await ReadNormalizedAsync(untracked));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Revert_file_discards_the_displayed_unstaged_diff_preserves_staged_changes_and_removes_untracked_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGitAsync(root, "init", "-b", "main");
            await RunGitAsync(root, "config", "user.name", "Codev Tests");
            await RunGitAsync(root, "config", "user.email", "codev-tests@example.invalid");
            var tracked = Path.Combine(root, "tracked.txt");
            await File.WriteAllTextAsync(tracked, "baseline\n");
            await RunGitAsync(root, "add", "--", "tracked.txt");
            await RunGitAsync(root, "commit", "-m", "initial");

            await File.WriteAllTextAsync(tracked, "staged version\n");
            var service = new GitRepositoryService(root);
            await service.StageFileAsync("tracked.txt");
            await File.WriteAllTextAsync(tracked, "unstaged version\n");
            var status = Assert.Single((await service.GetStatusAsync()).Files);
            var displayedDiff = await service.GetFileDiffAsync(status);

            await service.RevertFileAsync(status.Path, displayedDiff);

            Assert.Equal("staged version\n", await ReadNormalizedAsync(tracked));
            status = Assert.Single((await service.GetStatusAsync()).Files);
            Assert.Equal("M ", status.State);
            Assert.Contains("staged version", await service.GetStagedDiffAsync(), StringComparison.Ordinal);
            Assert.DoesNotContain("unstaged version", await service.GetStagedDiffAsync(), StringComparison.Ordinal);

            var untracked = Path.Combine(root, "new file.txt");
            await File.WriteAllTextAsync(untracked, "untracked contents\n");
            status = (await service.GetStatusAsync()).Files.Single(file => file.Path == "new file.txt");
            displayedDiff = await service.GetFileDiffAsync(status);
            await service.RevertFileAsync(status.Path, displayedDiff);
            Assert.False(File.Exists(untracked));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Bulk_revert_discards_all_reviewed_unstaged_files_and_preserves_staged_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGitAsync(root, "init", "-b", "main");
            await RunGitAsync(root, "config", "user.name", "Codev Tests");
            await RunGitAsync(root, "config", "user.email", "codev-tests@example.invalid");
            var tracked = Path.Combine(root, "tracked.txt");
            await File.WriteAllTextAsync(tracked, "baseline\n");
            await RunGitAsync(root, "add", "--", "tracked.txt");
            await RunGitAsync(root, "commit", "-m", "initial");
            var service = new GitRepositoryService(root);
            await File.WriteAllTextAsync(tracked, "staged version\n");
            await service.StageFileAsync("tracked.txt");
            await File.WriteAllTextAsync(tracked, "unstaged version\n");
            var untracked = Path.Combine(root, "new file.txt");
            await File.WriteAllTextAsync(untracked, "new file\n");

            var preview = await service.GetUnstagedDiscardPreviewAsync();
            Assert.Equal(2, preview.Changes.Count);
            await service.RevertAllUnstagedAsync(preview);

            Assert.Equal("staged version\n", await ReadNormalizedAsync(tracked));
            Assert.False(File.Exists(untracked));
            var remaining = Assert.Single((await service.GetStatusAsync()).Files);
            Assert.Equal("M ", remaining.State);
            Assert.Contains("staged version", await service.GetStagedDiffAsync(), StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Bulk_revert_refuses_a_stale_preview_before_changing_any_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGitAsync(root, "init", "-b", "main");
            await RunGitAsync(root, "config", "user.name", "Codev Tests");
            await RunGitAsync(root, "config", "user.email", "codev-tests@example.invalid");
            var first = Path.Combine(root, "first.txt");
            var second = Path.Combine(root, "second.txt");
            await File.WriteAllTextAsync(first, "first baseline\n");
            await File.WriteAllTextAsync(second, "second baseline\n");
            await RunGitAsync(root, "add", "--", "first.txt", "second.txt");
            await RunGitAsync(root, "commit", "-m", "initial");
            await File.WriteAllTextAsync(first, "first edit\n");
            await File.WriteAllTextAsync(second, "second edit\n");
            var service = new GitRepositoryService(root);
            var preview = await service.GetUnstagedDiscardPreviewAsync();
            await File.WriteAllTextAsync(second, "newer second edit\n");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RevertAllUnstagedAsync(preview));

            Assert.Contains("changed after review", error.Message, StringComparison.Ordinal);
            Assert.Equal("first edit\n", await ReadNormalizedAsync(first));
            Assert.Equal("newer second edit\n", await ReadNormalizedAsync(second));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Bulk_revert_requires_individual_review_when_the_path_limit_is_exceeded()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGitAsync(root, "init", "-b", "main");
            await RunGitAsync(root, "config", "user.name", "Codev Tests");
            await RunGitAsync(root, "config", "user.email", "codev-tests@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(root, "seed.txt"), "baseline\n");
            await RunGitAsync(root, "add", "--", "seed.txt");
            await RunGitAsync(root, "commit", "-m", "initial");
            for (var index = 0; index <= GitRepositoryService.MaxBulkDiscardFiles; index++)
                await File.WriteAllTextAsync(Path.Combine(root, $"new-{index:D2}.txt"), "untracked\n");
            var service = new GitRepositoryService(root);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetUnstagedDiscardPreviewAsync());

            Assert.Contains("limited to 40 unstaged paths", error.Message, StringComparison.Ordinal);
            Assert.Equal(GitRepositoryService.MaxBulkDiscardFiles + 1, (await service.GetStatusAsync()).Files.Count);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Unstage_file_moves_all_mixed_staged_and_unstaged_changes_to_the_working_tree()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGitAsync(root, "init", "-b", "main");
            await RunGitAsync(root, "config", "user.name", "Codev Tests");
            await RunGitAsync(root, "config", "user.email", "codev-tests@example.invalid");
            var path = Path.Combine(root, "mixed.txt");
            await File.WriteAllTextAsync(path, "baseline\n");
            await RunGitAsync(root, "add", "--", "mixed.txt");
            await RunGitAsync(root, "commit", "-m", "initial");
            var service = new GitRepositoryService(root);
            await File.WriteAllTextAsync(path, "staged edit\n");
            await service.StageFileAsync("mixed.txt");
            await File.WriteAllTextAsync(path, "latest working edit\n");
            Assert.Equal("MM", Assert.Single((await service.GetStatusAsync()).Files).State);

            await service.UnstageFileAsync("mixed.txt");

            Assert.Equal("latest working edit\n", await ReadNormalizedAsync(path));
            Assert.Equal(" M", Assert.Single((await service.GetStatusAsync()).Files).State);
            Assert.Equal("", await service.GetStagedDiffAsync());
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Revert_file_refuses_when_the_displayed_diff_is_stale()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGitAsync(root, "init", "-b", "main");
            await RunGitAsync(root, "config", "user.name", "Codev Tests");
            await RunGitAsync(root, "config", "user.email", "codev-tests@example.invalid");
            var path = Path.Combine(root, "sample.txt");
            await File.WriteAllTextAsync(path, "baseline\n");
            await RunGitAsync(root, "add", "--", "sample.txt");
            await RunGitAsync(root, "commit", "-m", "initial");
            await File.WriteAllTextAsync(path, "first edit\n");
            var service = new GitRepositoryService(root);
            var status = Assert.Single((await service.GetStatusAsync()).Files);
            var displayedDiff = await service.GetFileDiffAsync(status);
            await File.WriteAllTextAsync(path, "newer edit\n");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RevertFileAsync(status.Path, displayedDiff));

            Assert.Contains("diff changed after it was displayed", error.Message);
            Assert.Equal("newer edit\n", await ReadNormalizedAsync(path));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Revert_file_treats_git_pathspec_patterns_as_literal_file_names()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGitAsync(root, "init", "-b", "main");
            await RunGitAsync(root, "config", "user.name", "Codev Tests");
            await RunGitAsync(root, "config", "user.email", "codev-tests@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(root, "seed.txt"), "baseline\n");
            await RunGitAsync(root, "add", "--", "seed.txt");
            await RunGitAsync(root, "commit", "-m", "initial");
            var literalPath = Path.Combine(root, "[a].txt");
            var matchingPath = Path.Combine(root, "a.txt");
            await File.WriteAllTextAsync(literalPath, "remove only this file\n");
            await File.WriteAllTextAsync(matchingPath, "keep this file\n");
            var service = new GitRepositoryService(root);
            var selected = (await service.GetStatusAsync()).Files.Single(file => file.Path == "[a].txt");
            var displayedDiff = await service.GetFileDiffAsync(selected);

            await service.RevertFileAsync(selected.Path, displayedDiff);

            Assert.False(File.Exists(literalPath));
            Assert.True(File.Exists(matchingPath));
            Assert.Equal("keep this file\n", await ReadNormalizedAsync(matchingPath));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Can_stage_unstage_and_revert_one_hunk_without_touching_other_hunks()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGitAsync(root, "init", "-b", "main");
            await RunGitAsync(root, "config", "user.name", "Codev Tests");
            await RunGitAsync(root, "config", "user.email", "codev-tests@example.invalid");
            var lines = Enumerable.Range(1, 30).Select(index => $"line {index}").ToArray();
            var file = Path.Combine(root, "hunks.txt");
            await File.WriteAllLinesAsync(file, lines);
            await RunGitAsync(root, "add", "--", "hunks.txt");
            await RunGitAsync(root, "commit", "-m", "initial");

            lines[1] = "edited first";
            lines[24] = "edited second";
            await File.WriteAllLinesAsync(file, lines);
            var service = new GitRepositoryService(root);
            var status = Assert.Single((await service.GetStatusAsync()).Files);
            var diff = await service.GetFileDiffAsync(status);
            var firstChange = diff.IndexOf("+edited first", StringComparison.Ordinal);
            Assert.True(firstChange >= 0);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyHunkAsync(status.Path, diff,
                firstChange, "+edited first".Length, GitDiffHunkAction.Unstage));
            await service.ApplyHunkAsync(status.Path, diff, firstChange, "+edited first".Length, GitDiffHunkAction.Stage);

            status = Assert.Single((await service.GetStatusAsync()).Files);
            Assert.Equal("MM", status.State);
            var partiallyStagedDiff = await service.GetFileDiffAsync(status);
            var stagedFirstChange = partiallyStagedDiff.IndexOf("+edited first", StringComparison.Ordinal);
            Assert.True(stagedFirstChange >= 0);
            var staleSecondChange = diff.IndexOf("+edited second", StringComparison.Ordinal);
            Assert.True(staleSecondChange >= 0);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyHunkAsync(status.Path, diff,
                staleSecondChange, "+edited second".Length, GitDiffHunkAction.Stage));
            Assert.Contains("STAGED CHANGES", partiallyStagedDiff, StringComparison.Ordinal);
            Assert.Contains("UNSTAGED CHANGES", partiallyStagedDiff, StringComparison.Ordinal);
            Assert.DoesNotContain("+edited second", partiallyStagedDiff[..partiallyStagedDiff.IndexOf("UNSTAGED CHANGES", StringComparison.Ordinal)], StringComparison.Ordinal);
            Assert.Contains("+edited second", partiallyStagedDiff[(partiallyStagedDiff.IndexOf("UNSTAGED CHANGES", StringComparison.Ordinal))..], StringComparison.Ordinal);

            await service.ApplyHunkAsync(status.Path, partiallyStagedDiff, stagedFirstChange, "+edited first".Length, GitDiffHunkAction.Unstage);
            status = Assert.Single((await service.GetStatusAsync()).Files);
            Assert.Equal(" M", status.State);
            var unstagedDiff = await service.GetFileDiffAsync(status);
            var firstUnstagedChange = unstagedDiff.IndexOf("+edited first", StringComparison.Ordinal);
            await service.ApplyHunkAsync(status.Path, unstagedDiff, firstUnstagedChange, "+edited first".Length, GitDiffHunkAction.Revert);

            var finalLines = await File.ReadAllLinesAsync(file);
            Assert.Equal("line 2", finalLines[1]);
            Assert.Equal("edited second", finalLines[24]);
            Assert.Equal(" M", Assert.Single((await service.GetStatusAsync()).Files).State);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Status_reports_branch_changes_and_only_switches_existing_branches_when_clean()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunGitAsync(root, "init", "-b", "main");
            await RunGitAsync(root, "config", "user.name", "Codev Tests");
            await RunGitAsync(root, "config", "user.email", "codev-tests@example.invalid");
            var file = Path.Combine(root, "read me.txt");
            await File.WriteAllTextAsync(file, "base\n");
            await RunGitAsync(root, "add", "--", "read me.txt");
            await RunGitAsync(root, "commit", "-m", "initial");

            var service = new GitRepositoryService(root);
            var clean = await service.GetStatusAsync();
            Assert.True(Directory.Exists(clean.Root));
            Assert.Equal(Path.GetFileName(root), Path.GetFileName(clean.Root));
            Assert.Equal("main", clean.Branch);
            Assert.False(clean.HasChanges);
            Assert.Contains("main", await service.GetLocalBranchesAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CommitAsync("empty commit"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAndSwitchBranchAsync("bad..branch"));

            await service.CreateAndSwitchBranchAsync("feature/test");
            Assert.Equal("feature/test", (await service.GetStatusAsync()).Branch);
            Assert.Contains("feature/test", await service.GetLocalBranchesAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAndSwitchBranchAsync("feature/test"));
            await service.SwitchBranchAsync("main");
            await service.SwitchBranchAsync("feature/test");

            await File.AppendAllTextAsync(file, "modified\n");
            var dirty = await service.GetStatusAsync();
            Assert.True(dirty.HasChanges);
            Assert.Contains(dirty.Files, entry => entry.Path == "read me.txt" && entry.WorkingTree == "M");
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchBranchAsync("main"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAndSwitchBranchAsync("feature/blocked"));
            Assert.Equal("feature/test", (await service.GetStatusAsync()).Branch);

            var modification = dirty.Files.Single(entry => entry.Path == "read me.txt");
            Assert.Contains("modified", await service.GetFileDiffAsync(modification), StringComparison.Ordinal);
            await service.StageFileAsync(modification.Path);
            var staged = Assert.Single((await service.GetStatusAsync()).Files);
            Assert.Equal("M ", staged.State);
            Assert.Contains("modified", await service.GetStagedDiffAsync(), StringComparison.Ordinal);
            await service.UnstageFileAsync(modification.Path);
            Assert.Equal(" M", Assert.Single((await service.GetStatusAsync()).Files).State);

            var untracked = Path.Combine(root, "new file.txt");
            await File.WriteAllTextAsync(untracked, "new content\n");
            var untrackedStatus = (await service.GetStatusAsync()).Files.Single(entry => entry.Path == "new file.txt");
            Assert.Equal("??", untrackedStatus.State);
            Assert.Contains("new content", await service.GetFileDiffAsync(untrackedStatus), StringComparison.Ordinal);

            var review = await service.GetWorkingTreeReviewAsync();
            Assert.Equal("feature/test", review.Branch);
            Assert.Equal(2, review.Files.Count);
            Assert.Contains("modified", review.Diff, StringComparison.Ordinal);
            Assert.Contains("new content", review.Diff, StringComparison.Ordinal);
            var boundedReview = await service.GetWorkingTreeReviewAsync(maxCharacters: 180);
            Assert.True(boundedReview.Truncated);
            Assert.True(boundedReview.Diff.Length <= 180);
            Assert.Contains("truncated", boundedReview.Diff, StringComparison.OrdinalIgnoreCase);
            await service.StageFileAsync(modification.Path);
            var stagedWorkingTreeReview = await service.GetWorkingTreeReviewAsync();
            Assert.Contains("STAGED CHANGES", stagedWorkingTreeReview.Diff, StringComparison.Ordinal);
            Assert.Contains("UNTRACKED FILE", stagedWorkingTreeReview.Diff, StringComparison.Ordinal);

            await service.StageFileAsync("read me.txt");
            await service.StageFileAsync("new file.txt");
            Assert.Contains("new content", await service.GetStagedDiffAsync(), StringComparison.Ordinal);
            var reviewedDiff = await service.GetStagedReviewAsync();
            await File.AppendAllTextAsync(untracked, "updated after review\n");
            await service.StageFileAsync("new file.txt");
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CommitAsync("stale review", reviewedDiff));
            Assert.True((await service.GetStatusAsync()).HasChanges);
            reviewedDiff = await service.GetStagedReviewAsync();
            await service.CommitAsync("Save reviewed changes", reviewedDiff);
            Assert.False((await service.GetStatusAsync()).HasChanges);

            var commit = await RunGitAsync(root, "rev-parse", "HEAD");
            var commitReview = await service.GetCommitReviewAsync(commit.Trim());
            Assert.StartsWith("commit ", commitReview.Branch, StringComparison.Ordinal);
            Assert.Contains("new content", commitReview.Diff, StringComparison.Ordinal);
            Assert.Equal(2, commitReview.Files.Count);
            var boundedCommit = await service.GetCommitReviewAsync(commit.Trim(), maxCharacters: 160);
            Assert.True(boundedCommit.Truncated);
            Assert.True(boundedCommit.Diff.Length <= 160);
            var oneFileCommit = await service.GetCommitReviewAsync(commit.Trim(), maxFiles: 1);
            Assert.Single(oneFileCommit.Files);
            var omittedFile = Assert.Single(commitReview.Files.Except(oneFileCommit.Files));
            Assert.DoesNotContain(omittedFile, oneFileCommit.Diff, StringComparison.Ordinal);
            await Assert.ThrowsAsync<ArgumentException>(() => service.GetCommitReviewAsync("HEAD~1;whoami"));
            var branchReview = await service.GetBranchReviewAsync("main");
            Assert.Equal("branch main...HEAD", branchReview.Branch);
            Assert.Contains("new content", branchReview.Diff, StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetBranchReviewAsync("--all"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Status_explains_when_a_folder_is_not_a_repository()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new GitRepositoryService(root);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetStatusAsync());
            Assert.Contains("not inside a Git repository", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<string> ReadNormalizedAsync(string path) =>
        (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static async Task<string> RunGitAsync(string root, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(root, "missing-global-config");
        process.StartInfo.Environment["GIT_CONFIG_SYSTEM"] = Path.Combine(root, "missing-system-config");
        Assert.True(process.Start());
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {output} {error}");
        return output;
    }
}

using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Codev;

public sealed record GitFileStatus(string Staged, string WorkingTree, string Path, string? OriginalPath = null)
{
    public string State => $"{Staged}{WorkingTree}";
    public string DisplayPath => OriginalPath is null ? Path : $"{Path} ← {OriginalPath}";
}

public sealed record GitRepositoryStatus(string Root, string Branch, string? Upstream, int Ahead, int Behind, IReadOnlyList<GitFileStatus> Files)
{
    public bool HasChanges => Files.Count > 0;
}

public sealed record GitStagedReview(string TreeId, string Diff);
public sealed record GitWorkingTreeReview(string Branch, IReadOnlyList<string> Files, string Diff, bool Truncated);
public sealed record GitUnstagedDiscardEntry(GitFileStatus File, string DisplayedDiff);
public sealed record GitUnstagedDiscardPreview(IReadOnlyList<GitFileStatus> RepositoryFiles, IReadOnlyList<GitUnstagedDiscardEntry> Changes);

/// <summary>Reads Git state and switches only between existing local branches on a clean worktree.</summary>
public sealed class GitRepositoryService
{
    private static readonly TimeSpan GitCommandTimeout = TimeSpan.FromSeconds(30);
    public const int MaxReviewDiffCharacters = 40_000;
    public const int MaxReviewFiles = 40;
    public const int MaxBulkDiscardFiles = 40;
    private readonly string _workingDirectory;

    public GitRepositoryService(string workingDirectory) => _workingDirectory = Path.GetFullPath(workingDirectory);

    public async Task<GitRepositoryStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var rootResult = await RunGitAsync(["rev-parse", "--show-toplevel"], cancellationToken);
        EnsureSuccess(rootResult, "The selected folder is not inside a Git repository.");
        var root = Path.GetFullPath(rootResult.Output.Trim());
        var statusResult = await RunGitAsync(["status", "--porcelain=v1", "--branch", "--untracked-files=all", "-z"], cancellationToken);
        EnsureSuccess(statusResult, "Git could not read the repository status.");

        var entries = statusResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var branch = "(unknown branch)";
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var files = new List<GitFileStatus>();
        for (var index = 0; index < entries.Length; index++)
        {
            var line = entries[index];
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var header = line[3..];
                var trackingAt = header.IndexOf("...", StringComparison.Ordinal);
                var branchText = trackingAt < 0 ? header : header[..trackingAt];
                if (branchText.StartsWith("No commits yet on ", StringComparison.Ordinal)) branchText = branchText[18..];
                branch = branchText;
                if (trackingAt >= 0)
                {
                    var tracking = header[(trackingAt + 3)..];
                    var bracketAt = tracking.IndexOf(" [", StringComparison.Ordinal);
                    upstream = bracketAt < 0 ? tracking : tracking[..bracketAt];
                    if (bracketAt >= 0)
                    {
                        var counts = tracking[(bracketAt + 2)..].TrimEnd(']');
                        ahead = ParseCount(counts, "ahead");
                        behind = ParseCount(counts, "behind");
                    }
                }
                continue;
            }
            if (line.Length < 4) continue;
            var staged = line[..1];
            var workingTree = line.Substring(1, 1);
            var path = line[3..];
            var originalPath = (staged is "R" or "C" || workingTree is "R" or "C") && index + 1 < entries.Length
                ? entries[++index]
                : null;
            files.Add(new GitFileStatus(staged, workingTree, path, originalPath));
        }
        return new GitRepositoryStatus(root, branch, upstream, ahead, behind, files);
    }

    public async Task<IReadOnlyList<string>> GetLocalBranchesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(["branch", "--format=%(refname:short)"], cancellationToken);
        EnsureSuccess(result, "Git could not list local branches.");
        return result.Output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(branch => branch.Trim()).Where(branch => branch.Length > 0).ToArray();
    }

    public async Task SwitchBranchAsync(string branch, CancellationToken cancellationToken = default)
    {
        var branches = await GetLocalBranchesAsync(cancellationToken);
        if (!branches.Contains(branch, StringComparer.Ordinal)) throw new InvalidOperationException("Choose an existing local branch.");
        var status = await GetStatusAsync(cancellationToken);
        if (status.HasChanges) throw new InvalidOperationException("Commit or stash the working tree changes before switching branches.");
        if (string.Equals(status.Branch, branch, StringComparison.Ordinal)) return;
        var result = await RunGitAsync(["switch", "--", branch], cancellationToken);
        EnsureSuccess(result, "Git could not switch to the selected branch.");
    }

    public async Task CreateAndSwitchBranchAsync(string branch, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branch) || !string.Equals(branch, branch.Trim(), StringComparison.Ordinal) || branch.StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException("Enter a valid local branch name.", nameof(branch));
        if (branch.Contains("@{-", StringComparison.Ordinal)) throw new ArgumentException("Previous-branch shorthand is not allowed as a new branch name.", nameof(branch));
        var validation = await RunGitAsync(["check-ref-format", "--branch", branch], cancellationToken);
        EnsureSuccess(validation, "Git rejected this branch name.");
        var branches = await GetLocalBranchesAsync(cancellationToken);
        if (branches.Contains(branch, StringComparer.Ordinal)) throw new InvalidOperationException("A local branch with that name already exists.");
        var status = await GetStatusAsync(cancellationToken);
        if (status.HasChanges) throw new InvalidOperationException("Commit or stash the working tree changes before creating a branch.");
        var result = await RunGitAsync(["switch", "-c", branch], cancellationToken);
        EnsureSuccess(result, "Git could not create and switch to the new branch.");
    }

    public async Task<string> GetFileDiffAsync(GitFileStatus file, CancellationToken cancellationToken = default)
        => await GetFileDiffAsync(file, int.MaxValue, cancellationToken);

    private async Task<string> GetFileDiffAsync(GitFileStatus file, int maxCharacters, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        var current = status.Files.FirstOrDefault(candidate => string.Equals(candidate.Path, file.Path, StringComparison.Ordinal));
        if (current is null) throw new InvalidOperationException("That file is no longer changed in the repository. Refresh Git status and select it again.");

        var output = new System.Text.StringBuilder();
        if (current.Staged != " ")
        {
            output.AppendLine("STAGED CHANGES");
            output.AppendLine(await ReadDiffAsync(["diff", "--cached", "--no-ext-diff", "--no-color", "--", LiteralPathspec(current.Path)], cancellationToken, maxCharacters));
        }
        if (current.WorkingTree == "?")
        {
            output.AppendLine("UNTRACKED FILE");
            var boundedOutput = maxCharacters == int.MaxValue ? int.MaxValue : maxCharacters + 1;
            var diff = await RunGitAsync(["diff", "--no-index", "--no-ext-diff", "--no-color", "--", "/dev/null", current.Path], cancellationToken, boundedOutput);
            if (diff.ExitCode is not 0 and not 1) EnsureSuccess(diff, "Git could not read this untracked file.");
            output.AppendLine(diff.Output);
        }
        else if (current.WorkingTree != " ")
        {
            output.AppendLine("UNSTAGED CHANGES");
            output.AppendLine(await ReadDiffAsync(["diff", "--no-ext-diff", "--no-color", "--", LiteralPathspec(current.Path)], cancellationToken, maxCharacters));
        }
        var value = output.ToString().TrimEnd();
        return value.Length > maxCharacters ? value[..maxCharacters] + "\n[diff excerpt truncated]" : value;
    }

    public async Task<GitWorkingTreeReview> GetWorkingTreeReviewAsync(CancellationToken cancellationToken = default,
        int maxCharacters = MaxReviewDiffCharacters, int maxFiles = MaxReviewFiles)
    {
        if (maxCharacters < 1 || maxFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        var status = await GetStatusAsync(cancellationToken);
        var selected = status.Files.Take(maxFiles).ToArray();
        var output = new System.Text.StringBuilder();
        var truncated = status.Files.Count > selected.Length;
        foreach (var file in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var heading = $"\n===== {file.State.Trim()} {file.DisplayPath} =====\n";
            if (output.Length + heading.Length > maxCharacters) { truncated = true; break; }
            output.Append(heading);
            var remaining = maxCharacters - output.Length;
            if (remaining <= 0) { truncated = true; break; }
            var fileDiff = await GetFileDiffAsync(file, remaining, cancellationToken);
            if (fileDiff.Length > remaining) { fileDiff = fileDiff[..remaining]; truncated = true; }
            output.Append(fileDiff);
            if (output.Length >= maxCharacters) { truncated = true; break; }
        }
        if (truncated)
        {
            var marker = "\n[Review input truncated at Codev's safety limit.]";
            if (marker.Length > maxCharacters) marker = marker[..maxCharacters];
            if (output.Length + marker.Length > maxCharacters) output.Length = Math.Max(0, maxCharacters - marker.Length);
            output.Append(marker);
        }
        return new GitWorkingTreeReview(status.Branch, selected.Select(file => file.DisplayPath).ToArray(), output.ToString(), truncated);
    }

    public async Task<GitWorkingTreeReview> GetCommitReviewAsync(string commit, CancellationToken cancellationToken = default,
        int maxCharacters = MaxReviewDiffCharacters, int maxFiles = MaxReviewFiles)
    {
        if (string.IsNullOrWhiteSpace(commit) || !Regex.IsMatch(commit, "^[0-9a-fA-F]{7,40}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Enter a full or abbreviated commit hash (7–40 hexadecimal characters).", nameof(commit));
        if (maxCharacters < 1 || maxFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        var resolved = await RunGitAsync(["rev-parse", "--verify", "--end-of-options", commit + "^{commit}"], cancellationToken);
        EnsureSuccess(resolved, "Git could not find that commit in this repository.");
        var hash = resolved.Output.Trim();
        var filesResult = await RunGitAsync(["diff-tree", "--no-commit-id", "--name-only", "-r", "--root", "-z", hash], cancellationToken, 250_000);
        EnsureSuccess(filesResult, "Git could not list the files changed by this commit.");
        var allFiles = filesResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = allFiles.Take(maxFiles).ToArray();
        var diffArguments = new List<string> { "show", "--format=", "--no-ext-diff", "--no-color", "--no-renames", "--unified=3", hash, "--" };
        diffArguments.AddRange(files.Select(path => ":(literal)" + path));
        var diffResult = await RunGitAsync(diffArguments, cancellationToken, maxCharacters + 1);
        EnsureSuccess(diffResult, "Git could not read this commit's diff.");
        var truncated = allFiles.Length > files.Length || filesResult.Output.Length >= 250_000 || diffResult.Output.Length > maxCharacters;
        var diff = diffResult.Output.Length > maxCharacters ? diffResult.Output[..maxCharacters] : diffResult.Output;
        if (truncated)
        {
            var marker = "\n[Review input truncated at Codev's safety limit.]";
            if (marker.Length > maxCharacters) marker = marker[..maxCharacters];
            if (diff.Length + marker.Length > maxCharacters) diff = diff[..Math.Max(0, maxCharacters - marker.Length)];
            diff += marker;
        }
        return new GitWorkingTreeReview($"commit {hash[..12]}", files, diff, truncated);
    }

    public async Task<GitWorkingTreeReview> GetBranchReviewAsync(string baseBranch, CancellationToken cancellationToken = default,
        int maxCharacters = MaxReviewDiffCharacters, int maxFiles = MaxReviewFiles)
    {
        if (string.IsNullOrWhiteSpace(baseBranch) || !string.Equals(baseBranch, baseBranch.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Enter the exact name of a local base branch.", nameof(baseBranch));
        if (maxCharacters < 1 || maxFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        var branches = await GetLocalBranchesAsync(cancellationToken);
        if (!branches.Contains(baseBranch, StringComparer.Ordinal)) throw new InvalidOperationException("Choose an existing local branch as the review base.");
        var baseResult = await RunGitAsync(["rev-parse", "--verify", "--end-of-options", $"refs/heads/{baseBranch}^{{commit}}"], cancellationToken);
        EnsureSuccess(baseResult, "Git could not resolve the selected local base branch.");
        var headResult = await RunGitAsync(["rev-parse", "--verify", "HEAD^{commit}"], cancellationToken);
        EnsureSuccess(headResult, "Git could not resolve the current branch commit.");
        var baseHash = baseResult.Output.Trim();
        var headHash = headResult.Output.Trim();
        var range = $"{baseHash}...{headHash}";
        var filesResult = await RunGitAsync(["diff", "--name-only", "-z", range, "--"], cancellationToken, 250_000);
        EnsureSuccess(filesResult, "Git could not list changes from this branch.");
        var allFiles = filesResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = allFiles.Take(maxFiles).ToArray();
        var diffArguments = new List<string> { "diff", "--no-ext-diff", "--no-color", "--no-renames", "--unified=3", range, "--" };
        diffArguments.AddRange(files.Select(path => ":(literal)" + path));
        var diffResult = await RunGitAsync(diffArguments, cancellationToken, maxCharacters + 1);
        EnsureSuccess(diffResult, "Git could not read changes from this branch.");
        var truncated = allFiles.Length > files.Length || filesResult.Output.Length >= 250_000 || diffResult.Output.Length > maxCharacters;
        var diff = diffResult.Output.Length > maxCharacters ? diffResult.Output[..maxCharacters] : diffResult.Output;
        if (truncated)
        {
            var marker = "\n[Review input truncated at Codev's safety limit.]";
            if (marker.Length > maxCharacters) marker = marker[..maxCharacters];
            if (diff.Length + marker.Length > maxCharacters) diff = diff[..Math.Max(0, maxCharacters - marker.Length)];
            diff += marker;
        }
        return new GitWorkingTreeReview($"branch {baseBranch}...HEAD", files, diff, truncated);
    }

    public async Task<string> GetStagedDiffAsync(CancellationToken cancellationToken = default) =>
        await ReadDiffAsync(["diff", "--cached", "--no-ext-diff", "--no-color"], cancellationToken);

    public async Task<GitStagedReview> GetStagedReviewAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = await GetIndexTreeIdAsync(cancellationToken);
            var diff = await GetStagedDiffAsync(cancellationToken);
            var after = await GetIndexTreeIdAsync(cancellationToken);
            if (string.Equals(before, after, StringComparison.Ordinal)) return new GitStagedReview(after, diff);
        }
        throw new InvalidOperationException("The Git index is changing too quickly to produce a stable review. Refresh and try again.");
    }

    public async Task<IReadOnlyList<string>> GetRecentCommitSubjectsAsync(int count = 5, CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(count));
        var result = await RunGitAsync(["log", $"-{count}", "--format=%s"], cancellationToken, 4096);
        if (result.ExitCode != 0) return [];
        return result.Output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(subject => subject.Trim()).Where(subject => subject.Length > 0).Take(count).ToArray();
    }

    public async Task StageFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await EnsureChangedPathAsync(path, cancellationToken);
        var result = await RunGitAsync(["add", "--", LiteralPathspec(path)], cancellationToken);
        EnsureSuccess(result, "Git could not stage this file.");
    }

    public async Task StageAllAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.HasChanges) return;
        var result = await RunGitAsync(["add", "--all", "--", ":/"], cancellationToken);
        EnsureSuccess(result, "Git could not stage all project changes.");
    }

    public async Task UnstageAllAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.Files.Any(file => file.Staged != " ")) return;
        var result = await RunGitAsync(["restore", "--staged", "--", ":/"], cancellationToken);
        EnsureSuccess(result, "Git could not unstage all project changes.");
    }

    public async Task UnstageFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = await EnsureChangedPathAsync(path, cancellationToken);
        if (file.Staged == " ") throw new InvalidOperationException("This file has no staged changes.");
        var result = await RunGitAsync(["restore", "--staged", "--", LiteralPathspec(path)], cancellationToken);
        EnsureSuccess(result, "Git could not unstage this file.");
    }

    /// <summary>Captures a bounded review of every unstaged path, including untracked files, before a bulk discard.</summary>
    public async Task<GitUnstagedDiscardPreview> GetUnstagedDiscardPreviewAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        var changes = status.Files.Where(file => file.WorkingTree != " ").ToArray();
        if (changes.Length == 0) throw new InvalidOperationException("There are no unstaged changes to discard.");
        if (changes.Length > MaxBulkDiscardFiles)
            throw new InvalidOperationException($"Discard all is limited to {MaxBulkDiscardFiles} unstaged paths so every change can be reviewed. Revert individual files instead.");

        var entries = new List<GitUnstagedDiscardEntry>(changes.Length);
        var totalCharacters = 0;
        foreach (var file in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureBulkRevertSupported(file);
            var diff = await GetFileDiffAsync(file, MaxReviewDiffCharacters - totalCharacters, cancellationToken);
            totalCharacters += diff.Length;
            if (diff.Contains("[diff excerpt truncated]", StringComparison.Ordinal) || totalCharacters > MaxReviewDiffCharacters)
                throw new InvalidOperationException($"Discard all is limited to {MaxReviewDiffCharacters:N0} characters of reviewed diffs. Revert individual files instead.");
            entries.Add(new GitUnstagedDiscardEntry(file, diff));
        }
        return new GitUnstagedDiscardPreview(status.Files.ToArray(), entries);
    }

    /// <summary>Applies a reviewed bulk discard only if the complete Git status and every displayed diff are unchanged.</summary>
    public async Task RevertAllUnstagedAsync(GitUnstagedDiscardPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var status = await GetStatusAsync(cancellationToken);
        if (!status.Files.SequenceEqual(preview.RepositoryFiles))
            throw new InvalidOperationException("Git status changed after the discard preview. Refresh and review the updated changes.");
        foreach (var entry in preview.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureBulkRevertSupported(entry.File);
            var current = status.Files.FirstOrDefault(file => string.Equals(file.Path, entry.File.Path, StringComparison.Ordinal));
            if (current is null || !string.Equals(await GetFileDiffAsync(current, cancellationToken), entry.DisplayedDiff, StringComparison.Ordinal))
                throw new InvalidOperationException($"The diff for {entry.File.DisplayPath} changed after review. Refresh and review the updated changes.");
        }

        var reverted = 0;
        foreach (var entry in preview.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RevertFileAsync(entry.File.Path, entry.DisplayedDiff, cancellationToken);
                reverted++;
            }
            catch (Exception ex) when (reverted > 0 && ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Discard stopped after {reverted} of {preview.Changes.Count} files. Refresh Git status; completed paths were reverted. {ex.Message}", ex);
            }
        }
    }

    /// <summary>Discards the complete unstaged diff for one file after verifying the displayed diff is still current.</summary>
    public async Task RevertFileAsync(string path, string displayedDiff, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(displayedDiff);
        var file = await EnsureChangedPathAsync(path, cancellationToken);
        if (file.WorkingTree == " ") throw new InvalidOperationException("This file has no unstaged changes to revert.");
        if (file.OriginalPath is not null || IsUnmergedState(file.State))
            throw new InvalidOperationException("Whole-file revert is unavailable for renames and unresolved merge conflicts. Resolve those changes manually.");

        var currentDiff = await GetFileDiffAsync(file, cancellationToken);
        if (!string.Equals(displayedDiff, currentDiff, StringComparison.Ordinal))
            throw new InvalidOperationException("The file diff changed after it was displayed. Refresh Git status and review the file again.");

        var result = file.WorkingTree == "?"
            ? await RunGitAsync(["clean", "-f", "--", LiteralPathspec(file.Path)], cancellationToken)
            : await RunGitAsync(["restore", "--worktree", "--", LiteralPathspec(file.Path)], cancellationToken);
        EnsureSuccess(result, file.WorkingTree == "?"
            ? "Git could not remove this untracked file."
            : "Git could not discard this file's unstaged changes.");
    }

    private static void EnsureBulkRevertSupported(GitFileStatus file)
    {
        if (file.OriginalPath is not null || IsUnmergedState(file.State))
            throw new InvalidOperationException($"Discard all cannot include renamed or unresolved paths ({file.DisplayPath}). Resolve or revert that path individually first.");
    }

    private static bool IsUnmergedState(string state) => state is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU" || state.Contains('U');

    public async Task ApplyHunkAsync(string path, string displayedDiff, int selectionStart, int selectionLength,
        GitDiffHunkAction action, CancellationToken cancellationToken = default)
    {
        var file = await EnsureChangedPathAsync(path, cancellationToken);
        if (file.WorkingTree == "?") throw new InvalidOperationException("Hunk actions are unavailable for untracked files; stage or discard the whole file instead.");

        var currentDiff = await GetFileDiffAsync(file, cancellationToken);
        if (!string.Equals(displayedDiff, currentDiff, StringComparison.Ordinal))
            throw new InvalidOperationException("The file diff changed after it was displayed. Refresh Git status and select the hunk again.");
        if (!GitDiffHunkSelector.TrySelect(currentDiff, selectionStart, selectionLength, out var selection) || selection is null)
            throw new InvalidOperationException("Select text inside exactly one textual diff hunk.");

        var validAction = action switch
        {
            GitDiffHunkAction.Stage => selection.Section == GitDiffHunkSection.Unstaged && file.WorkingTree != " ",
            GitDiffHunkAction.Unstage => selection.Section == GitDiffHunkSection.Staged && file.Staged != " ",
            GitDiffHunkAction.Revert => selection.Section == GitDiffHunkSection.Unstaged && file.WorkingTree != " ",
            _ => false
        };
        if (!validAction) throw new InvalidOperationException("That hunk is not available for the selected Git action.");

        var arguments = action switch
        {
            GitDiffHunkAction.Stage => new[] { "apply", "--cached", "--whitespace=nowarn" },
            GitDiffHunkAction.Unstage => new[] { "apply", "--reverse", "--cached", "--whitespace=nowarn" },
            GitDiffHunkAction.Revert => new[] { "apply", "--reverse", "--whitespace=nowarn" },
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        var result = await RunGitAsync(arguments, cancellationToken, standardInput: selection.Patch);
        EnsureSuccess(result, action switch
        {
            GitDiffHunkAction.Stage => "Git could not stage this hunk.",
            GitDiffHunkAction.Unstage => "Git could not unstage this hunk.",
            GitDiffHunkAction.Revert => "Git could not revert this hunk.",
            _ => "Git could not apply this hunk action."
        });
    }

    public async Task CommitAsync(string message, GitStagedReview? expectedReview = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Enter a commit message.", nameof(message));
        var status = await GetStatusAsync(cancellationToken);
        if (!status.Files.Any(file => file.Staged != " ")) throw new InvalidOperationException("Stage at least one file before creating a commit.");
        if (expectedReview is not null)
        {
            var currentReview = await GetStagedReviewAsync(cancellationToken);
            if (!string.Equals(expectedReview.TreeId, currentReview.TreeId, StringComparison.Ordinal) ||
                !string.Equals(expectedReview.Diff, currentReview.Diff, StringComparison.Ordinal))
                throw new InvalidOperationException("The staged changes changed after review. Reopen the commit review and confirm the updated diff.");
        }
        var result = await RunGitAsync(["commit", "-m", message.Trim()], cancellationToken);
        EnsureSuccess(result, "Git could not create the commit.");
    }

    private async Task<GitFileStatus> EnsureChangedPathAsync(string path, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        return status.Files.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("That file is no longer changed in the repository. Refresh Git status and select it again.");
    }

    private async Task<string> ReadDiffAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, int maxCharacters = int.MaxValue)
    {
        var result = await RunGitAsync(arguments, cancellationToken, maxCharacters == int.MaxValue ? int.MaxValue : maxCharacters + 1);
        EnsureSuccess(result, "Git could not read the selected diff.");
        return result.Output.Length > maxCharacters ? result.Output[..maxCharacters] + "\n[diff excerpt truncated]" : result.Output;
    }

    private async Task<string> GetIndexTreeIdAsync(CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(["write-tree"], cancellationToken);
        EnsureSuccess(result, "Git could not snapshot the staged index for review.");
        return result.Output.Trim();
    }

    private async Task<GitCommandResult> RunGitAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        int maxOutputCharacters = int.MaxValue, string? standardInput = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            }
        };
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Git could not be started.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Git was not found. Install Git and ensure `git` is on PATH.", ex);
        }

        var outputTask = ReadOutputAsync(process.StandardOutput, maxOutputCharacters, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GitCommandTimeout);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"The Git command took longer than {GitCommandTimeout.TotalSeconds:0} seconds.");
        }
        return new GitCommandResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, int maxCharacters, CancellationToken cancellationToken)
    {
        if (maxCharacters == int.MaxValue) return await reader.ReadToEndAsync(cancellationToken);
        var output = new System.Text.StringBuilder(Math.Min(maxCharacters, 4096));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            var keep = Math.Min(read, maxCharacters - output.Length);
            if (keep > 0) output.Append(buffer, 0, keep);
        }
        return output.ToString();
    }

    private static int ParseCount(string text, string key)
    {
        var match = Regex.Match(text, $@"(?:^|,\s*){key}\s+(\d+)", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var count) ? count : 0;
    }

    private static void EnsureSuccess(GitCommandResult result, string message)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException($"{message}\n{result.Error.Trim()}");
    }

    private static string LiteralPathspec(string path) => ":(literal)" + path;

    private sealed record GitCommandResult(int ExitCode, string Output, string Error);
}

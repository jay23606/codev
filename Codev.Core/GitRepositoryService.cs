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

/// <summary>Reads Git state and switches only between existing local branches on a clean worktree.</summary>
public sealed class GitRepositoryService
{
    public const int MaxReviewDiffCharacters = 40_000;
    public const int MaxReviewFiles = 40;
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
            output.AppendLine(await ReadDiffAsync(["diff", "--cached", "--no-ext-diff", "--no-color", "--", current.Path], cancellationToken, maxCharacters));
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
            output.AppendLine(await ReadDiffAsync(["diff", "--no-ext-diff", "--no-color", "--", current.Path], cancellationToken, maxCharacters));
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
        var result = await RunGitAsync(["add", "--", path], cancellationToken);
        EnsureSuccess(result, "Git could not stage this file.");
    }

    public async Task UnstageFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = await EnsureChangedPathAsync(path, cancellationToken);
        if (file.Staged == " ") throw new InvalidOperationException("This file has no staged changes.");
        var result = await RunGitAsync(["restore", "--staged", "--", path], cancellationToken);
        EnsureSuccess(result, "Git could not unstage this file.");
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

    private async Task<GitCommandResult> RunGitAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, int maxOutputCharacters = int.MaxValue)
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("The Git command took longer than 10 seconds.");
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

    private sealed record GitCommandResult(int ExitCode, string Output, string Error);
}

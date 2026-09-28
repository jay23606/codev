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

/// <summary>Reads Git state and switches only between existing local branches on a clean worktree.</summary>
public sealed class GitRepositoryService
{
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
    {
        var status = await GetStatusAsync(cancellationToken);
        var current = status.Files.FirstOrDefault(candidate => string.Equals(candidate.Path, file.Path, StringComparison.Ordinal));
        if (current is null) throw new InvalidOperationException("That file is no longer changed in the repository. Refresh Git status and select it again.");

        var output = new System.Text.StringBuilder();
        if (current.Staged != " ")
        {
            output.AppendLine("STAGED CHANGES");
            output.AppendLine(await ReadDiffAsync(["diff", "--cached", "--no-ext-diff", "--no-color", "--", current.Path], cancellationToken));
        }
        if (current.WorkingTree == "?")
        {
            output.AppendLine("UNTRACKED FILE");
            var diff = await RunGitAsync(["diff", "--no-index", "--no-ext-diff", "--no-color", "--", "/dev/null", current.Path], cancellationToken);
            if (diff.ExitCode is not 0 and not 1) EnsureSuccess(diff, "Git could not read this untracked file.");
            output.AppendLine(diff.Output);
        }
        else if (current.WorkingTree != " ")
        {
            output.AppendLine("UNSTAGED CHANGES");
            output.AppendLine(await ReadDiffAsync(["diff", "--no-ext-diff", "--no-color", "--", current.Path], cancellationToken));
        }
        return output.ToString().TrimEnd();
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

    private async Task<string> ReadDiffAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(arguments, cancellationToken);
        EnsureSuccess(result, "Git could not read the selected diff.");
        return result.Output;
    }

    private async Task<string> GetIndexTreeIdAsync(CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(["write-tree"], cancellationToken);
        EnsureSuccess(result, "Git could not snapshot the staged index for review.");
        return result.Output.Trim();
    }

    private async Task<GitCommandResult> RunGitAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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
            throw new InvalidOperationException("Git was not found. Install Git for Windows and ensure git.exe is on PATH.", ex);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
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

using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Codev;

public sealed record GitFileStatus(string Staged, string WorkingTree, string Path)
{
    public string State => $"{Staged}{WorkingTree}";
}

public sealed record GitRepositoryStatus(string Root, string Branch, string? Upstream, int Ahead, int Behind, IReadOnlyList<GitFileStatus> Files)
{
    public bool HasChanges => Files.Count > 0;
}

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
            if ((staged is "R" or "C" || workingTree is "R" or "C") && index + 1 < entries.Length)
                path += " ← " + entries[++index];
            files.Add(new GitFileStatus(staged, workingTree, path));
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

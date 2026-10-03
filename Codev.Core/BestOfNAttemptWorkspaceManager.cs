using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Codev;

public sealed record BestOfNAttemptSnapshot(Guid Id, string BaselineId, string SourceProjectPath, string RootPath,
    string? GitHeadCommit = null);
public sealed record BestOfNAttemptWorkspace(int AttemptNumber, string BaselineId, string IsolationId, string WorkspacePath);
public sealed record BestOfNAttemptWorkspaceReview(IReadOnlyList<CodeTaskFileProposal> Proposals,
    IReadOnlyList<string> BlockingReasons)
{
    public bool CanApply => BlockingReasons.Count == 0;
}

/// <summary>Captures a project tree once, then clones that exact snapshot into separate attempt directories.</summary>
public sealed class BestOfNAttemptWorkspaceManager
{
    public const int MaximumAttempts = BestOfNAttemptCoordinator.MaximumAttempts;
    public const int MaximumFiles = 200_000;
    public const int MaximumReviewChanges = 100;
    public const long MaximumBytes = 4L * 1024 * 1024 * 1024;

    private readonly string _root;
    public BestOfNAttemptWorkspaceManager(string localApplicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataPath);
        _root = Path.GetFullPath(Path.Combine(localApplicationDataPath, "Codev", "best-of-n-workspaces"));
    }

    public async Task<BestOfNAttemptSnapshot> CaptureAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var source = Path.GetFullPath(projectPath);
        EnsureOrdinaryDirectory(source, "The project folder cannot be a link.");
        EnsureStorageRoot();
        var gitHead = await ReadGitHeadIfRepositoryRootAsync(source, cancellationToken).ConfigureAwait(false);

        var id = Guid.NewGuid();
        var root = Path.Combine(_root, id.ToString("N"));
        var baseline = Path.Combine(root, "baseline");
        Directory.CreateDirectory(root);
        try
        {
            RestrictDirectoryToCurrentUser(root);
            await CopyTreeAsync(source, baseline, makeWritable: false, cancellationToken).ConfigureAwait(false);
            var baselineId = await ComputeTreeIdAsync(baseline, cancellationToken).ConfigureAwait(false);
            var sourceId = await ComputeTreeIdAsync(source, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(baselineId, sourceId, StringComparison.Ordinal))
                throw new IOException("The project tree changed while the attempt baseline was being captured.");
            if (gitHead is not null && !string.Equals(gitHead, await ReadGitHeadIfRepositoryRootAsync(source, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
                throw new IOException("The source repository moved to a different Git commit while the attempt baseline was being captured.");
            return new BestOfNAttemptSnapshot(id, baselineId, source, root, gitHead);
        }
        catch
        {
            TryDeleteOwnedDirectory(root);
            throw;
        }
    }

    public async Task<BestOfNAttemptWorkspace> CreateAttemptWorkspaceAsync(BestOfNAttemptSnapshot snapshot,
        int attemptNumber, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (attemptNumber is < 1 or > MaximumAttempts)
            throw new ArgumentOutOfRangeException(nameof(attemptNumber), $"Attempt number must be between 1 and {MaximumAttempts}.");
        ValidateSnapshot(snapshot);
        var baseline = Path.Combine(snapshot.RootPath, "baseline");
        var currentId = await ComputeTreeIdAsync(baseline, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(snapshot.BaselineId, currentId, StringComparison.Ordinal))
            throw new InvalidOperationException("The captured baseline changed; no attempt workspace was created.");

        var destination = Path.Combine(snapshot.RootPath, $"attempt-{attemptNumber}");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("An attempt workspace already exists for this snapshot and attempt number.");
        try
        {
            await CopyTreeAsync(baseline, destination, makeWritable: true, cancellationToken).ConfigureAwait(false);
            var copyId = await ComputeTreeIdAsync(destination, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(snapshot.BaselineId, copyId, StringComparison.Ordinal))
                throw new IOException("The attempt workspace does not match the captured project state.");
            if (snapshot.GitHeadCommit is not null)
                await InitializeAttemptGitContextAsync(snapshot, destination, attemptNumber, cancellationToken).ConfigureAwait(false);
            return new BestOfNAttemptWorkspace(attemptNumber, snapshot.BaselineId,
                Path.GetFullPath(destination), Path.GetFullPath(destination));
        }
        catch
        {
            TryDeleteOwnedDirectory(destination);
            throw;
        }
    }

    public void Delete(BestOfNAttemptSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);
        TryDeleteOwnedDirectory(snapshot.RootPath);
    }

    public async Task<BestOfNAttemptWorkspaceReview> ReviewChangesAsync(BestOfNAttemptSnapshot snapshot,
        BestOfNAttemptWorkspace workspace, WorkspaceFileService targetFiles,
        IReadOnlyList<string>? contextSources = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(targetFiles);
        ValidateSnapshot(snapshot);
        var expectedWorkspace = Path.GetFullPath(Path.Combine(snapshot.RootPath, $"attempt-{workspace.AttemptNumber}"));
        if (workspace.AttemptNumber is < 1 or > MaximumAttempts ||
            !string.Equals(workspace.BaselineId, snapshot.BaselineId, StringComparison.Ordinal) ||
            !string.Equals(workspace.WorkspacePath, expectedWorkspace, PathComparison) ||
            !string.Equals(workspace.IsolationId, expectedWorkspace, PathComparison))
            throw new InvalidOperationException("The attempt workspace does not belong to this captured project snapshot.");
        EnsureOrdinaryDirectory(expectedWorkspace, "The attempt workspace cannot be a link.");
        var actualBaselineId = await ComputeTreeIdAsync(Path.Combine(snapshot.RootPath, "baseline"), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(snapshot.BaselineId, actualBaselineId, StringComparison.Ordinal))
            throw new InvalidOperationException("The captured baseline changed during the attempt; no candidate can be reviewed safely.");
        if (!string.Equals(Path.GetFullPath(targetFiles.Root), Path.GetFullPath(snapshot.SourceProjectPath), PathComparison))
            throw new InvalidOperationException("Winner review must target the original project folder captured by this snapshot.");

        var baseline = await EnumerateSnapshotFilesAsync(Path.Combine(snapshot.RootPath, "baseline"), cancellationToken).ConfigureAwait(false);
        var candidate = await EnumerateSnapshotFilesAsync(expectedWorkspace, cancellationToken).ConfigureAwait(false);
        var paths = baseline.Keys.Concat(candidate.Keys).Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var proposals = new List<CodeTaskFileProposal>();
        var blockers = new List<string>();
        foreach (var relativePath in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hadOriginal = baseline.TryGetValue(relativePath, out var beforePath);
            var hasResult = candidate.TryGetValue(relativePath, out var afterPath);
            if (hadOriginal && !hasResult)
            {
                blockers.Add($"File deletion cannot be applied through the reviewed proposal path: {relativePath}");
                continue;
            }
            if (hadOriginal && hasResult && await FileContentsMatchAsync(beforePath!, afterPath!, cancellationToken).ConfigureAwait(false))
                continue;
            if (!targetFiles.IsSupportedContextFile(relativePath) || targetFiles.IsContextExcluded(relativePath) ||
                WorkspaceFileService.IsSensitiveFileName(Path.GetFileName(relativePath)))
            {
                blockers.Add($"Changed path is excluded from reviewed source-file writes: {relativePath}");
                continue;
            }

            string before;
            string after;
            try
            {
                before = hadOriginal ? await ReadBoundedTextFileAsync(beforePath!, cancellationToken).ConfigureAwait(false) : "";
                after = await ReadBoundedTextFileAsync(afterPath!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or InvalidOperationException)
            {
                blockers.Add($"Changed file cannot be reviewed as bounded UTF-8 text ({Path.GetFileName(relativePath)}): {ex.Message}");
                continue;
            }
            if (hadOriginal && string.Equals(before, after, StringComparison.Ordinal)) continue;

            string targetPath;
            try { targetPath = targetFiles.ResolvePath(relativePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                blockers.Add($"Changed path is not safe in the original project: {relativePath} ({ex.Message})");
                continue;
            }
            if (hadOriginal)
            {
                if (!File.Exists(targetPath))
                {
                    blockers.Add($"Original file was removed after the attempt baseline was captured: {relativePath}");
                    continue;
                }
                try
                {
                    var current = await targetFiles.ReadFileSnapshotAsync(relativePath, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(current.Content, before, StringComparison.Ordinal))
                    {
                        blockers.Add($"Original file changed after the attempt baseline was captured: {relativePath}");
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    blockers.Add($"Original file cannot be safely re-read for winner review: {relativePath} ({ex.Message})");
                    continue;
                }
            }
            else
            {
                if (File.Exists(targetPath))
                {
                    blockers.Add($"New file path now exists in the original project: {relativePath}");
                    continue;
                }
                if (!Directory.Exists(Path.GetDirectoryName(targetPath)))
                {
                    blockers.Add($"New file requires a directory that does not exist in the original project: {relativePath}");
                    continue;
                }
            }
            if (proposals.Count >= MaximumReviewChanges)
            {
                blockers.Add($"Candidate changed more than the {MaximumReviewChanges} files Codev can safely review at once.");
                break;
            }
            proposals.Add(new CodeTaskFileProposal(relativePath, before, after, IsNewFile: !hadOriginal,
                ContextSources: contextSources?.ToArray()));
        }
        return new BestOfNAttemptWorkspaceReview(proposals, blockers);
    }

    private async Task CopyTreeAsync(string source, string destination, bool makeWritable, CancellationToken cancellationToken)
    {
        EnsureOrdinaryDirectory(source, "Snapshot source folders cannot be links.");
        Directory.CreateDirectory(destination);
        RestrictDirectoryToCurrentUser(destination);
        EnsureOrdinaryDirectory(destination, "Snapshot destination cannot be a link.");

        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        var fileCount = 0;
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"Cannot capture a project containing a symbolic link or reparse point: {Path.GetRelativePath(source, entry)}");
                // Git metadata belongs to the original repository and is replaced by private attempt metadata.
                if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.GetFullPath(Path.Combine(current.Destination, name));
                EnsureWithinRoot(destination, target);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target);
                    pending.Push((entry, target));
                    continue;
                }

                if (++fileCount > MaximumFiles)
                    throw new InvalidOperationException($"Project snapshot exceeds the {MaximumFiles:N0}-file limit.");
                var length = new FileInfo(entry).Length;
                totalBytes = checked(totalBytes + length);
                if (totalBytes > MaximumBytes)
                    throw new InvalidOperationException($"Project snapshot exceeds the {MaximumBytes / (1024 * 1024 * 1024)} GiB limit.");
                await CopyFileAsync(entry, target, makeWritable, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string?> ReadGitHeadIfRepositoryRootAsync(string projectPath, CancellationToken cancellationToken)
    {
        var projectGitMarker = Path.Combine(projectPath, ".git");
        if ((Directory.Exists(projectGitMarker) || File.Exists(projectGitMarker)) &&
            (File.GetAttributes(projectGitMarker) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The project .git marker cannot be a symbolic link or reparse point.");
        var marker = projectPath;
        var hasGitMarker = false;
        while (!string.IsNullOrEmpty(marker))
        {
            if (HasGitMetadataMarker(marker))
            {
                hasGitMarker = true;
                break;
            }
            var parent = Path.GetDirectoryName(marker);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, marker, PathComparison)) break;
            marker = parent;
        }
        if (!hasGitMarker) return null;

        var rootOutput = await RunGitAsync(projectPath, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (rootOutput.ExitCode != 0)
            throw new InvalidOperationException("Git metadata was found above this project, but Git could not identify its worktree root; best-of-N cannot isolate it safely.");
        var gitRoot = Path.GetFullPath(rootOutput.StandardOutput.Trim());
        if (!string.Equals(gitRoot, Path.GetFullPath(projectPath), PathComparison))
            throw new InvalidOperationException("Best-of-N currently requires the attached project folder to be the Git repository root so each attempt can receive a private Git context.");
        var headOutput = await RunGitAsync(projectPath, cancellationToken, "rev-parse", "--verify", "HEAD^{commit}").ConfigureAwait(false);
        if (headOutput.ExitCode != 0 || string.IsNullOrWhiteSpace(headOutput.StandardOutput))
            throw new InvalidOperationException("Best-of-N requires the attached Git repository to have a committed HEAD.");
        return headOutput.StandardOutput.Trim();
    }

    private static bool HasGitMetadataMarker(string projectPath)
    {
        var marker = Path.Combine(projectPath, ".git");
        return Directory.Exists(marker)
            ? File.Exists(Path.Combine(marker, "HEAD"))
            : File.Exists(marker);
    }

    private async Task InitializeAttemptGitContextAsync(BestOfNAttemptSnapshot snapshot, string workspace,
        int attemptNumber, CancellationToken cancellationToken)
    {
        var clonePath = Path.Combine(snapshot.RootPath, $"git-context-{attemptNumber}");
        var templatePath = Path.Combine(snapshot.RootPath, "empty-git-template");
        if (Directory.Exists(clonePath) || File.Exists(clonePath))
            throw new IOException("A private Git context already exists for this attempt.");
        Directory.CreateDirectory(templatePath);
        try
        {
            var clone = await RunGitAsync(snapshot.SourceProjectPath, cancellationToken,
                "clone", "--shared", "--no-checkout", "--template", templatePath, "--", snapshot.SourceProjectPath, clonePath)
                .ConfigureAwait(false);
            if (clone.ExitCode != 0)
                throw new InvalidOperationException($"Could not create a private Git context for attempt {attemptNumber}: {BoundGitOutput(clone)}");

            var clonedHead = await RunGitAsync(clonePath, cancellationToken, "rev-parse", "--verify", "HEAD^{commit}").ConfigureAwait(false);
            if (clonedHead.ExitCode != 0 || !string.Equals(snapshot.GitHeadCommit, clonedHead.StandardOutput.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The source repository changed while the private attempt Git context was being created.");

            var removeRemote = await RunGitAsync(clonePath, cancellationToken, "remote", "remove", "origin").ConfigureAwait(false);
            if (removeRemote.ExitCode != 0)
                throw new InvalidOperationException($"Could not remove the source remote from the private attempt Git context: {BoundGitOutput(removeRemote)}");

            var gitDirectory = Path.Combine(clonePath, ".git");
            EnsureOrdinaryDirectory(gitDirectory, "The private attempt Git metadata cannot be a link.");
            var privateGitDirectory = Path.Combine(workspace, ".git");
            if (Directory.Exists(privateGitDirectory) || File.Exists(privateGitDirectory))
                throw new IOException("The captured project contains a .git entry inside the attempt workspace.");
            Directory.Move(gitDirectory, privateGitDirectory);

            var index = await RunGitAsync(workspace, cancellationToken, "read-tree", "HEAD").ConfigureAwait(false);
            if (index.ExitCode != 0)
                throw new InvalidOperationException($"Could not initialize the private attempt Git index: {BoundGitOutput(index)}");
            var prefix = await RunGitAsync(workspace, cancellationToken, "rev-parse", "--show-prefix").ConfigureAwait(false);
            if (prefix.ExitCode != 0 || !string.IsNullOrWhiteSpace(prefix.StandardOutput))
                throw new IOException("The private attempt Git context does not resolve to its isolated workspace root.");
        }
        finally
        {
            if (Directory.Exists(clonePath)) TryDeleteOwnedDirectory(clonePath);
            if (Directory.Exists(templatePath)) TryDeleteOwnedDirectory(templatePath);
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunGitAsync(string workingDirectory,
        CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Git for isolated best-of-N workspaces.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderr = process.StandardError.ReadToEndAsync(linked.Token);
        try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Git did not finish preparing the private attempt context within two minutes.");
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static string BoundGitOutput((int ExitCode, string StandardOutput, string StandardError) result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        detail = detail.Trim();
        return detail.Length <= 1_000 ? detail : detail[..1_000] + "…";
    }

    private static Task<Dictionary<string, string>> EnumerateSnapshotFilesAsync(string root, CancellationToken cancellationToken)
    {
        EnsureOrdinaryDirectory(root, "Attempt review folders cannot be links.");
        var files = new Dictionary<string, string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var name = Path.GetFileName(entry);
                if (WorkspaceFileService.IsIgnoredDirectory(name)) continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Attempt review cannot follow symbolic links or reparse points.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else
                {
                    if (files.Count >= MaximumFiles) throw new InvalidOperationException("Attempt review exceeded its file-count limit.");
                    files.Add(Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/'), entry);
                }
            }
        }
        return Task.FromResult(files);
    }

    private static async Task<string> ReadBoundedTextFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = FileHardLinkInspector.OpenSingleLinkReadStream(path, Path.GetFileName(path));
        if (stream.Length > 200_000) throw new InvalidOperationException("The file exceeds Codev's 200 KB reviewed-write limit.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        if (bytes.Length > 200_000) throw new InvalidOperationException("The file exceeds Codev's 200 KB reviewed-write limit.");
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
    }

    private static async Task<bool> FileContentsMatchAsync(string firstPath, string secondPath, CancellationToken cancellationToken)
    {
        await using var first = FileHardLinkInspector.OpenSingleLinkReadStream(firstPath, Path.GetFileName(firstPath));
        await using var second = FileHardLinkInspector.OpenSingleLinkReadStream(secondPath, Path.GetFileName(secondPath));
        if (first.Length != second.Length) return false;
        var firstHash = await SHA256.HashDataAsync(first, cancellationToken).ConfigureAwait(false);
        var secondHash = await SHA256.HashDataAsync(second, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static async Task CopyFileAsync(string source, string destination, bool makeWritable, CancellationToken cancellationToken)
    {
        await using var input = FileHardLinkInspector.OpenSingleLinkReadStream(source, Path.GetFileName(source));
        var initialLength = input.Length;
        var initialWriteTime = File.GetLastWriteTimeUtc(source);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (input.Length != initialLength || new FileInfo(source).Length != initialLength ||
            File.GetLastWriteTimeUtc(source) != initialWriteTime || output.Length != initialLength)
            throw new IOException($"Project file changed while the attempt baseline was being captured: {Path.GetFileName(source)}");
        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(source) & ~FileAttributes.ReparsePoint;
            if (makeWritable) attributes &= ~FileAttributes.ReadOnly;
            File.SetAttributes(destination, attributes);
        }
        else
        {
            var mode = File.GetUnixFileMode(source);
            if (makeWritable) mode |= UnixFileMode.UserWrite;
            File.SetUnixFileMode(destination, mode);
        }
    }

    private async Task<string> ComputeTreeIdAsync(string root, CancellationToken cancellationToken)
    {
        EnsureOrdinaryDirectory(root, "Snapshot folders cannot be links.");
        var entries = new List<(string Path, bool IsDirectory)>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)) continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Snapshot contains a symbolic link or reparse point.");
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                entries.Add((entry, isDirectory));
                if (isDirectory) pending.Push(entry);
            }
        }

        entries.Sort((left, right) => StringComparer.Ordinal.Compare(
            Path.GetRelativePath(root, left.Path).Replace(Path.DirectorySeparatorChar, '/'),
            Path.GetRelativePath(root, right.Path).Replace(Path.DirectorySeparatorChar, '/')));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long totalBytes = 0;
        var fileCount = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, entry.Path).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            if (entry.IsDirectory)
            {
                hash.AppendData([0x44]);
            }
            else
            {
                if (++fileCount > MaximumFiles)
                    throw new InvalidOperationException($"Project snapshot exceeds the {MaximumFiles:N0}-file limit.");
                hash.AppendData([0x46]);
                await using var input = FileHardLinkInspector.OpenSingleLinkReadStream(entry.Path, relative);
                var length = input.Length;
                hash.AppendData(Encoding.UTF8.GetBytes(length.ToString(CultureInfo.InvariantCulture)));
                hash.AppendData([0]);
                totalBytes = checked(totalBytes + length);
                if (totalBytes > MaximumBytes)
                    throw new InvalidOperationException($"Project snapshot exceeds the {MaximumBytes / (1024 * 1024 * 1024)} GiB limit.");
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    hash.AppendData(buffer, 0, read);
            }
            hash.AppendData([0xff]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void EnsureStorageRoot()
    {
        var codevRoot = Path.GetDirectoryName(_root)!;
        Directory.CreateDirectory(codevRoot);
        EnsureOrdinaryDirectory(codevRoot, "The Codev data folder cannot be a link.");
        Directory.CreateDirectory(_root);
        EnsureOrdinaryDirectory(_root, "The attempt-workspaces folder cannot be a link.");
        RestrictDirectoryToCurrentUser(_root);
    }

    private void ValidateSnapshot(BestOfNAttemptSnapshot snapshot)
    {
        var expectedRoot = Path.Combine(_root, snapshot.Id.ToString("N"));
        var suppliedRoot = Path.GetFullPath(snapshot.RootPath);
        if (!string.Equals(expectedRoot, suppliedRoot, PathComparison) || !Directory.Exists(expectedRoot))
            throw new InvalidOperationException("The attempt snapshot is not an existing Codev-managed workspace.");
        EnsureOrdinaryDirectory(_root, "The attempt-workspaces folder is a link.");
        EnsureOrdinaryDirectory(expectedRoot, "The attempt snapshot folder is a link.");
        EnsureOrdinaryDirectory(Path.Combine(expectedRoot, "baseline"), "The captured baseline folder is a link.");
        if (string.IsNullOrWhiteSpace(snapshot.BaselineId) || snapshot.BaselineId.Length != 64 ||
            snapshot.BaselineId.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("The captured baseline identifier is invalid.");
    }

    private void TryDeleteOwnedDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_root, full);
        if (Path.IsPathRooted(relative) || relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison) ||
            (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to delete a directory outside the managed attempt-workspaces root.");
        Directory.Delete(full, recursive: true);
    }

    private static void EnsureOrdinaryDirectory(string path, string message)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(message);
    }

    private static void RestrictDirectoryToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void EnsureWithinRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidOperationException("A project path resolved outside the captured attempt workspace.");
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

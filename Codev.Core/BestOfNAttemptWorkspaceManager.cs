using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Codev;

public sealed record BestOfNAttemptSnapshot(Guid Id, string BaselineId, string SourceProjectPath, string RootPath);
public sealed record BestOfNAttemptWorkspace(int AttemptNumber, string BaselineId, string IsolationId, string WorkspacePath);

/// <summary>Captures a project tree once, then clones that exact snapshot into separate attempt directories.</summary>
public sealed class BestOfNAttemptWorkspaceManager
{
    public const int MaximumAttempts = BestOfNAttemptCoordinator.MaximumAttempts;
    public const int MaximumFiles = 200_000;
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

        var id = Guid.NewGuid();
        var root = Path.Combine(_root, id.ToString("N"));
        var baseline = Path.Combine(root, "baseline");
        Directory.CreateDirectory(root);
        try
        {
            RestrictDirectoryToCurrentUser(root);
            await CopyTreeAsync(source, baseline, makeWritable: false, cancellationToken).ConfigureAwait(false);
            var baselineId = await ComputeTreeIdAsync(baseline, cancellationToken).ConfigureAwait(false);
            return new BestOfNAttemptSnapshot(id, baselineId, source, root);
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
                // Git metadata belongs to the original repository and cannot be shared by attempt copies.
                if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)) continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"Cannot capture a project containing a symbolic link or reparse point: {Path.GetRelativePath(source, entry)}");
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

    private static async Task CopyFileAsync(string source, string destination, bool makeWritable, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
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
                var length = new FileInfo(entry.Path).Length;
                hash.AppendData(Encoding.UTF8.GetBytes(length.ToString(CultureInfo.InvariantCulture)));
                hash.AppendData([0]);
                totalBytes = checked(totalBytes + length);
                if (totalBytes > MaximumBytes)
                    throw new InvalidOperationException($"Project snapshot exceeds the {MaximumBytes / (1024 * 1024 * 1024)} GiB limit.");
                await using var input = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
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

using System.Text.Json;

namespace Codev;

/// <summary>Persists explicit local trust decisions for project folders. A trusted root covers its descendants.</summary>
public sealed class ProjectFolderTrustRegistry
{
    private readonly string _path;
    private TrustSnapshot _snapshot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ProjectFolderTrustRegistry(string path, IEnumerable<string> roots, IEnumerable<string> knownFolders, bool canWrite, string? loadError)
    {
        _path = Path.GetFullPath(path);
        _snapshot = new TrustSnapshot(new HashSet<string>(roots, PathComparer), new HashSet<string>(knownFolders, PathComparer));
        CanWrite = canWrite;
        LoadError = loadError;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public bool CanWrite { get; }
    public string? LoadError { get; }
    public IReadOnlyList<string> TrustedRoots => Volatile.Read(ref _snapshot).TrustedRoots.OrderBy(path => path, PathComparer).ToArray();

    public static ProjectFolderTrustRegistry Load(string path)
    {
        if (!File.Exists(path)) return new ProjectFolderTrustRegistry(path, [], [], true, null);
        try
        {
            var data = JsonSerializer.Deserialize<TrustFile>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("The trust file must contain an object.");
            var roots = (data.TrustedRoots ?? []).Select(NormalizePath).Where(IsAllowedTrustRoot).ToArray();
            var known = (data.KnownFolders ?? []).Select(NormalizePath).ToArray();
            return new ProjectFolderTrustRegistry(path, roots, known, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return new ProjectFolderTrustRegistry(path, [], [], false, ex.Message);
        }
    }

    public string? FindTrustedRoot(string folder)
    {
        try
        {
            var candidate = NormalizePath(folder);
            return Volatile.Read(ref _snapshot).TrustedRoots
                .Where(root => ContainsPath(root, candidate) && ContainsNoLinkedPathSegments(root, candidate))
                .OrderByDescending(root => root.Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public bool IsTrusted(string folder) => FindTrustedRoot(folder) is not null;
    public bool IsKnown(string folder)
    {
        try { return Volatile.Read(ref _snapshot).KnownFolders.Contains(NormalizePath(folder)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public bool IsDirectTrustRoot(string folder)
    {
        try { return Volatile.Read(ref _snapshot).TrustedRoots.Contains(NormalizePath(folder)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public async Task TrustAsync(string folder, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(folder);
        if (!IsAllowedTrustRoot(normalized)) throw new InvalidOperationException("The filesystem root cannot be trusted. Choose a narrower project folder.");
        if (!ContainsNoLinkedPathSegments(normalized, normalized))
            throw new UnauthorizedAccessException("A project folder with a symbolic link or junction in its path cannot be trusted.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            var current = Volatile.Read(ref _snapshot);
            if (current.TrustedRoots.Contains(normalized) && current.KnownFolders.Contains(normalized)) return;
            var roots = new HashSet<string>(current.TrustedRoots, PathComparer) { normalized };
            var knownFolders = new HashSet<string>(current.KnownFolders, PathComparer) { normalized };
            await PublishAsync(new TrustSnapshot(roots, knownFolders), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkKnownAsync(string folder, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(folder);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            var current = Volatile.Read(ref _snapshot);
            if (current.KnownFolders.Contains(normalized)) return;
            var knownFolders = new HashSet<string>(current.KnownFolders, PathComparer) { normalized };
            await PublishAsync(current with { KnownFolders = knownFolders }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RevokeAsync(string folder, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(folder);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            var current = Volatile.Read(ref _snapshot);
            if (!current.TrustedRoots.Contains(normalized)) return;
            var roots = new HashSet<string>(current.TrustedRoots, PathComparer);
            roots.Remove(normalized);
            await PublishAsync(current with { TrustedRoots = roots }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task PublishAsync(TrustSnapshot next, CancellationToken cancellationToken)
    {
        var file = new TrustFile(next.TrustedRoots.OrderBy(path => path, PathComparer).ToArray(),
            next.KnownFolders.OrderBy(path => path, PathComparer).ToArray());
        await AtomicTextFile.WriteAsync(_path, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }), cancellationToken)
            .ConfigureAwait(false);
        Volatile.Write(ref _snapshot, next);
    }

    private void EnsureWritable()
    {
        if (!CanWrite) throw new InvalidOperationException($"Folder trust settings could not be loaded and were preserved: {LoadError}");
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A folder path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsAllowedTrustRoot(string path) =>
        !string.Equals(path, Path.GetPathRoot(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record TrustFile(string[]? TrustedRoots, string[]? KnownFolders);
    private sealed record TrustSnapshot(HashSet<string> TrustedRoots, HashSet<string> KnownFolders);

    private static bool ContainsPath(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static bool ContainsNoLinkedPathSegments(string root, string candidate)
    {
        try
        {
            var rootPath = Path.GetFullPath(root);
            var candidatePath = Path.GetFullPath(candidate);
            if (!ContainsPath(rootPath, candidatePath)) return false;
            var pathSegments = new Stack<string>();
            var current = candidatePath;
            var rootDirectory = Path.GetPathRoot(current);
            while (!string.IsNullOrEmpty(current))
            {
                pathSegments.Push(current);
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || PathComparer.Equals(parent, current)) break;
                current = parent;
            }
            while (pathSegments.Count > 0)
            {
                current = pathSegments.Pop();
                if ((Directory.Exists(current) || File.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

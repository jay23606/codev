using System.Text.Json;

namespace Codev;

/// <summary>Persists explicit local trust decisions for project folders. A trusted root covers its descendants.</summary>
public sealed class ProjectFolderTrustRegistry
{
    private readonly string _path;
    private readonly HashSet<string> _trustedRoots;
    private readonly HashSet<string> _knownFolders;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ProjectFolderTrustRegistry(string path, IEnumerable<string> roots, IEnumerable<string> knownFolders, bool canWrite, string? loadError)
    {
        _path = Path.GetFullPath(path);
        _trustedRoots = new HashSet<string>(roots, PathComparer);
        _knownFolders = new HashSet<string>(knownFolders, PathComparer);
        CanWrite = canWrite;
        LoadError = loadError;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public bool CanWrite { get; }
    public string? LoadError { get; }
    public IReadOnlyList<string> TrustedRoots => _trustedRoots.OrderBy(path => path, PathComparer).ToArray();

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
            return _trustedRoots
                .Where(root => ContainsPath(root, candidate) && ContainsNoLinkedPathSegments(root, candidate))
                .OrderByDescending(root => root.Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public bool IsTrusted(string folder) => FindTrustedRoot(folder) is not null;
    public bool IsKnown(string folder)
    {
        try { return _knownFolders.Contains(NormalizePath(folder)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public bool IsDirectTrustRoot(string folder)
    {
        try { return _trustedRoots.Contains(NormalizePath(folder)); }
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
            var addedRoot = _trustedRoots.Add(normalized);
            var addedKnown = _knownFolders.Add(normalized);
            if (!addedRoot && !addedKnown) return;
            try { await PersistAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                if (addedRoot) _trustedRoots.Remove(normalized);
                if (addedKnown) _knownFolders.Remove(normalized);
                throw;
            }
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
            if (!_knownFolders.Add(normalized)) return;
            try { await PersistAsync(cancellationToken).ConfigureAwait(false); }
            catch { _knownFolders.Remove(normalized); throw; }
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
            if (!_trustedRoots.Remove(normalized)) return;
            try { await PersistAsync(cancellationToken).ConfigureAwait(false); }
            catch { _trustedRoots.Add(normalized); throw; }
        }
        finally { _gate.Release(); }
    }

    private async Task PersistAsync(CancellationToken cancellationToken) =>
        await AtomicTextFile.WriteAsync(_path, JsonSerializer.Serialize(new TrustFile(TrustedRoots.ToArray(), _knownFolders.OrderBy(path => path, PathComparer).ToArray()), new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);

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

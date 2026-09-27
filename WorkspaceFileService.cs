using System.IO;
using System.Text;

namespace Codev;

/// <summary>Provides project-scoped file operations and refuses path escapes and link traversal.</summary>
public sealed class WorkspaceFileService
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "dist", "build", "coverage" };
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".xaml", ".csproj", ".sln", ".md", ".json", ".js", ".jsx", ".ts", ".tsx", ".py", ".html", ".css", ".sql", ".xml", ".yml", ".yaml", ".toml", ".props", ".targets", ".ps1", ".sh", ".bat" };

    private readonly string _root;

    public static bool IsSensitiveFileName(string name)
    {
        var fileName = Path.GetFileName(name).ToLowerInvariant();
        return fileName == ".env" || fileName.StartsWith(".env.", StringComparison.Ordinal) ||
               fileName.Contains("secret") || fileName.Contains("credential") ||
               fileName.EndsWith(".pem", StringComparison.Ordinal) || fileName.EndsWith(".pfx", StringComparison.Ordinal) ||
               fileName.EndsWith(".key", StringComparison.Ordinal) || fileName is "id_rsa" or "id_ed25519";
    }

    public WorkspaceFileService(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Project folder not found: {root}");
        var full = Path.GetFullPath(root);
        _root = string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public string Root => _root;

    public string ResolvePath(string relativePath, bool allowWorkspaceRoot = false)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            if (allowWorkspaceRoot) return _root;
            throw new ArgumentException("A project-relative file path is required.", nameof(relativePath));
        }

        var full = Path.GetFullPath(Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(_root, relativePath));
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            if (allowWorkspaceRoot) return _root;
            throw new UnauthorizedAccessException("The workspace folder itself is not a file.");
        }

        if (!IsPathWithinRoot(_root, full))
            throw new UnauthorizedAccessException("This tool only allows paths inside the selected project folder.");

        var relative = Path.GetRelativePath(_root, full);
        var current = _root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Project file operations do not follow symbolic links or junctions.");
        }

        if (IsSensitivePath(full))
            throw new UnauthorizedAccessException("This file looks like a secret or private key and is excluded from agent access.");
        return full;
    }

    public IReadOnlyList<string> ListFiles(string relativeDirectory = "", int maxEntries = 200)
    {
        var directory = ResolvePath(relativeDirectory, allowWorkspaceRoot: true);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Folder not found: {relativeDirectory}");
        var results = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0 && results.Count < maxEntries)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (results.Count >= maxEntries) break;
                var attributes = File.GetAttributes(entry);
                var name = Path.GetFileName(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!IgnoredDirectories.Contains(name)) pending.Push(entry);
                    continue;
                }
                if (IsSensitivePath(entry)) continue;
                results.Add(Path.GetRelativePath(_root, entry));
            }
        }
        return results;
    }

    public async Task<string> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var full = ResolvePath(relativePath);
        var info = new FileInfo(full);
        if (!info.Exists) throw new FileNotFoundException("File not found in the selected project.", relativePath);
        if (info.Length > 500_000) throw new InvalidOperationException("Files larger than 500 KB are not opened by the agent.");
        return await File.ReadAllTextAsync(full, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> SearchFilesAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Search text is required.", nameof(query));
        var matches = new List<string>();
        foreach (var relative in ListFiles(maxEntries: 500))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SourceExtensions.Contains(Path.GetExtension(relative))) continue;
            string[] lines;
            try
            {
                var full = ResolvePath(relative);
                if (new FileInfo(full).Length > 500_000) continue;
                lines = await File.ReadAllLinesAsync(full, cancellationToken);
            }
            catch { continue; }
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                matches.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                if (matches.Count >= 50) return matches;
            }
        }
        return matches;
    }

    public async Task<string?> CreateCheckpointAsync(string relativePath, Guid conversationId, CancellationToken cancellationToken = default)
    {
        var full = ResolvePath(relativePath);
        if (!File.Exists(full)) return null;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "checkpoints", conversationId.ToString("N"));
        Directory.CreateDirectory(directory);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)))[..12];
        var backup = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{hash}.bak");
        await using var source = File.OpenRead(full);
        await using var destination = File.Create(backup);
        await source.CopyToAsync(destination, cancellationToken);
        return backup;
    }

    public async Task WriteFileAtomicAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        if (content.Length > 200_000) throw new InvalidOperationException("Proposed file is larger than 200 KB.");
        var full = ResolvePath(relativePath);
        var parent = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The parent folder must already exist; Codev will not create new directory trees yet.");
        var temp = Path.Combine(parent, $".codev-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, content, cancellationToken);
            ResolvePath(relativePath);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public static bool IsPathWithinRoot(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var normalizedPath = Path.GetFullPath(path);
        var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
        return string.Equals(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSensitivePath(string path)
    {
        var relative = Path.GetRelativePath(_root, path);
        return relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(IsSensitiveFileName);
    }
}

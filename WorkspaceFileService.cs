using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Codev;

/// <summary>Provides project-scoped file operations and refuses path escapes and link traversal.</summary>
public sealed class WorkspaceFileService
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "dist", "build", "coverage" };
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".xaml", ".csproj", ".sln", ".md", ".txt", ".json", ".js", ".jsx", ".ts", ".tsx", ".py", ".html", ".css", ".sql", ".xml", ".yml", ".yaml", ".toml", ".props", ".targets", ".ps1", ".sh", ".bat" };

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
                if (!SourceExtensions.Contains(Path.GetExtension(entry))) continue;
                results.Add(Path.GetRelativePath(_root, entry));
            }
        }
        return results;
    }

    public async Task<string> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        return (await ReadFileSnapshotAsync(relativePath, cancellationToken)).Content;
    }

    public async Task<FileSnapshot> ReadFileSnapshotAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var full = ResolvePath(relativePath);
        if (!SourceExtensions.Contains(Path.GetExtension(full))) throw new InvalidOperationException("Only common source, text, and configuration files are opened by the agent.");
        var info = new FileInfo(full);
        if (!info.Exists) throw new FileNotFoundException("File not found in the selected project.", relativePath);
        if (info.Length > 500_000) throw new InvalidOperationException("Files larger than 500 KB are not opened by the agent.");
        var bytes = await File.ReadAllBytesAsync(full, cancellationToken);
        return new FileSnapshot(Encoding.UTF8.GetString(bytes), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
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

    public async Task<string> RunApprovedCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("A command is required.", nameof(command));
        if (command.Length > 4000) throw new InvalidOperationException("Commands longer than 4,000 characters are not allowed.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(3)) throw new ArgumentOutOfRangeException(nameof(timeout), "Command timeout must be at most three minutes.");

        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command }
        };
        using var process = new System.Diagnostics.Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("Could not start PowerShell.");
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var stdout = ReadLimitedAsync(process.StandardOutput, 10_000, linked.Token);
        var stderr = ReadLimitedAsync(process.StandardError, 10_000, linked.Token);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            try { await Task.WhenAll(stdout, stderr); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            return $"Command timed out after {timeout.TotalSeconds:0} seconds and was terminated.";
        }
        await Task.WhenAll(stdout, stderr);
        var output = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(stdout.Result)) output.AppendLine(stdout.Result);
        if (!string.IsNullOrWhiteSpace(stderr.Result)) output.AppendLine("STDERR:").AppendLine(stderr.Result);
        if (output.Length > 24_000) output.Length = 24_000;
        output.AppendLine().Append("Exit code: ").Append(process.ExitCode);
        return output.ToString();
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int maxCharacters, CancellationToken cancellationToken)
    {
        var output = new StringBuilder(Math.Min(maxCharacters, 4096));
        var buffer = new char[2048];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            var keep = Math.Min(count, maxCharacters - output.Length);
            if (keep > 0) output.Append(buffer, 0, keep);
        }
        return output.ToString();
    }

    public async Task<string?> CreateCheckpointAsync(string relativePath, Guid conversationId, CancellationToken cancellationToken = default, string? expectedHash = null)
    {
        var full = ResolvePath(relativePath);
        if (!File.Exists(full)) return null;
        var bytes = await File.ReadAllBytesAsync(full, cancellationToken);
        if (expectedHash is not null && !HashMatches(bytes, expectedHash))
            throw new IOException("The file changed while its proposed edit was being reviewed. Nothing was overwritten; please inspect it again.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "checkpoints", conversationId.ToString("N"));
        Directory.CreateDirectory(directory);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)))[..12];
        var backup = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{hash}.bak");
        await File.WriteAllBytesAsync(backup, bytes, cancellationToken);
        return backup;
    }

    public async Task WriteFileAtomicAsync(string relativePath, string content, CancellationToken cancellationToken = default, string? expectedOriginalHash = null, int maxCharacters = 200_000)
    {
        if (content.Length > maxCharacters) throw new InvalidOperationException($"Proposed file is larger than {maxCharacters / 1000} KB.");
        var full = ResolvePath(relativePath);
        if (!SourceExtensions.Contains(Path.GetExtension(full))) throw new InvalidOperationException("Only common source, text, and configuration files are edited by the agent.");
        if (expectedOriginalHash is not null && !await CurrentFileMatchesAsync(full, expectedOriginalHash, cancellationToken))
            throw new IOException("The file changed while its proposed edit was being reviewed. Nothing was overwritten; please inspect it again.");
        var parent = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The parent folder must already exist; Codev will not create new directory trees yet.");
        var temp = Path.Combine(parent, $".codev-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, content, cancellationToken);
            ResolvePath(relativePath);
            if (expectedOriginalHash is not null && !await CurrentFileMatchesAsync(full, expectedOriginalHash, cancellationToken))
                throw new IOException("The file changed while its proposed edit was being reviewed. Nothing was overwritten; please inspect it again.");
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public async Task<string> ReadCheckpointAsync(string relativePath, Guid conversationId, string checkpointPath, CancellationToken cancellationToken = default)
    {
        ResolvePath(relativePath);
        var path = ValidateCheckpointPath(conversationId, checkpointPath);
        if (new FileInfo(path).Length > 500_000) throw new InvalidOperationException("Checkpoint files larger than 500 KB cannot be restored.");
        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    public async Task<string> RestoreCheckpointAsync(string relativePath, Guid conversationId, string checkpointPath, string expectedCurrentHash, CancellationToken cancellationToken = default)
    {
        var checkpoint = ValidateCheckpointPath(conversationId, checkpointPath);
        if (new FileInfo(checkpoint).Length > 500_000) throw new InvalidOperationException("Checkpoint files larger than 500 KB cannot be restored.");
        var restored = await File.ReadAllTextAsync(checkpoint, cancellationToken);
        var rollback = await CreateCheckpointAsync(relativePath, conversationId, cancellationToken, expectedCurrentHash);
        await WriteFileAtomicAsync(relativePath, restored, cancellationToken, expectedCurrentHash, maxCharacters: 500_000);
        return rollback ?? throw new IOException("Could not save a rollback checkpoint before restoring.");
    }

    private static string ValidateCheckpointPath(Guid conversationId, string checkpointPath)
    {
        var expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "checkpoints", conversationId.ToString("N"));
        if (!IsPathWithinRoot(expectedRoot, checkpointPath) || !File.Exists(checkpointPath) || (File.GetAttributes(checkpointPath) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("The checkpoint is not a valid local backup for this conversation.");
        return Path.GetFullPath(checkpointPath);
    }

    private static async Task<bool> CurrentFileMatchesAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        return HashMatches(await File.ReadAllBytesAsync(path, cancellationToken), expectedHash);
    }

    private static bool HashMatches(byte[] bytes, string expectedHash) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(expectedHash));

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

public sealed record FileSnapshot(string Content, string Sha256);

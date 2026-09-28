using System.IO;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;

namespace Codev;

/// <summary>Provides project-scoped file operations and refuses path escapes and link traversal.</summary>
public sealed class WorkspaceFileService
{
    public const int MaxContextFiles = 24;
    public const int MaxContextCharacters = 32_000;
    public const int MaxContextFileCharacters = 2_400;
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "dist", "build", "coverage" };
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // .NET, JavaScript/TypeScript, and common web/configuration files.
        ".cs", ".xaml", ".csproj", ".sln", ".cshtml", ".razor", ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts",
        ".html", ".css", ".scss", ".sass", ".less", ".vue", ".svelte", ".md", ".mdx", ".txt", ".json", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf", ".properties", ".props", ".targets",
        // Common compiled and interpreted languages.
        ".py", ".pyi", ".go", ".rs", ".java", ".kt", ".kts", ".swift", ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hxx", ".m", ".mm",
        ".php", ".rb", ".lua", ".pl", ".pm", ".scala", ".sc", ".dart", ".ex", ".exs", ".erl", ".hrl", ".clj", ".cljs", ".cljc", ".hs", ".lhs", ".elm", ".r", ".jl",
        ".f", ".f90", ".for", ".pas", ".pp", ".asm", ".s", ".sql", ".proto", ".graphql", ".gql", ".tf", ".hcl", ".nix", ".ps1", ".sh", ".bat"
    };

    private readonly string _root;
    private readonly string[] _contextExclusions;

    public static bool IsSensitiveFileName(string name)
    {
        var fileName = Path.GetFileName(name).ToLowerInvariant();
        return fileName == ".env" || fileName.StartsWith(".env.", StringComparison.Ordinal) ||
               fileName.Contains("secret") || fileName.Contains("credential") ||
               fileName.EndsWith(".pem", StringComparison.Ordinal) || fileName.EndsWith(".pfx", StringComparison.Ordinal) ||
               fileName.EndsWith(".key", StringComparison.Ordinal) || fileName is "id_rsa" or "id_ed25519";
    }

    public static bool IsIgnoredDirectory(string name) => IgnoredDirectories.Contains(name);

    public WorkspaceFileService(string root, IReadOnlyList<string>? contextExclusions = null)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Project folder not found: {root}");
        var full = Path.GetFullPath(root);
        _root = string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _contextExclusions = (contextExclusions ?? []).Select(NormalizeExclusion).Where(value => value is not null).Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string Root => _root;
    public IReadOnlyList<string> ContextExclusions => _contextExclusions;

    public bool IsSupportedContextFile(string relativePath) =>
        !string.IsNullOrWhiteSpace(relativePath) && SourceExtensions.Contains(Path.GetExtension(relativePath));

    public string ResolvePath(string relativePath, bool allowWorkspaceRoot = false)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            if (allowWorkspaceRoot) return _root;
            throw new ArgumentException("A project-relative file path is required.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath) || IsWindowsRootedPath(relativePath))
            throw new UnauthorizedAccessException("This tool only allows paths inside the selected project folder.");
        var platformRelativePath = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_root, platformRelativePath));
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

    private static bool IsWindowsRootedPath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] is '\\' or '/');

    public IReadOnlyList<string> ListFiles(string relativeDirectory = "", int maxEntries = 200)
        => ListFilesCore(relativeDirectory, maxEntries, applyContextExclusions: false);

    /// <summary>Lists immediate, visible project entries without following links or exposing excluded/secret paths.</summary>
    public IReadOnlyList<string> ListDirectoryEntries(string relativeDirectory = "", int maxEntries = 200)
    {
        var directory = ResolvePath(relativeDirectory, allowWorkspaceRoot: true);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Folder not found: {relativeDirectory}");
        var results = new List<string>();
        var maximum = Math.Clamp(maxEntries, 1, 200);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Take(10_000))
        {
            if (results.Count >= maximum) break;
            var attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            var name = Path.GetFileName(entry);
            if (string.IsNullOrWhiteSpace(name) || name[0] == '.' || IsSensitiveFileName(name)) continue;
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (isDirectory && IsIgnoredDirectory(name)) continue;
            var relative = Path.GetRelativePath(_root, entry);
            if (IsContextExcluded(relative)) continue;
            results.Add(isDirectory ? name + Path.DirectorySeparatorChar : name);
        }
        return results.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> ListContextFiles(int maxEntries = 300)
        => ListFilesCore("", maxEntries, applyContextExclusions: true);

    private IReadOnlyList<string> ListFilesCore(string relativeDirectory, int maxEntries, bool applyContextExclusions)
    {
        var directory = ResolvePath(relativeDirectory, allowWorkspaceRoot: true);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Folder not found: {relativeDirectory}");
        var results = new List<string>();
        var pending = new Stack<string>();
        const int maxScannedEntries = 10_000;
        var scannedEntries = 0;
        pending.Push(directory);
        while (pending.Count > 0 && results.Count < maxEntries && scannedEntries < maxScannedEntries)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current).Take(maxScannedEntries - scannedEntries).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (results.Count >= maxEntries || scannedEntries >= maxScannedEntries) break;
                scannedEntries++;
                var attributes = File.GetAttributes(entry);
                var name = Path.GetFileName(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var relative = Path.GetRelativePath(_root, entry);
                    if (!IsIgnoredDirectory(name) && (!applyContextExclusions || !IsContextExcluded(relative))) pending.Push(entry);
                    continue;
                }
                if (IsSensitivePath(entry)) continue;
                if (!SourceExtensions.Contains(Path.GetExtension(entry))) continue;
                var relativeFile = Path.GetRelativePath(_root, entry);
                if (applyContextExclusions && IsContextExcluded(relativeFile)) continue;
                results.Add(relativeFile);
            }
        }
        return results;
    }

    /// <summary>Estimates source-file tokens using bounded sizes; it does not include chat history or prompt instructions.</summary>
    public int EstimateContextTokens(IReadOnlyList<string>? selectedFiles = null)
    {
        var files = selectedFiles is { Count: > 0 } ? selectedFiles : ListContextFiles(maxEntries: 300);
        var estimatedChars = 0;
        var fileCount = 0;
        foreach (var relative in files)
        {
            if (fileCount >= MaxContextFiles || estimatedChars >= MaxContextCharacters) break;
            if (!SourceExtensions.Contains(Path.GetExtension(relative)) || IsContextExcluded(relative)) continue;
            try
            {
                var info = new FileInfo(ResolvePath(relative));
                if (!info.Exists) continue;
                estimatedChars = Math.Min(MaxContextCharacters, estimatedChars + Math.Min(MaxContextFileCharacters, (int)Math.Min(int.MaxValue, info.Length)) + relative.Length + 16);
                fileCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return (estimatedChars + 3) / 4;
    }

    public bool IsContextExcluded(string relativePath)
    {
        string normalized;
        try { normalized = NormalizeRelativePath(relativePath); }
        catch { return false; }
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var rule in _contextExclusions)
        {
            if (rule.Contains('*') || rule.Contains('?'))
            {
                if (!rule.Contains('/') && segments.Length > 0 && FileSystemName.MatchesSimpleExpression(rule, segments[^1], ignoreCase: true)) return true;
            }
            else if (rule.Contains('/'))
            {
                if (normalized.Equals(rule, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(rule + "/", StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (segments.Any(segment => segment.Equals(rule, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    public static bool IsValidContextExclusion(string? value) => NormalizeExclusion(value) is not null;

    private static string? NormalizeExclusion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 || Path.IsPathRooted(value)) return null;
        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || normalized.Contains(':')) return null;
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..")) return null;
        if ((normalized.Contains('*') || normalized.Contains('?')) && normalized.Contains('/')) return null;
        return normalized;
    }

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) throw new ArgumentException("A project-relative path is required.", nameof(value));
        var normalized = value.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or "..")) throw new ArgumentException("A normalized project-relative path is required.", nameof(value));
        return string.Join('/', segments);
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
        var matches = await SearchFilesCoreAsync(query, ListFiles(maxEntries: 500), cancellationToken);
        return matches.Select(match => match.ToString()).ToArray();
    }

    public Task<IReadOnlyList<FileSearchMatch>> SearchContextFilesAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Search text is required.", nameof(query));
        return SearchFilesCoreAsync(query, ListContextFiles(maxEntries: 500), cancellationToken);
    }

    private async Task<IReadOnlyList<FileSearchMatch>> SearchFilesCoreAsync(string query, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Search text is required.", nameof(query));
        var matches = new List<FileSearchMatch>();
        foreach (var relative in files)
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
            catch (OperationCanceledException) { throw; }
            catch { continue; }
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var line = lines[i].Trim();
                if (line.Length > 320) line = line[..320] + "…";
                matches.Add(new FileSearchMatch(relative, i + 1, line));
                if (matches.Count >= 50) return matches;
            }
        }
        return matches;
    }

    public async Task<string> RunApprovedCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default, IProgress<TimeSpan>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("A command is required.", nameof(command));
        if (command.Length > 4000) throw new InvalidOperationException("Commands longer than 4,000 characters are not allowed.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(3)) throw new ArgumentOutOfRangeException(nameof(timeout), "Command timeout must be at most three minutes.");

        var shell = ShellCommandResolver.ResolveCurrent();
        var start = shell.CreateStartInfo(command, _root);
        using var process = new System.Diagnostics.Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {shell.DisplayName}.");
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        var stdout = ReadLimitedAsync(process.StandardOutput, 10_000, linked.Token);
        var stderr = ReadLimitedAsync(process.StandardError, 10_000, linked.Token);
        var progressTask = progress is null ? Task.CompletedTask : ReportCommandProgressAsync(progress, progressCts.Token);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            try { await Task.WhenAll(stdout, stderr); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            return $"Command timed out after {timeout.TotalSeconds:0} seconds and was terminated.";
        }
        finally
        {
            progressCts.Cancel();
            try { await progressTask; } catch (OperationCanceledException) { }
        }
        await Task.WhenAll(stdout, stderr);
        var output = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(stdout.Result)) output.AppendLine(stdout.Result);
        if (!string.IsNullOrWhiteSpace(stderr.Result)) output.AppendLine("STDERR:").AppendLine(stderr.Result);
        if (output.Length > 24_000) output.Length = 24_000;
        output.AppendLine().Append("Exit code: ").Append(process.ExitCode);
        return output.ToString();
    }

    private static async Task ReportCommandProgressAsync(IProgress<TimeSpan> progress, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            progress.Report(stopwatch.Elapsed);
        }
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

    public async Task CreateFileAtomicAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        if (content.Length > 200_000) throw new InvalidOperationException("Proposed file is larger than 200 KB.");
        var full = ResolvePath(relativePath);
        if (!SourceExtensions.Contains(Path.GetExtension(full))) throw new InvalidOperationException("Only common source, text, and configuration files can be created by the agent.");
        if (File.Exists(full)) throw new IOException("A file already exists at this path. Review it as an edit instead.");
        var parent = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The parent folder must already exist; Codev will not create new directory trees yet.");
        var temp = Path.Combine(parent, $".codev-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, content, cancellationToken);
            ResolvePath(relativePath);
            File.Move(temp, full, overwrite: false);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
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

    public async Task<string?> RestoreFileStateAsync(string relativePath, Guid conversationId, bool previousFileExisted, string? checkpointPath, string? expectedCurrentHash, CancellationToken cancellationToken = default)
    {
        var full = ResolvePath(relativePath);
        if (!SourceExtensions.Contains(Path.GetExtension(full))) throw new InvalidOperationException("Only common source, text, and configuration files are restored by the agent.");
        var currentExists = File.Exists(full);
        if (currentExists)
        {
            if (expectedCurrentHash is null || !await CurrentFileMatchesAsync(full, expectedCurrentHash, cancellationToken))
                throw new IOException("The file changed since the last review. Nothing was overwritten; review its current contents first.");
        }
        else if (expectedCurrentHash is not null)
            throw new IOException("The file changed since the last review. Nothing was overwritten; review its current contents first.");

        var rollback = currentExists ? await CreateCheckpointAsync(relativePath, conversationId, cancellationToken, expectedCurrentHash) : null;
        if (currentExists && rollback is null) throw new IOException("Could not save a rollback checkpoint.");

        if (previousFileExisted)
        {
            if (string.IsNullOrWhiteSpace(checkpointPath)) throw new FileNotFoundException("The saved checkpoint for this change is missing.");
            var checkpoint = ValidateCheckpointPath(conversationId, checkpointPath);
            if (new FileInfo(checkpoint).Length > 500_000) throw new InvalidOperationException("Checkpoint files larger than 500 KB cannot be restored.");
            var content = await File.ReadAllTextAsync(checkpoint, cancellationToken);
            if (currentExists) await WriteFileAtomicAsync(relativePath, content, cancellationToken, expectedCurrentHash, maxCharacters: 500_000);
            else await CreateFileAtomicAsync(relativePath, content, cancellationToken);
        }
        else
        {
            if (!currentExists) return null;
            if (!await CurrentFileMatchesAsync(full, expectedCurrentHash!, cancellationToken))
                throw new IOException("The file changed while it was being removed. Nothing was deleted; review its current contents first.");
            File.Delete(full);
        }
        return rollback;
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
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
        return string.Equals(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison) ||
               normalizedPath.StartsWith(rootPrefix, comparison);
    }

    private bool IsSensitivePath(string path)
    {
        var relative = Path.GetRelativePath(_root, path);
        return relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(IsSensitiveFileName);
    }
}

public sealed record FileSnapshot(string Content, string Sha256);

public sealed record FileSearchMatch(string RelativePath, int LineNumber, string LineText)
{
    public override string ToString() => $"{RelativePath}:{LineNumber}: {LineText}";
}

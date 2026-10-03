using System.Text;

namespace Codev;

public sealed record AgentProfileDocument(string FileName, string Contents);

/// <summary>Reads and validates editable user-level Markdown agent profiles.</summary>
public sealed class UserAgentProfileStore(string directory)
{
    public const int MaxDocuments = AgentProfileCatalog.MaxProfilesPerScope;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _directory = Path.GetFullPath(directory);

    public async Task<IReadOnlyList<AgentProfileDocument>> LoadDocumentsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory)) return [];
        EnsureSafeDirectory();
        var paths = Directory.EnumerateFiles(_directory, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDocuments + 1)
            .ToArray();
        if (paths.Length > MaxDocuments) throw new InvalidDataException($"At most {MaxDocuments} user agent profiles can be edited.");

        var documents = new List<AgentProfileDocument>(paths.Length);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRegularFile(path);
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > AgentProfileCatalog.MaxProfileFileBytes)
                throw new InvalidDataException($"Agent profile '{Path.GetFileName(path)}' exceeds the 32 KB size limit.");
            try { documents.Add(new AgentProfileDocument(Path.GetFileName(path), StrictUtf8.GetString(bytes))); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException($"Agent profile '{Path.GetFileName(path)}' is not valid UTF-8.", ex); }
        }
        return documents;
    }

    public async Task SaveAsync(string fileName, string contents, CancellationToken cancellationToken = default)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fileName, stem + ".md", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(AgentProfileCatalog.NormalizeProfileFileStem(stem), stem, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Use a simple profile filename such as 'reviewer.md'.");
        if (Encoding.UTF8.GetByteCount(contents) > AgentProfileCatalog.MaxProfileFileBytes)
            throw new InvalidDataException("Agent profile files are limited to 32 KB.");
        if (!AgentProfileCatalog.TryParse(fileName, contents, "user", out var profile, out var error) || profile is null)
            throw new InvalidDataException(error);
        if (profile.Name.Equals("Orchestrator", StringComparison.OrdinalIgnoreCase) ||
            profile.Name.Equals("Plan", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{profile.Name} is a reserved built-in profile name.");

        EnsureNoLinkedPathComponents();
        Directory.CreateDirectory(_directory);
        EnsureSafeDirectory();
        var target = Path.Combine(_directory, stem + ".md");
        EnsureRegularFile(target);
        var existingPaths = Directory.EnumerateFiles(_directory, "*.md", SearchOption.TopDirectoryOnly)
            .Take(MaxDocuments + 1)
            .ToArray();
        if (existingPaths.Length > MaxDocuments)
            throw new InvalidDataException($"At most {MaxDocuments} user agent profiles can be saved.");
        var targetExists = existingPaths.Any(existing => Path.GetFullPath(existing).Equals(Path.GetFullPath(target), PathComparison));
        if (!targetExists && existingPaths.Length >= MaxDocuments)
            throw new InvalidDataException($"At most {MaxDocuments} user agent profiles can be saved.");
        foreach (var existing in existingPaths)
        {
            if (Path.GetFullPath(existing).Equals(Path.GetFullPath(target), PathComparison)) continue;
            EnsureRegularFile(existing);
            if (new FileInfo(existing).Length > AgentProfileCatalog.MaxProfileFileBytes) continue;
            var bytes = await File.ReadAllBytesAsync(existing, cancellationToken).ConfigureAwait(false);
            try
            {
                var text = StrictUtf8.GetString(bytes);
                if (AgentProfileCatalog.TryParse(Path.GetFileName(existing), text, "user", out var other, out _) &&
                    other?.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase) == true)
                    throw new InvalidDataException($"A user profile named '{profile.Name}' already exists in {Path.GetFileName(existing)}.");
            }
            catch (DecoderFallbackException) { }
        }
        await AtomicTextFile.WriteAsync(target, contents, cancellationToken).ConfigureAwait(false);
    }

    private StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private void EnsureSafeDirectory()
    {
        EnsureNoLinkedPathComponents();
        if (!Directory.Exists(_directory)) throw new DirectoryNotFoundException("The user agent profile directory could not be created.");
    }

    private void EnsureNoLinkedPathComponents()
    {
        var current = Path.GetPathRoot(_directory) ?? throw new InvalidDataException("The user profile directory path is invalid.");
        foreach (var segment in _directory[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) && IsReparsePoint(current))
                throw new InvalidDataException("The user agent profile directory cannot contain symbolic links.");
        }
    }

    private static void EnsureRegularFile(string path)
    {
        if (IsReparsePoint(path)) throw new InvalidDataException($"Agent profile '{Path.GetFileName(path)}' is a symbolic link and cannot be edited.");
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}

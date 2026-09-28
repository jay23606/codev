using System.Text;

namespace Codev;

public sealed record ProjectSkillLoadResult(IReadOnlyList<SlashCommandDefinition> Skills, IReadOnlyList<string> Warnings);

/// <summary>Discovers manually-invoked Markdown skills. Skill files are prompt data only; scripts are never executed.</summary>
public static class ProjectSkillCatalog
{
    public const int MaxSkillsPerScope = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<ProjectSkillLoadResult> LoadAsync(string userSkillsDirectory, string? projectRoot,
        bool includeProjectSkills, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var user = await LoadScopeAsync(userSkillsDirectory, "user", warnings, cancellationToken).ConfigureAwait(false);
        var project = new List<SlashCommandDefinition>();
        if (includeProjectSkills && !string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
        {
            var skillsDirectory = Path.Combine(Path.GetFullPath(projectRoot), ".codev", "skills");
            if (IsSafeProjectSkillsDirectory(projectRoot, skillsDirectory))
                project = await LoadScopeAsync(skillsDirectory, "project", warnings, cancellationToken).ConfigureAwait(false);
            else
                warnings.Add("Project skills were skipped because .codev/skills is a symbolic link or resolves outside the project.");
        }

        var merged = new List<SlashCommandDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in project.Concat(user))
            if (names.Add(skill.Name)) merged.Add(skill);
        return new ProjectSkillLoadResult(merged, warnings);
    }

    public static async Task<SlashCommandExpansionResult> ReadPromptAsync(SlashCommandDefinition skill,
        string userSkillsDirectory, string? projectRoot, bool projectTrusted, string? invocation,
        CancellationToken cancellationToken = default)
    {
        if (skill.Scope is not ("skill-user" or "skill-project") || !skill.Name.StartsWith("/skill-", StringComparison.OrdinalIgnoreCase))
            return new(false, "", "This is not a discovered skill.");

        string root;
        if (skill.Scope == "skill-user") root = Path.GetFullPath(userSkillsDirectory);
        else
        {
            if (!projectTrusted || string.IsNullOrWhiteSpace(projectRoot))
                return new(false, "", "Project skills require a trusted attached project.");
            root = Path.Combine(Path.GetFullPath(projectRoot), ".codev", "skills");
            if (!IsSafeProjectSkillsDirectory(projectRoot, root))
                return new(false, "", "The project skills folder is not a safe local folder.");
        }

        var directoryName = skill.Name["/skill-".Length..];
        var skillDirectory = Path.Combine(root, directoryName);
        var skillFile = Path.Combine(skillDirectory, "SKILL.md");
        if (!IsContained(root, skillFile) || !File.Exists(skillFile) ||
            IsReparsePoint(root) || IsReparsePoint(skillDirectory) || IsReparsePoint(skillFile))
            return new(false, "", "That skill file is missing or uses a symbolic link.");

        try
        {
            var bytes = await ReadBoundedAsync(skillFile, cancellationToken).ConfigureAwait(false);
            var text = StrictUtf8.GetString(bytes);
            if (!CustomSlashCommandService.TryParseFile("skill-" + directoryName + ".md", text,
                    skill.Scope == "skill-project" ? "project" : "user", out var parsed, out var error) || parsed is null)
                return new(false, "", "That skill file is no longer valid: " + error);
            if (!parsed.Name.Equals(skill.Name, StringComparison.OrdinalIgnoreCase))
                return new(false, "", "That skill has changed names. Refresh the slash suggestions and try again.");
            return CustomSlashCommandService.Expand(parsed, invocation);
        }
        catch (DecoderFallbackException)
        {
            return new(false, "", "That skill file is not valid UTF-8.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, "", "That skill file could not be read.");
        }
    }

    private static async Task<List<SlashCommandDefinition>> LoadScopeAsync(string directory, string scope,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var skills = new List<SlashCommandDefinition>();
        try
        {
            if (!Directory.Exists(directory)) return skills;
            if (IsReparsePoint(directory))
            {
                warnings.Add($"The {scope} skills folder was skipped because it is a symbolic link.");
                return skills;
            }

            var directories = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                .Take(MaxSkillsPerScope + 1).ToArray();
            if (directories.Length > MaxSkillsPerScope)
                warnings.Add($"Only the first {MaxSkillsPerScope} {scope} skills are loaded.");
            foreach (var skillDirectory in directories.Take(MaxSkillsPerScope).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(skillDirectory);
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][a-z0-9_-]{0,39}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                {
                    warnings.Add($"Skill folder '{name}' has an invalid name and was skipped.");
                    continue;
                }
                var skillFile = Path.Combine(skillDirectory, "SKILL.md");
                try
                {
                    if (IsReparsePoint(skillDirectory))
                    {
                        warnings.Add($"Skill '{name}' was skipped because its folder is a symbolic link.");
                        continue;
                    }
                    if (!File.Exists(skillFile)) continue;
                    if (IsReparsePoint(skillFile))
                    {
                        warnings.Add($"Skill '{name}' was skipped because SKILL.md is a symbolic link.");
                        continue;
                    }
                    if (new FileInfo(skillFile).Length > CustomSlashCommandService.MaxCommandFileBytes)
                    {
                        warnings.Add($"Skill '{name}' exceeds the size limit and was skipped.");
                        continue;
                    }
                    var bytes = await ReadBoundedAsync(skillFile, cancellationToken).ConfigureAwait(false);
                    var text = StrictUtf8.GetString(bytes);
                    if (CustomSlashCommandService.TryParseFile("skill-" + name + ".md", text, scope, out var parsed, out var error) && parsed is not null)
                        skills.Add(parsed with { Prompt = null, Scope = scope == "project" ? "skill-project" : "skill-user", FilePath = skillFile });
                    else warnings.Add($"Skill '{name}' was skipped: {error}");
                }
                catch (DecoderFallbackException)
                {
                    warnings.Add($"Skill '{name}' is not valid UTF-8 and was skipped.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    warnings.Add($"Skill '{name}' could not be read and was skipped.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            warnings.Add($"The {scope} skills folder could not be read.");
        }
        return skills;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > CustomSlashCommandService.MaxCommandFileBytes)
            throw new IOException("Skill file exceeds the size limit.");
        var buffer = new byte[CustomSlashCommandService.MaxCommandFileBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        if (total > CustomSlashCommandService.MaxCommandFileBytes) throw new IOException("Skill file exceeds the size limit.");
        return buffer[..total];
    }

    private static bool IsSafeProjectSkillsDirectory(string projectRoot, string skillsDirectory)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(skillsDirectory).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        var current = Path.GetFullPath(projectRoot);
        foreach (var segment in new[] { ".codev", "skills" })
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && IsReparsePoint(current)) return false;
        }
        return true;
    }

    private static bool IsContained(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }
}

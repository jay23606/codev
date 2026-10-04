using System.Text;

namespace Codev;

public sealed record ProjectSkillLoadResult(IReadOnlyList<SlashCommandDefinition> Skills, IReadOnlyList<string> Warnings);

/// <summary>Discovers reusable Markdown skills. Skill files are prompt data only; scripts are never executed.</summary>
public static class ProjectSkillCatalog
{
    public const int MaxSkillsPerScope = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] ProjectSkillPaths =
    [
        Path.Combine(".codev", "skills"),
        Path.Combine(".opencode", "skills"),
        Path.Combine(".claude", "skills"),
        Path.Combine(".agents", "skills")
    ];

    public static IReadOnlyList<string> GetCompatibleUserSkillDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) return [];
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot) || !Path.IsPathRooted(configRoot))
            configRoot = Path.Combine(home, ".config");
        var openCodeConfig = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(openCodeConfig) || !Path.IsPathRooted(openCodeConfig))
            openCodeConfig = Path.Combine(configRoot, "opencode");
        return
        [
            Path.Combine(openCodeConfig, "skills"),
            Path.Combine(home, ".claude", "skills"),
            Path.Combine(home, ".agents", "skills")
        ];
    }

    public static async Task<ProjectSkillLoadResult> LoadAsync(string userSkillsDirectory, string? projectRoot,
        bool includeProjectSkills, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? additionalUserSkillsDirectories = null)
    {
        var warnings = new List<string>();
        var user = new List<SlashCommandDefinition>();
        foreach (var directory in new[] { userSkillsDirectory }.Concat(additionalUserSkillsDirectories ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (user.Count >= MaxSkillsPerScope) break;
            if (!HasNoLinkedPathSegments(directory))
            {
                warnings.Add("A user skills folder was skipped because it is a symbolic link or has a linked ancestor.");
                continue;
            }
            user.AddRange(await LoadScopeAsync(directory, "user", warnings, cancellationToken, MaxSkillsPerScope - user.Count).ConfigureAwait(false));
        }
        var project = new List<SlashCommandDefinition>();
        if (includeProjectSkills && !string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot) && HasNoLinkedPathSegments(projectRoot))
        {
            foreach (var relativePath in ProjectSkillPaths)
            {
                var skillsDirectory = Path.Combine(Path.GetFullPath(projectRoot), relativePath);
                if (!IsSafeProjectSkillsDirectory(projectRoot, skillsDirectory, relativePath))
                {
                    warnings.Add($"Project skills in '{relativePath.Replace('\\', '/')}' were skipped because the folder is a symbolic link or resolves outside the project.");
                    continue;
                }
                var remaining = MaxSkillsPerScope - project.Count;
                if (remaining <= 0) break;
                project.AddRange(await LoadScopeAsync(skillsDirectory, "project", warnings, cancellationToken, remaining, projectRoot).ConfigureAwait(false));
            }
        }

        var merged = new List<SlashCommandDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in project.Concat(user))
            if (names.Add(skill.Name)) merged.Add(skill);
        return new ProjectSkillLoadResult(merged, warnings);
    }

    public static async Task<SlashCommandExpansionResult> ReadPromptAsync(SlashCommandDefinition skill,
        string userSkillsDirectory, string? projectRoot, bool projectTrusted, string? invocation,
        CancellationToken cancellationToken = default, IReadOnlyList<string>? additionalUserSkillsDirectories = null)
    {
        if (skill.Scope is not ("skill-user" or "skill-project") || !skill.Name.StartsWith("/skill-", StringComparison.OrdinalIgnoreCase))
            return new(false, "", "This is not a discovered skill.");

        string root;
        if (skill.Scope == "skill-user") root = Path.GetFullPath(userSkillsDirectory);
        else
        {
            if (!projectTrusted || string.IsNullOrWhiteSpace(projectRoot) || !HasNoLinkedPathSegments(projectRoot))
                return new(false, "", "Project skills require a trusted attached project.");
            root = Path.GetFullPath(projectRoot);
        }

        var directoryName = skill.Name["/skill-".Length..];
        var skillFile = skill.FilePath;
        if (skill.Scope == "skill-user")
        {
            var allowedRoots = new List<string> { root };
            foreach (var directory in additionalUserSkillsDirectories ?? [])
            {
                try { allowedRoots.Add(Path.GetFullPath(directory)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
            skillFile ??= Path.Combine(root, directoryName, "SKILL.md");
            var containingRoot = allowedRoots.FirstOrDefault(candidate => HasNoLinkedPathSegments(candidate) &&
                IsContained(candidate, skillFile) && SamePath(Path.Combine(candidate, directoryName, "SKILL.md"), skillFile));
            var skillDirectory = Path.GetDirectoryName(skillFile);
            if (containingRoot is null || skillDirectory is null || IsReparsePoint(containingRoot) || IsReparsePoint(skillDirectory))
                return new(false, "", "That skill file is missing or uses a symbolic link.");
        }
        else
        {
            if (skillFile is null)
                skillFile = Path.Combine(root, ProjectSkillPaths[0], directoryName, "SKILL.md");
            if (!TryGetSafeProjectSkillDirectory(projectRoot!, skillFile, out var skillRoot, out var skillDirectory) ||
                !IsContained(skillRoot, skillFile) || !SamePath(Path.Combine(skillRoot, directoryName, "SKILL.md"), skillFile) ||
                IsReparsePoint(skillRoot) || IsReparsePoint(skillDirectory))
                return new(false, "", "That skill file is missing or uses a symbolic link.");
        }
        if (!File.Exists(skillFile) || IsReparsePoint(skillFile))
            return new(false, "", "That skill file is missing or uses a symbolic link.");

        try
        {
            var projectRootForFile = skill.Scope == "skill-project" ? root : null;
            var bytes = await ReadBoundedAsync(skillFile, cancellationToken, projectRootForFile).ConfigureAwait(false);
            var text = StrictUtf8.GetString(bytes);
            if (!TryParseSkillFile(directoryName, text, skill.Scope == "skill-project" ? "project" : "user",
                    out var parsed, out var error) || parsed is null)
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
        List<string> warnings, CancellationToken cancellationToken, int maxSkills = MaxSkillsPerScope, string? projectRoot = null)
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
                .Take(maxSkills + 1).ToArray();
            if (directories.Length > maxSkills)
                warnings.Add($"Only the first {maxSkills} {scope} skills from this folder are loaded.");
            foreach (var skillDirectory in directories.Take(maxSkills).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
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
                    var bytes = await ReadBoundedAsync(skillFile, cancellationToken, scope == "project" ? projectRoot : null).ConfigureAwait(false);
                    var text = StrictUtf8.GetString(bytes);
                    if (TryParseSkillFile(name, text, scope, out var parsed, out var error) && parsed is not null)
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

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken, string? projectRoot = null)
    {
        await using Stream stream = projectRoot is null
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan)
            : FileHardLinkInspector.OpenSingleLinkReadStream(path, Path.GetRelativePath(projectRoot, path),
                FileHardLinkInspector.GetCanonicalDirectoryPath(projectRoot));
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

    private static bool TryParseSkillFile(string directoryName, string contents, string scope,
        out SlashCommandDefinition? parsed, out string error)
    {
        parsed = null;
        error = "";
        var normalized = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length < 4 || lines[0].Trim() != "---")
            return CustomSlashCommandService.TryParseFile("skill-" + directoryName + ".md", contents, scope, out parsed, out error);
        var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (end < 0) return CustomSlashCommandService.TryParseFile("skill-" + directoryName + ".md", contents, scope, out parsed, out error);

        string? declaredName = null;
        var sanitized = new List<string> { lines[0] };
        var skipIndentedContinuation = false;
        foreach (var line in lines.Skip(1).Take(end - 1))
        {
            var trimmed = line.Trim();
            if (skipIndentedContinuation && (line.Length == 0 || char.IsWhiteSpace(line[0]))) continue;
            skipIndentedContinuation = false;
            if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                declaredName = UnquoteFrontmatterValue(trimmed[(trimmed.IndexOf(':') + 1)..].Trim());
                continue;
            }
            if (trimmed.StartsWith("license:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("compatibility:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("metadata:", StringComparison.OrdinalIgnoreCase))
            {
                skipIndentedContinuation = true;
                continue;
            }
            sanitized.Add(line);
        }
        sanitized.Add(lines[end]);
        sanitized.AddRange(lines.Skip(end + 1));
        if (declaredName is not null && !declaredName.Equals(directoryName, StringComparison.Ordinal))
        {
            error = "The skill name in frontmatter must match its folder name.";
            return false;
        }
        return CustomSlashCommandService.TryParseFile("skill-" + directoryName + ".md", string.Join('\n', sanitized),
            scope, out parsed, out error);
    }

    private static string UnquoteFrontmatterValue(string value) =>
        value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"' ? value[1..^1] : value;

    private static bool IsSafeProjectSkillsDirectory(string projectRoot, string skillsDirectory, string relativePath)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(skillsDirectory).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        var current = Path.GetFullPath(projectRoot);
        foreach (var segment in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && IsReparsePoint(current)) return false;
        }
        return true;
    }

    private static bool TryGetSafeProjectSkillDirectory(string projectRoot, string skillFile, out string skillsRoot, out string skillDirectory)
    {
        skillsRoot = "";
        skillDirectory = Path.GetDirectoryName(skillFile) ?? "";
        var fullFile = Path.GetFullPath(skillFile);
        foreach (var relativePath in ProjectSkillPaths)
        {
            var candidate = Path.Combine(Path.GetFullPath(projectRoot), relativePath);
            if (!IsSafeProjectSkillsDirectory(projectRoot, candidate, relativePath) || !IsContained(candidate, fullFile)) continue;
            skillsRoot = candidate;
            var current = Path.GetFullPath(projectRoot);
            foreach (var segment in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (IsReparsePoint(current)) return false;
            }
            return !IsReparsePoint(skillDirectory);
        }
        return false;
    }

    private static bool IsContained(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }

    private static bool HasNoLinkedPathSegments(string path)
    {
        try
        {
            var current = Path.GetFullPath(path);
            while (true)
            {
                if ((Directory.Exists(current) || File.Exists(current)) && IsReparsePoint(current)) return false;
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return true;
                current = parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

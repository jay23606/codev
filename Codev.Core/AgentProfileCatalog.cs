using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Codev;

public enum AgentToolPermission
{
    Ask,
    Allow,
    Deny
}

public enum AgentToolProfileDecision
{
    DeferToProjectPolicy,
    ApprovedOnce,
    Denied,
    Rejected
}

public sealed record AgentProfile(
    string Name,
    string Description,
    string? Model,
    double? Temperature,
    int? MaxSteps,
    AgentToolPermission DefaultPermission,
    IReadOnlyDictionary<string, AgentToolPermission> ToolPermissions,
    string Instructions,
    string Scope,
    string FilePath,
    IReadOnlyList<string>? AllowedEditPaths = null,
    IReadOnlyList<string>? DeniedEditPaths = null,
    IReadOnlyDictionary<string, AgentToolPermission>? CommandPermissions = null);

public sealed record AgentProfileLoadResult(IReadOnlyList<AgentProfile> Profiles, IReadOnlyList<string> Warnings);

public static class AgentProfilePolicy
{
    /// <summary>Profile Allow defers to Codev's project mode; profile Ask adds a one-call confirmation outside Auto; Deny always blocks.</summary>
    public static AgentToolPermission PermissionFor(AgentProfile? profile, string toolName, string? command = null)
    {
        if (profile is null) return AgentToolPermission.Allow;
        var permission = profile.DefaultPermission;
        foreach (var (pattern, rule) in profile.ToolPermissions)
        {
            var matches = pattern.Equals("mcp:*", StringComparison.OrdinalIgnoreCase)
                ? toolName.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase)
                : pattern.Contains('*')
                    ? AgentProfileCatalog.GlobMatches(pattern, toolName)
                    : pattern.Equals(toolName, StringComparison.OrdinalIgnoreCase);
            if (matches) permission = rule;
        }
        if ((toolName is "run_command" or "verify_command" or "start_background_command") && command is not null)
        {
            foreach (var (pattern, rule) in profile.CommandPermissions ?? new Dictionary<string, AgentToolPermission>())
                if (AgentProfileCatalog.CommandPatternMatches(pattern, command)) permission = rule;
        }
        return permission;
    }

    public static bool IsAvailable(AgentProfile? profile, string toolName) =>
        PermissionFor(profile, toolName) != AgentToolPermission.Deny;

    public static bool RequiresOneCallApproval(AgentToolPermission permission, ProjectCommandPermissionMode projectMode) =>
        permission == AgentToolPermission.Ask && projectMode != ProjectCommandPermissionMode.Auto;

    /// <summary>Denials take precedence; an empty allow list leaves paths unrestricted.</summary>
    public static bool CanEditPath(AgentProfile? profile, string relativePath, string projectRoot)
    {
        if (profile is null) return true;
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot,
            relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
        var normalized = Path.GetRelativePath(projectRoot, fullPath).Replace('\\', '/');
        if ((profile.DeniedEditPaths ?? []).Any(pattern => AgentProfileCatalog.PathPatternMatches(pattern, normalized))) return false;
        var allowed = profile.AllowedEditPaths ?? [];
        return allowed.Count == 0 || allowed.Any(pattern => AgentProfileCatalog.PathPatternMatches(pattern, normalized));
    }
}

/// <summary>Loads bounded Markdown agent profiles. Profile files are prompt and policy data only; they are never executed.</summary>
public static class AgentProfileCatalog
{
    public const int MaxProfilesPerScope = 64;
    public const int MaxProfileFileBytes = 32 * 1024;
    public const int MaxInstructionsCharacters = 20_000;
    public const int MaxToolPolicies = 128;
    public const int MaxEditPathPatterns = 40;
    public const int MaxCommandPatterns = 40;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex SafeName = new("^[a-z][a-z0-9_-]{0,39}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SafeTool = new("^(?:[a-z][a-z0-9_*-?]{0,63}|mcp:\\*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CommandClauseSeparators = new("(?:&&|\\|\\||[;|])", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    public static IReadOnlyList<AgentProfile> BuiltInProfiles { get; } =
    [
        BuiltIn("Code", "General coding work under Codev's selected project permissions.",
            "Inspect relevant files, make focused changes, and verify the result. Respect the user's request and Codev's permission decisions.", AgentToolPermission.Allow),
        BuiltIn("Debug", "Reproduce a problem, test explanations, then make a focused fix.",
            "Start by reproducing the reported problem. State a testable explanation, inspect relevant evidence, change one thing at a time, and verify the fix. Do not hide or work around a failing check.", AgentToolPermission.Allow,
            ("run_command", AgentToolPermission.Ask), ("verify_command", AgentToolPermission.Ask), ("start_background_command", AgentToolPermission.Ask)),
        BuiltIn("Ask", "Inspect and explain without changing files or running shell commands.",
            "Answer by inspecting the selected project as needed. Do not edit or create files, apply patches, or run shell commands. MCP calls require their normal approval policy.", AgentToolPermission.Allow,
            ("create_file", AgentToolPermission.Deny), ("write_file", AgentToolPermission.Deny), ("apply_patch", AgentToolPermission.Deny),
            ("run_command", AgentToolPermission.Deny), ("verify_command", AgentToolPermission.Deny), ("start_background_command", AgentToolPermission.Deny), ("mcp:*", AgentToolPermission.Ask)),
        BuiltIn("Orchestrator", "Delegate bounded subtasks to isolated child conversations.",
            "Break the user's task into bounded, independent subtasks and delegate them using delegate_task. Each child starts immediately in its own Git worktree and runs concurrently with this response; up to three children may run. Children cannot create more children. The parent may finish before child results arrive; use returned results in a follow-up when needed. Do not claim a child is complete until its result appears.",
            AgentToolPermission.Deny, ("delegate_task", AgentToolPermission.Allow)),
        BuiltIn("Plan", "Inspect a project and propose a plan without changing files or running commands.",
            "Analyze the user's request and relevant project files. Return a concrete, ordered implementation plan and note uncertainties. Do not edit files, run shell commands, call MCP tools, or delegate work.",
            AgentToolPermission.Deny,
            ("list_files", AgentToolPermission.Allow), ("read_file", AgentToolPermission.Allow), ("search_files", AgentToolPermission.Allow))
    ];

    public static string? NormalizeReferenceName(string? name) =>
        name is { Length: <= 40 } && SafeName.IsMatch(name) ? name : null;

    /// <summary>Moves the former built-in Code profile selection to the new default Build agent without overriding custom profiles.</summary>
    public static string? MigrateBuiltInCodeSelection(string? selectedName, IReadOnlyList<AgentProfile> resolvedProfiles)
    {
        if (!string.Equals(selectedName, "Code", StringComparison.OrdinalIgnoreCase)) return selectedName;
        return resolvedProfiles.Any(profile => profile.Name.Equals("Code", StringComparison.OrdinalIgnoreCase) && profile.Scope != "built-in")
            ? selectedName
            : resolvedProfiles.Any(profile => profile.Name.Equals("Code", StringComparison.OrdinalIgnoreCase) && profile.Scope == "built-in")
                ? null
                : selectedName;
    }

    public static async Task<AgentProfileLoadResult> LoadAsync(string userProfilesDirectory, string? projectRoot,
        bool includeProjectProfiles, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var user = await LoadScopeAsync(userProfilesDirectory, "user", warnings, cancellationToken).ConfigureAwait(false);
        var project = new List<AgentProfile>();
        if (includeProjectProfiles && !string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot) &&
            HasNoLinkedPathSegments(projectRoot))
        {
            var path = Path.Combine(Path.GetFullPath(projectRoot), ".codev", "agents");
            if (IsSafeProjectDirectory(projectRoot, path))
                project = await LoadScopeAsync(path, "project", warnings, cancellationToken).ConfigureAwait(false);
            else warnings.Add("Project agent profiles were skipped because .codev/agents is a symbolic link or resolves outside the project.");
        }

        var merged = new List<AgentProfile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in project.Concat(user).Concat(BuiltInProfiles))
        {
            if ((profile.Name.Equals("Orchestrator", StringComparison.OrdinalIgnoreCase) ||
                 profile.Name.Equals("Plan", StringComparison.OrdinalIgnoreCase)) && profile.Scope != "built-in")
            {
                warnings.Add($"The reserved built-in agent profile name '{profile.Name}' cannot be overridden.");
                continue;
            }
            if (names.Add(profile.Name)) merged.Add(profile);
        }
        return new AgentProfileLoadResult(merged, warnings);
    }

    public static bool TryParse(string fileName, string contents, string scope, out AgentProfile? profile, out string error)
    {
        profile = null;
        error = "";
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !SafeName.IsMatch(name))
            return Fail("Use a Markdown filename with a simple agent name.", out error);
        if (contents.Length > MaxProfileFileBytes) return Fail("Agent profile files are limited to 32 KB.", out error);

        var lines = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length < 4 || lines[0].Trim() != "---") return Fail("Start the profile with frontmatter between --- lines.", out error);
        var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (end < 0) return Fail("Agent profile frontmatter is missing its closing --- line.", out error);

        string? displayName = null;
        string? description = null;
        string? model = null;
        double? temperature = null;
        int? maxSteps = null;
        var defaultPermission = AgentToolPermission.Ask;
        var permissions = new Dictionary<string, AgentToolPermission>(StringComparer.OrdinalIgnoreCase);
        var allowedEditPaths = new List<string>();
        var deniedEditPaths = new List<string>();
        var commandPermissions = new Dictionary<string, AgentToolPermission>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1).Take(end - 1))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var colon = trimmed.IndexOf(':');
            if (colon <= 0) return Fail("Each frontmatter field must use key: value syntax.", out error);
            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim().Trim('"', '\'');
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) displayName = value;
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
            else if (key.Equals("model", StringComparison.OrdinalIgnoreCase)) model = value;
            else if (key.Equals("temperature", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed) || parsed is < 0 or > 2)
                    return Fail("temperature must be a number from 0 through 2.", out error);
                temperature = parsed;
            }
            else if (key.Equals("max_steps", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > CodeTaskLimits.MaxModelStepsPerTurn)
                    return Fail($"max_steps must be from 1 through {CodeTaskLimits.MaxModelStepsPerTurn}.", out error);
                maxSteps = parsed;
            }
            else if (key.Equals("default_permission", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParsePermission(value, out defaultPermission)) return Fail("default_permission must be ask, allow, or deny.", out error);
            }
            else if (key.Equals("tools", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var entry in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var equals = entry.LastIndexOf('=');
                    if (equals < 1 || !SafeTool.IsMatch(entry[..equals].Trim()) || !TryParsePermission(entry[(equals + 1)..].Trim(), out var permission))
                        return Fail("tools must be comma-separated tool=ask|allow|deny entries using simple tool names.", out error);
                    var tool = entry[..equals].Trim();
                    if (permissions.ContainsKey(tool)) return Fail($"Tool '{tool}' has more than one permission entry.", out error);
                    if (permissions.Count == MaxToolPolicies) return Fail($"Profiles may define at most {MaxToolPolicies} tool policies.", out error);
                    permissions.Add(tool, permission);
                }
            }
            else if (key.Equals("edit_paths", StringComparison.OrdinalIgnoreCase) || key.Equals("deny_edit_paths", StringComparison.OrdinalIgnoreCase))
            {
                var target = key.Equals("edit_paths", StringComparison.OrdinalIgnoreCase) ? allowedEditPaths : deniedEditPaths;
                foreach (var entry in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!IsValidPathPattern(entry)) return Fail($"{key} must contain safe project-relative glob patterns.", out error);
                    if (target.Count == MaxEditPathPatterns) return Fail($"{key} supports at most {MaxEditPathPatterns} patterns.", out error);
                    if (target.Contains(entry, StringComparer.OrdinalIgnoreCase)) return Fail($"{key} contains a duplicate pattern.", out error);
                    target.Add(entry);
                }
            }
            else if (key.Equals("commands", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var entry in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var equals = entry.LastIndexOf('=');
                    if (equals < 1 || !IsValidCommandPattern(entry[..equals].Trim()) ||
                        !TryParsePermission(entry[(equals + 1)..].Trim(), out var permission))
                        return Fail("commands must be comma-separated command_pattern=ask|allow|deny entries.", out error);
                    var pattern = entry[..equals].Trim();
                    if (commandPermissions.Count == MaxCommandPatterns) return Fail($"Profiles may define at most {MaxCommandPatterns} command rules.", out error);
                    if (!commandPermissions.TryAdd(pattern, permission)) return Fail($"Command pattern '{pattern}' has more than one permission entry.", out error);
                }
            }
            else return Fail($"Unsupported agent profile field '{key}'.", out error);
        }

        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 80 || displayName.Any(char.IsControl))
            return Fail("Add a display name of 1–80 printable characters.", out error);
        if (string.IsNullOrWhiteSpace(description) || description.Length > 180)
            return Fail("Add a short description of at most 180 characters.", out error);
        if (model is { Length: > 160 } || model?.Any(char.IsControl) == true) return Fail("The model value is too long or contains control characters.", out error);
        var instructions = string.Join('\n', lines.Skip(end + 1)).Trim();
        if (instructions.Length > MaxInstructionsCharacters) return Fail("Profile instructions exceed the size limit.", out error);
        if (instructions.Length == 0) return Fail("Add the agent instructions after the frontmatter.", out error);

        profile = new AgentProfile(displayName, description, string.IsNullOrWhiteSpace(model) ? null : model,
            temperature, maxSteps, defaultPermission, permissions, instructions, scope, "",
            allowedEditPaths, deniedEditPaths, commandPermissions);
        return true;
    }

    internal static bool PathPatternMatches(string pattern, string relativePath)
    {
        return GlobMatches(pattern.Replace('\\', '/'), relativePath.Replace('\\', '/'), pathPattern: true);
    }

    internal static bool GlobMatches(string pattern, string value, bool pathPattern = false)
    {
        var escaped = Regex.Escape(pattern);
        var regex = "^" + escaped.Replace("\\*\\*", ".*")
            .Replace("\\*", pathPattern ? "[^/]*" : ".*")
            .Replace("\\?", pathPattern ? "[^/]" : ".") + "$";
        var options = RegexOptions.CultureInvariant | (pathPattern && !OperatingSystem.IsWindows() ? RegexOptions.None : RegexOptions.IgnoreCase);
        return Regex.IsMatch(value, regex, options, TimeSpan.FromMilliseconds(100));
    }

    internal static bool CommandPatternMatches(string pattern, string command)
    {
        var normalizedPattern = NormalizeCommandWhitespace(pattern);
        var regex = "^" + Regex.Escape(normalizedPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        var options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
        return CommandClauseSeparators.Split(command).Any(clause =>
            Regex.IsMatch(NormalizeCommandWhitespace(clause), regex, options, TimeSpan.FromMilliseconds(100)));
    }

    private static string NormalizeCommandWhitespace(string command) =>
        string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsValidCommandPattern(string pattern) =>
        pattern.Length is > 0 and <= 200 && !pattern.Any(char.IsControl);

    private static bool IsValidPathPattern(string pattern)
    {
        if (pattern.Length is 0 or > 160 || pattern.StartsWith('/') || Path.IsPathRooted(pattern) ||
            pattern.Any(char.IsControl) || pattern.Contains(':') || pattern.Contains('\\')) return false;
        var segments = pattern.Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not ("." or "..") &&
            !segment.Contains('[') && !segment.Contains(']'));
    }

    private static async Task<List<AgentProfile>> LoadScopeAsync(string directory, string scope, List<string> warnings,
        CancellationToken cancellationToken)
    {
        var result = new List<AgentProfile>();
        try
        {
            if (!Directory.Exists(directory)) return result;
            if (IsReparsePoint(directory)) { warnings.Add($"The {scope} agent profile directory is a symbolic link and was skipped."); return result; }
            var files = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly).Take(MaxProfilesPerScope + 1).ToArray();
            if (files.Length > MaxProfilesPerScope) warnings.Add($"Only the first {MaxProfilesPerScope} {scope} agent profiles are loaded.");
            foreach (var path in files.Take(MaxProfilesPerScope).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (IsReparsePoint(path)) { warnings.Add($"Agent profile '{Path.GetFileName(path)}' is a symbolic link and was skipped."); continue; }
                    if (new FileInfo(path).Length > MaxProfileFileBytes) { warnings.Add($"Agent profile '{Path.GetFileName(path)}' exceeds the size limit and was skipped."); continue; }
                    var text = StrictUtf8.GetString(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                    if (TryParse(Path.GetFileName(path), text, scope, out var profile, out var error) && profile is not null)
                        result.Add(profile with { FilePath = path });
                    else warnings.Add($"Agent profile '{Path.GetFileName(path)}' was skipped: {error}");
                }
                catch (DecoderFallbackException) { warnings.Add($"Agent profile '{Path.GetFileName(path)}' is not valid UTF-8 and was skipped."); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { warnings.Add($"Agent profile '{Path.GetFileName(path)}' could not be read."); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { warnings.Add($"The {scope} agent profile directory could not be read."); }
        return result;
    }

    private static bool IsSafeProjectDirectory(string projectRoot, string profileDirectory)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(profileDirectory).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        var current = Path.GetFullPath(projectRoot);
        foreach (var segment in new[] { ".codev", "agents" })
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && IsReparsePoint(current)) return false;
        }
        return true;
    }

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
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return true;
                current = parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool TryParsePermission(string value, out AgentToolPermission permission) =>
        Enum.TryParse(value, ignoreCase: true, out permission) && Enum.IsDefined(permission);

    private static AgentProfile BuiltIn(string name, string description, string instructions, AgentToolPermission defaultPermission,
        params (string Tool, AgentToolPermission Permission)[] rules) =>
        new(name, description, null, null, null, defaultPermission,
            rules.ToDictionary(rule => rule.Tool, rule => rule.Permission, StringComparer.OrdinalIgnoreCase),
            instructions, "built-in", "");

    private static bool Fail(string message, out string error) { error = message; return false; }
}

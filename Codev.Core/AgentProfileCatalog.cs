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
    IReadOnlyDictionary<string, AgentToolPermission>? CommandPermissions = null,
    string Mode = "all",
    IReadOnlyList<OpenCodeAgentPermissionRule>? OpenCodePermissionRules = null);

/// <summary>An ordered permission rule imported from an OpenCode V2 Markdown agent.</summary>
public sealed record OpenCodeAgentPermissionRule(string Action, string Resource, AgentToolPermission Permission);

public sealed record AgentProfileLoadResult(IReadOnlyList<AgentProfile> Profiles, IReadOnlyList<string> Warnings);

public static class AgentProfilePolicy
{
    /// <summary>Profile Allow defers to Codev's project mode; Ask adds a one-call confirmation outside Auto; the last matching rule determines the permission.</summary>
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
        foreach (var rule in profile.OpenCodePermissionRules ?? [])
        {
            if (rule.Action.Equals("shell", StringComparison.OrdinalIgnoreCase))
            {
                if (toolName is "run_command" or "verify_command" or "start_background_command")
                {
                    if (command is null)
                    {
                        // A command-scoped allow keeps the shell tool available; the same ordered
                        // rules are evaluated again with the actual command before execution.
                        if (rule.Resource == "*" || rule.Permission != AgentToolPermission.Deny) permission = rule.Permission;
                    }
                    else if (AgentProfileCatalog.OpenCodeShellRuleMatches(rule.Resource, command, rule.Permission))
                    {
                        permission = rule.Permission;
                    }
                }
                continue;
            }

            if (AgentProfileCatalog.OpenCodeToolsForPermission(rule.Action).Any(pattern =>
                    AgentProfileCatalog.OpenCodeToolPatternMatches(pattern, toolName)))
            {
                if (rule.Resource == "*") permission = rule.Permission;
                else if (rule.Action.Equals("edit", StringComparison.OrdinalIgnoreCase) &&
                         rule.Permission != AgentToolPermission.Deny && IsEditTool(toolName))
                {
                    // Keep edit tools in the model schema when a V2 profile allows only selected
                    // paths. CanEditPath applies the same ordered rules to each proposed path.
                    permission = rule.Permission;
                }
            }
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
        if (allowed.Count > 0 && !allowed.Any(pattern => AgentProfileCatalog.PathPatternMatches(pattern, normalized))) return false;

        var permission = AgentToolPermission.Allow;
        foreach (var rule in profile.OpenCodePermissionRules ?? [])
        {
            if (rule.Action is not ("*" or "edit") ||
                !AgentProfileCatalog.PathPatternMatches(rule.Resource, normalized)) continue;
            permission = rule.Permission;
        }
        return permission != AgentToolPermission.Deny;
    }

    private static bool IsEditTool(string toolName) => toolName is "create_file" or "write_file" or "apply_patch";
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
    private static readonly Regex CommandClauseSeparators = new("(?:&&|&|\\|\\||[;|\\r\\n])", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string[] ProjectAgentPaths = [Path.Combine(".codev", "agents"), Path.Combine(".opencode", "agents")];

    public static IReadOnlyList<string> GetCompatibleUserAgentProfileDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) return [];
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot) || !Path.IsPathRooted(configRoot)) configRoot = Path.Combine(home, ".config");
        var openCodeConfig = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(openCodeConfig) || !Path.IsPathRooted(openCodeConfig)) openCodeConfig = Path.Combine(configRoot, "opencode");
        return [Path.Combine(openCodeConfig, "agents")];
    }
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

    /// <summary>Validates a stored display-name reference. Profile filenames remain restricted to short slugs.</summary>
    public static string? NormalizeReferenceName(string? name) =>
        name is { Length: > 0 and <= 80 } &&
        string.Equals(name, name.Trim(), StringComparison.Ordinal) &&
        name is not "." and not ".." &&
        !name.Any(char.IsControl) && !name.Contains('/') && !name.Contains('\\')
            ? name : null;

    public static string? NormalizeProfileFileStem(string? name) =>
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
        bool includeProjectProfiles, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? additionalUserProfileDirectories = null)
    {
        var warnings = new List<string>();
        var user = await LoadScopeAsync(userProfilesDirectory, "user", warnings, cancellationToken).ConfigureAwait(false);
        foreach (var directory in additionalUserProfileDirectories ?? [])
        {
            if (user.Count >= MaxProfilesPerScope) break;
            if (!HasNoLinkedPathSegments(directory))
            {
                warnings.Add("An OpenCode user agent directory was skipped because it is a symbolic link or has a linked ancestor.");
                continue;
            }
            user.AddRange(await LoadOpenCodeScopeAsync(directory, "user-opencode", warnings, cancellationToken,
                MaxProfilesPerScope - user.Count).ConfigureAwait(false));
        }
        var project = new List<AgentProfile>();
        if (includeProjectProfiles && !string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot) &&
            HasNoLinkedPathSegments(projectRoot))
        {
            foreach (var relativePath in ProjectAgentPaths)
            {
                if (project.Count >= MaxProfilesPerScope) break;
                var path = Path.Combine(Path.GetFullPath(projectRoot), relativePath);
                if (!IsSafeProjectDirectory(projectRoot, path, relativePath))
                {
                    warnings.Add($"Project agent profiles in '{relativePath.Replace('\\', '/')}' were skipped because the folder is a symbolic link or resolves outside the project.");
                    continue;
                }
                var remaining = MaxProfilesPerScope - project.Count;
                var scope = relativePath.StartsWith(Path.Combine(".opencode", "agents"), StringComparison.Ordinal)
                    ? "project-opencode" : "project";
                var loaded = scope == "project"
                    ? await LoadScopeAsync(path, scope, warnings, cancellationToken, remaining, projectRoot, relativePath).ConfigureAwait(false)
                    : await LoadOpenCodeScopeAsync(path, scope, warnings, cancellationToken, remaining, projectRoot, relativePath).ConfigureAwait(false);
                project.AddRange(loaded);
            }
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
            var key = Unquote(trimmed[..colon].Trim());
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

        var normalizedDisplayName = NormalizeReferenceName(displayName);
        if (normalizedDisplayName is null)
            return Fail("Add a display name of 1–80 printable characters without path separators.", out error);
        if (string.IsNullOrWhiteSpace(description) || description.Length > 180)
            return Fail("Add a short description of at most 180 characters.", out error);
        if (model is { Length: > 160 } || model?.Any(char.IsControl) == true) return Fail("The model value is too long or contains control characters.", out error);
        var instructions = string.Join('\n', lines.Skip(end + 1)).Trim();
        if (instructions.Length > MaxInstructionsCharacters) return Fail("Profile instructions exceed the size limit.", out error);
        if (instructions.Length == 0) return Fail("Add the agent instructions after the frontmatter.", out error);

        profile = new AgentProfile(normalizedDisplayName, description, string.IsNullOrWhiteSpace(model) ? null : model,
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

    internal static bool OpenCodeShellRuleMatches(string pattern, string command, AgentToolPermission permission)
    {
        var clauses = CommandClauseSeparators.Split(command).Where(clause => !string.IsNullOrWhiteSpace(clause)).ToArray();
        if (clauses.Length == 0) return false;
        // A narrow allow/ask rule must cover every part of a compound command; one matching
        // clause cannot weaken a broader deny. A deny is conservative when any clause matches.
        return permission == AgentToolPermission.Deny
            ? clauses.Any(clause => CommandPatternMatches(pattern, clause))
            : clauses.All(clause => CommandPatternMatches(pattern, clause));
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
        CancellationToken cancellationToken, int maxProfiles = MaxProfilesPerScope,
        string? trustedProjectRoot = null, string? relativeDirectory = null)
    {
        var result = new List<AgentProfile>();
        try
        {
            if (!Directory.Exists(directory)) return result;
            if (IsReparsePoint(directory)) { warnings.Add($"The {scope} agent profile directory is a symbolic link and was skipped."); return result; }
            var files = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(maxProfiles + 1).ToArray();
            if (files.Length > maxProfiles) warnings.Add($"Only the first {maxProfiles} {scope} agent profiles are loaded.");
            foreach (var path in files.Take(maxProfiles))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (trustedProjectRoot is null && IsReparsePoint(path)) { warnings.Add($"Agent profile '{Path.GetFileName(path)}' is a symbolic link and was skipped."); continue; }
                    var text = await ReadProfileTextAsync(path, cancellationToken, trustedProjectRoot,
                        trustedProjectRoot is null ? null : Path.Combine(relativeDirectory!, Path.GetFileName(path))).ConfigureAwait(false);
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

    private static async Task<List<AgentProfile>> LoadOpenCodeScopeAsync(string directory, string scope, List<string> warnings,
        CancellationToken cancellationToken, int maxProfiles, string? trustedProjectRoot = null, string? relativeDirectory = null)
    {
        var result = new List<AgentProfile>();
        try
        {
            if (!Directory.Exists(directory)) return result;
            if (IsReparsePoint(directory))
            {
                warnings.Add($"The {scope} agent profile directory is a symbolic link and was skipped.");
                return result;
            }
            var files = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(maxProfiles + 1).ToArray();
            if (files.Length > maxProfiles) warnings.Add($"Only the first {maxProfiles} {scope} agent profiles are loaded.");
            foreach (var path in files.Take(maxProfiles))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(path);
                try
                {
                    if (trustedProjectRoot is null && IsReparsePoint(path)) { warnings.Add($"OpenCode agent '{fileName}' is a symbolic link and was skipped."); continue; }
                    var text = await ReadProfileTextAsync(path, cancellationToken, trustedProjectRoot,
                        trustedProjectRoot is null ? null : Path.Combine(relativeDirectory!, fileName)).ConfigureAwait(false);
                    if (TryParseOpenCodeAgent(fileName, text, scope, out var profile, out var modelWarning, out var error) && profile is not null)
                    {
                        result.Add(profile with { FilePath = path });
                        if (modelWarning is not null) warnings.Add($"OpenCode agent '{fileName}': {modelWarning}");
                    }
                    else warnings.Add($"OpenCode agent '{fileName}' was skipped: {error}");
                }
                catch (DecoderFallbackException) { warnings.Add($"OpenCode agent '{fileName}' is not valid UTF-8 and was skipped."); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { warnings.Add($"OpenCode agent '{fileName}' could not be read."); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { warnings.Add($"The {scope} agent profile directory could not be read."); }
        return result;
    }

    private static async Task<string> ReadProfileTextAsync(string path, CancellationToken cancellationToken,
        string? trustedProjectRoot, string? relativePath)
    {
        await using var stream = trustedProjectRoot is not null && relativePath is not null
            ? FileHardLinkInspector.OpenSingleLinkReadStream(path, relativePath, trustedProjectRoot)
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxProfileFileBytes)
            throw new IOException($"Agent profile '{Path.GetFileName(path)}' exceeds the size limit.");
        var bytes = new byte[MaxProfileFileBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        if (length > MaxProfileFileBytes)
            throw new IOException($"Agent profile '{Path.GetFileName(path)}' exceeds the size limit.");
        return StrictUtf8.GetString(bytes, 0, length);
    }

    private static bool TryParseOpenCodeAgent(string fileName, string contents, string scope,
        out AgentProfile? profile, out string? modelWarning, out string error)
    {
        profile = null;
        modelWarning = null;
        error = "";
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !SafeName.IsMatch(name))
            return Fail("use a Markdown filename with a simple agent name", out error);
        if (contents.Length > MaxProfileFileBytes) return Fail("agent profile files are limited to 32 KB", out error);
        var lines = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length < 4 || lines[0].Trim() != "---") return Fail("start the profile with frontmatter between --- lines", out error);
        var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (end < 0) return Fail("agent profile frontmatter is missing its closing --- line", out error);

        string? description = null;
        string? model = null;
        string mode = "all";
        var modeWasSpecified = false;
        var sawV2Field = false;
        double? temperature = null;
        int? maxSteps = null;
        string? section = null;
        var commandPatternSection = false;
        string? v2Action = null;
        string? v2Resource = null;
        AgentToolPermission? v2Effect = null;
        var toolPermissions = new Dictionary<string, AgentToolPermission>(StringComparer.OrdinalIgnoreCase);
        var commandPermissions = new Dictionary<string, AgentToolPermission>(StringComparer.Ordinal);
        var v2PermissionRules = new List<OpenCodeAgentPermissionRule>();
        var sawV2Permissions = false;
        var sawLegacyPermissions = false;
        var disabled = false;
        var hidden = false;
        foreach (var line in lines.Skip(1).Take(end - 1))
        {
            var indent = line.TakeWhile(char.IsWhiteSpace).Count();
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            if (indent is not (0 or 2 or 4) || (indent > 0 && section is null))
                return Fail("OpenCode agent mappings must use two-space indentation without implicit blocks", out error);

            if (section == "permissions" && indent == 2 && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                if (!TryAppendOpenCodeV2PermissionRule(v2PermissionRules, ref v2Action, ref v2Resource, ref v2Effect, out error)) return false;
                var entry = trimmed[2..];
                var entryColon = entry.IndexOf(':');
                if (entryColon <= 0 || !Unquote(entry[..entryColon].Trim()).Equals("action", StringComparison.OrdinalIgnoreCase))
                    return Fail("OpenCode V2 permission entries must begin with an action field", out error);
                v2Action = Unquote(entry[(entryColon + 1)..].Trim());
                if (v2Action.Length == 0) return Fail("OpenCode V2 permission actions cannot be empty", out error);
                v2Resource = null;
                v2Effect = null;
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0) return Fail("each supported agent field must use key: value syntax", out error);
            var key = Unquote(trimmed[..colon].Trim());
            var value = Unquote(trimmed[(colon + 1)..].Trim());

            if (section == "permissions" && indent == 4)
            {
                if (v2Action is null) return Fail("OpenCode V2 permission fields must follow a list item", out error);
                if (key.Equals("resource", StringComparison.OrdinalIgnoreCase))
                {
                    if (v2Resource is not null) return Fail("OpenCode V2 permission entries may contain only one resource", out error);
                    v2Resource = value;
                }
                else if (key.Equals("effect", StringComparison.OrdinalIgnoreCase))
                {
                    if (v2Effect is not null) return Fail("OpenCode V2 permission entries may contain only one effect", out error);
                    if (!TryParsePermission(value, out var effect)) return Fail("OpenCode V2 permission effects must be allow, ask, or deny", out error);
                    v2Effect = effect;
                }
                else return Fail($"unsupported OpenCode V2 permission field '{key}'", out error);
                continue;
            }

            if (indent == 0)
            {
                commandPatternSection = false;
                if (key.Equals("permission", StringComparison.OrdinalIgnoreCase) || key.Equals("tools", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("permissions", StringComparison.OrdinalIgnoreCase))
                {
                    section = key.ToLowerInvariant();
                    if (section == "permissions")
                    {
                        if (sawV2Permissions) return Fail("OpenCode V2 agents may define permissions only once", out error);
                        sawV2Permissions = true;
                        sawV2Field = true;
                    }
                    else sawLegacyPermissions = true;
                    if (value.Length != 0) return Fail($"the OpenCode '{key}' field must use a YAML mapping", out error);
                    continue;
                }
                section = null;
                if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
                else if (key.Equals("mode", StringComparison.OrdinalIgnoreCase))
                {
                    if (value is not ("primary" or "subagent" or "all")) return Fail("mode must be primary, subagent, or all", out error);
                    mode = value;
                    modeWasSpecified = true;
                }
                else if (key.Equals("model", StringComparison.OrdinalIgnoreCase)) model = value;
                else if (key.Equals("steps", StringComparison.OrdinalIgnoreCase) || key.Equals("maxSteps", StringComparison.OrdinalIgnoreCase) || key.Equals("max_steps", StringComparison.OrdinalIgnoreCase))
                {
                    if (key.Equals("steps", StringComparison.OrdinalIgnoreCase)) sawV2Field = true;
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > CodeTaskLimits.MaxModelStepsPerTurn)
                        return Fail($"{key} must be from 1 through {CodeTaskLimits.MaxModelStepsPerTurn}", out error);
                    maxSteps = parsed;
                }
                else if (key.Equals("temperature", StringComparison.OrdinalIgnoreCase))
                {
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed) || parsed is < 0 or > 2)
                        return Fail("temperature must be a number from 0 through 2", out error);
                    temperature = parsed;
                }
                else if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    if (!value.Equals(name, StringComparison.OrdinalIgnoreCase)) return Fail("name must match the Markdown filename", out error);
                }
                else if (key.Equals("hidden", StringComparison.OrdinalIgnoreCase))
                {
                    sawV2Field = true;
                    if (!bool.TryParse(value, out hidden)) return Fail("hidden must be true or false", out error);
                }
                else if (key.Equals("disabled", StringComparison.OrdinalIgnoreCase))
                {
                    sawV2Field = true;
                    if (!bool.TryParse(value, out disabled)) return Fail("disabled must be true or false", out error);
                }
                else if (key.Equals("color", StringComparison.OrdinalIgnoreCase)) { sawV2Field = true; /* Display-only in OpenCode. */ }
                else if (key.Equals("permission", StringComparison.OrdinalIgnoreCase) || key.Equals("tools", StringComparison.OrdinalIgnoreCase))
                    return Fail($"the OpenCode '{key}' field must use an indented YAML mapping", out error);
                else return Fail($"unsupported OpenCode agent field '{key}'", out error);
                continue;
            }

            if (section == "permissions") return Fail("OpenCode V2 permission entries must use action/resource/effect list items", out error);

            if (section == "permission" && indent == 2)
            {
                if (key.Equals("bash", StringComparison.OrdinalIgnoreCase) && value.Length == 0)
                {
                    commandPatternSection = true;
                    continue;
                }
                commandPatternSection = false;
                if (!TryParsePermission(value, out var permission)) return Fail($"permission.{key} must be allow, ask, or deny", out error);
                if (!IsSupportedOpenCodePermission(key)) return Fail($"permission.{key} does not map to a supported Codev tool", out error);
                if (!ApplyOpenCodePermission(toolPermissions, key, permission, out var permissionError)) return Fail(permissionError, out error);
                continue;
            }
            if (section == "permission" && indent >= 4 && commandPatternSection)
            {
                if (!TryParsePermission(value, out var permission) || !IsValidCommandPattern(key))
                    return Fail("permission.bash patterns must map safe command patterns to allow, ask, or deny", out error);
                MergePermission(commandPermissions, key, permission);
                continue;
            }
            if (section == "tools" && indent == 2)
            {
                if (!bool.TryParse(value, out var enabled)) return Fail($"tools.{key} must be true or false", out error);
                if (!ApplyOpenCodePermission(toolPermissions, key, enabled ? AgentToolPermission.Allow : AgentToolPermission.Deny, out var toolError))
                    return Fail(toolError, out error);
                continue;
            }
            return Fail("nested OpenCode agent fields are not supported except permission.bash command patterns", out error);
        }

        if (sawV2Permissions)
        {
            if (sawLegacyPermissions) return Fail("OpenCode V1 permission mappings cannot be mixed with V2 permissions", out error);
            if (!TryAppendOpenCodeV2PermissionRule(v2PermissionRules, ref v2Action, ref v2Resource, ref v2Effect, out error)) return false;
            if (v2PermissionRules.Count == 0) return Fail("OpenCode V2 permissions must contain at least one rule", out error);
        }

        if (hidden) return Fail("hidden OpenCode agents are not imported into Codev's visible agent picker", out error);
        if (disabled) return Fail("disabled OpenCode agents are not imported into Codev's agent picker", out error);

        if (string.IsNullOrWhiteSpace(description) || description.Length > 180)
            return Fail("description must contain 1–180 characters", out error);
        var instructions = string.Join('\n', lines.Skip(end + 1)).Trim();
        if (instructions.Length == 0 || instructions.Length > MaxInstructionsCharacters)
            return Fail($"agent instructions must contain 1–{MaxInstructionsCharacters} characters", out error);
        var normalized = new StringBuilder()
            .AppendLine("---")
            .Append("name: ").AppendLine(name)
            .Append("description: ").AppendLine(description)
            .AppendLine("default_permission: ask");
        if (temperature is not null) normalized.Append("temperature: ").AppendLine(temperature.Value.ToString(CultureInfo.InvariantCulture));
        if (maxSteps is not null) normalized.Append("max_steps: ").AppendLine(maxSteps.Value.ToString(CultureInfo.InvariantCulture));
        if (toolPermissions.Count > 0)
            normalized.Append("tools: ").AppendLine(string.Join(", ", toolPermissions.Select(rule => $"{rule.Key}={rule.Value.ToString().ToLowerInvariant()}")));
        // Keep command patterns in the profile model without serializing through its comma-list
        // syntax: OpenCode patterns can themselves contain commas and whitespace.
        normalized.AppendLine("---").AppendLine(instructions);
        if (!TryParse(fileName, normalized.ToString(), scope, out profile, out error) || profile is null) return false;
        profile = profile with
        {
            Mode = mode,
            Model = null,
            ToolPermissions = toolPermissions.Where(rule => !rule.Key.Equals("run_command", StringComparison.OrdinalIgnoreCase) &&
                !rule.Key.Equals("verify_command", StringComparison.OrdinalIgnoreCase) &&
                !rule.Key.Equals("start_background_command", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(rule => rule.Key, rule => rule.Value, StringComparer.OrdinalIgnoreCase),
            CommandPermissions = commandPermissions.Count == 0 ? null : commandPermissions,
            OpenCodePermissionRules = v2PermissionRules.Count == 0 ? null : v2PermissionRules
        };
        if ((sawV2Permissions || sawV2Field) && !modeWasSpecified) profile = profile with { Mode = "primary" };
        modelWarning = string.IsNullOrWhiteSpace(model) ? null : "the OpenCode model preference was not applied; choose the provider/model in Codev.";
        return true;
    }

    private static bool ApplyOpenCodePermission(Dictionary<string, AgentToolPermission> target, string permissionName,
        AgentToolPermission permission, out string error)
    {
        error = "";
        var normalized = permissionName.ToLowerInvariant();
        var tools = normalized switch
        {
            "edit" or "write" => new[] { "create_file", "write_file", "apply_patch" },
            "bash" => new[] { "run_command", "verify_command", "start_background_command" },
            "read" => new[] { "list_files", "read_file", "search_files" },
            "list" or "glob" => new[] { "list_files" },
            "grep" => new[] { "search_files" },
            "task" => new[] { "delegate_task" },
            "skill" => new[] { "load_skill_*" },
            "webfetch" or "websearch" or "external_directory" or "lsp" or "question" or "doom_loop" => [],
            _ when SafeTool.IsMatch(permissionName) => [permissionName],
            _ => []
        };
        if (tools.Length == 0 && normalized is not ("webfetch" or "websearch" or "external_directory" or "lsp" or "question" or "doom_loop"))
        {
            error = $"permission.{permissionName} does not map to a supported Codev tool";
            return false;
        }
        foreach (var tool in tools) MergePermission(target, tool, permission);
        return true;
    }

    internal static IReadOnlyList<string> OpenCodeToolsForPermission(string permissionName) => permissionName.ToLowerInvariant() switch
    {
        "*" => ["*"],
        "edit" or "write" => ["create_file", "write_file", "apply_patch"],
        "bash" or "shell" => ["run_command", "verify_command", "start_background_command"],
        "read" => ["list_files", "read_file", "search_files"],
        "list" or "glob" => ["list_files"],
        "grep" => ["search_files"],
        "task" or "subagent" => ["delegate_task"],
        "skill" => ["load_skill_*"],
        "webfetch" or "websearch" or "external_directory" or "lsp" or "question" or "doom_loop" => [],
        _ when SafeTool.IsMatch(permissionName) => [permissionName],
        _ => []
    };

    internal static bool OpenCodeToolPatternMatches(string pattern, string toolName)
    {
        if (pattern == "*") return true;
        if (pattern == "mcp:*") return toolName.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase);
        if (pattern.Contains('*') || pattern.Contains('?'))
            return GlobMatches(pattern, toolName) ||
                (toolName.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase) && GlobMatches(pattern, toolName[4..]));
        return pattern.Equals(toolName, StringComparison.OrdinalIgnoreCase) ||
            (toolName.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase) && pattern.Equals(toolName[4..], StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryAppendOpenCodeV2PermissionRule(List<OpenCodeAgentPermissionRule> rules,
        ref string? action, ref string? resource, ref AgentToolPermission? effect, out string error)
    {
        error = "";
        if (action is null && resource is null && effect is null) return true;
        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(resource) || effect is null)
            return Fail("each OpenCode V2 permission entry requires action, resource, and effect", out error);
        if (rules.Count >= MaxToolPolicies) return Fail($"OpenCode V2 agents may define at most {MaxToolPolicies} permission rules", out error);

        var normalizedAction = action.ToLowerInvariant();
        var mappedTools = OpenCodeToolsForPermission(normalizedAction);
        if (mappedTools.Count == 0 && normalizedAction is not ("webfetch" or "websearch" or "lsp" or "question" or "doom_loop"))
            return Fail($"OpenCode V2 permission action '{action}' does not map to a supported Codev tool", out error);

        if (normalizedAction == "shell")
        {
            if (!IsValidCommandPattern(resource) || CommandClauseSeparators.IsMatch(resource))
                return Fail("OpenCode V2 shell resources must be single safe command patterns", out error);
        }
        else if (normalizedAction == "edit" && resource != "*")
        {
            if (!IsValidPathPattern(resource)) return Fail("OpenCode V2 edit resources must be safe project-relative path patterns", out error);
            if (effect == AgentToolPermission.Ask)
                return Fail("path-scoped OpenCode V2 edit ask rules are not supported; use allow or deny", out error);
        }
        else if (resource != "*")
        {
            return Fail($"resource-scoped OpenCode V2 '{action}' rules are not supported by Codev and were not imported", out error);
        }

        rules.Add(new OpenCodeAgentPermissionRule(normalizedAction, resource, effect.Value));
        action = null;
        resource = null;
        effect = null;
        return true;
    }

    private static bool IsSupportedOpenCodePermission(string name) => name.ToLowerInvariant() is
        "edit" or "write" or "bash" or "read" or "list" or "glob" or "grep" or "task" or "skill" or
        "webfetch" or "websearch" or "external_directory" or "lsp" or "question" or "doom_loop";

    private static void MergePermission(Dictionary<string, AgentToolPermission> rules, string key, AgentToolPermission permission)
    {
        if (!rules.TryGetValue(key, out var current) || PermissionRank(permission) > PermissionRank(current)) rules[key] = permission;
    }

    private static int PermissionRank(AgentToolPermission permission) => permission switch
    {
        AgentToolPermission.Deny => 3,
        AgentToolPermission.Ask => 2,
        _ => 1
    };

    private static string Unquote(string value) => value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"'
        ? value[1..^1] : value;

    private static bool IsSafeProjectDirectory(string projectRoot, string profileDirectory, string relativePath)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(profileDirectory).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        var current = Path.GetFullPath(projectRoot);
        foreach (var segment in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
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

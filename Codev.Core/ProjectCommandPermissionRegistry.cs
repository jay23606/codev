using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codev;

public enum ProjectCommandPermissionMode
{
    AskEveryTime,
    Auto,
    Allowlist,
    ReadOnly
}

public enum ProjectCommandPermissionDecision
{
    Ask,
    Allow,
    Deny
}

public enum ProjectCommandApprovalChoice
{
    Cancel,
    RunOnce,
    AllowExactCommand,
    DenyExactCommand
}

public enum CommandApprovalOutcome
{
    Approved,
    ApprovedReadOnly,
    Rejected,
    Denied
}

public sealed record ProjectCommandPermissionRule(string Command, ProjectCommandPermissionDecision Decision);
public sealed record ProjectCommandPermissions(string ProjectPath, ProjectCommandPermissionMode Mode,
    List<ProjectCommandPermissionRule> Rules);

/// <summary>Stores exact per-project shell command decisions outside project folders. Invalid data fails closed to prompting.</summary>
public sealed class ProjectCommandPermissionRegistry
{
    private const int MaxProjects = 500;
    private const int MaxRulesPerProject = 300;
    private const int MaxCommandLength = 4000;
    private const int MaxFileBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly Regex GitCommand = new(@"\bgit(?:\.exe)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex GitMetadataPath = new(@"(?:^|[/\\\s])\.git(?:$|[/\\\s])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, ProjectCommandPermissions> _projects = new(PathComparer);

    private ProjectCommandPermissionRegistry(string path, bool canPersist = true, string? loadError = null)
    {
        _path = Path.GetFullPath(path);
        CanPersist = canPersist;
        LoadError = loadError;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public bool CanPersist { get; }
    public string? LoadError { get; }

    public static ProjectCommandPermissionRegistry Load(string path)
    {
        var registry = new ProjectCommandPermissionRegistry(path);
        if (!File.Exists(path)) return registry;
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("The permission file exceeds the size limit.");
            var projects = JsonSerializer.Deserialize<List<ProjectCommandPermissions>>(File.ReadAllText(path), JsonOptions) ?? [];
            if (projects.Count > MaxProjects) throw new InvalidDataException("The permission file contains too many projects.");
            foreach (var project in projects)
            {
                if (project is null || string.IsNullOrWhiteSpace(project.ProjectPath) || project.Rules is null || project.Rules.Count > MaxRulesPerProject)
                    throw new InvalidDataException("The permission file contains an invalid project entry.");
                var normalizedPath = NormalizeProjectPath(project.ProjectPath);
                var mode = Enum.IsDefined(project.Mode) ? project.Mode : ProjectCommandPermissionMode.AskEveryTime;
                var rules = NormalizeRules(project.Rules);
                if (!registry._projects.TryAdd(normalizedPath, new ProjectCommandPermissions(normalizedPath, mode, rules)))
                    throw new InvalidDataException("The permission file contains duplicate project entries.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new ProjectCommandPermissionRegistry(path, canPersist: false,
                loadError: $"Saved command permissions could not be read ({ex.GetType().Name}); commands will require approval and the file was preserved.");
        }
        return registry;
    }

    public ProjectCommandPermissionMode GetMode(string projectPath) => GetProject(projectPath)?.Mode ?? ProjectCommandPermissionMode.AskEveryTime;

    public bool HasProjectSettings(string projectPath) => GetProject(projectPath) is not null;

    public IReadOnlyList<ProjectCommandPermissionRule> GetRules(string projectPath) => GetProject(projectPath)?.Rules.ToArray() ?? [];

    public ProjectCommandPermissionDecision Evaluate(string projectPath, string command, string shellName = "", bool allowReadOnly = true,
        IReadOnlyList<string>? contextExclusions = null, bool isVerification = false,
        ProjectCommandPermissionMode? modeWhenUnconfigured = null)
    {
        var project = GetProject(projectPath);
        var mode = project?.Mode ?? modeWhenUnconfigured ?? ProjectCommandPermissionMode.AskEveryTime;
        var rules = project?.Rules ?? [];
        var normalizedCommand = NormalizeCommand(command);
        if (rules.Any(rule => rule.Decision == ProjectCommandPermissionDecision.Deny &&
                                      string.Equals(rule.Command, normalizedCommand, StringComparison.Ordinal)))
            return ProjectCommandPermissionDecision.Deny;
        // Auto is an explicit trust decision: resolve every outstanding command prompt as allow,
        // while preserving per-project exact deny rules. This mirrors OpenCode's --auto behavior.
        if (mode == ProjectCommandPermissionMode.Auto) return ProjectCommandPermissionDecision.Allow;
        if ((mode is ProjectCommandPermissionMode.Auto or ProjectCommandPermissionMode.Allowlist) && CanCreateAllowRule(normalizedCommand) &&
            rules.Any(rule => rule.Decision == ProjectCommandPermissionDecision.Allow &&
                                      string.Equals(rule.Command, normalizedCommand, StringComparison.Ordinal)))
            return ProjectCommandPermissionDecision.Allow;
        if (allowReadOnly && mode is (ProjectCommandPermissionMode.ReadOnly or ProjectCommandPermissionMode.Auto))
        {
            string classificationRoot;
            try { classificationRoot = project?.ProjectPath ?? NormalizeProjectPath(projectPath); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                return ProjectCommandPermissionDecision.Ask;
            }
            if (ReadOnlyCommandClassifier.IsReadOnly(normalizedCommand, classificationRoot, shellName, contextExclusions))
                return ProjectCommandPermissionDecision.Allow;
        }
        return ProjectCommandPermissionDecision.Ask;
    }

    /// <summary>True when an approved inspection should use Codev's bounded file APIs instead of launching a shell.</summary>
    public bool ShouldUseBoundedFileInspection(string projectPath, string command,
        ProjectCommandPermissionDecision decision, bool isVerification = false, string shellName = "",
        IReadOnlyList<string>? contextExclusions = null)
    {
        if (decision != ProjectCommandPermissionDecision.Allow || isVerification) return false;
        return GetMode(projectPath) switch
        {
            ProjectCommandPermissionMode.ReadOnly => true,
            ProjectCommandPermissionMode.Auto => ReadOnlyCommandClassifier.IsReadOnly(command, projectPath, shellName, contextExclusions),
            _ => false
        };
    }

    public static bool CanCreateAllowRule(string command) => !TargetsProtectedLocation(command);

    public Task SetModeAsync(string projectPath, ProjectCommandPermissionMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return UpdateProjectAsync(projectPath, project => project with { Mode = mode }, cancellationToken);
    }

    /// <summary>Copies a project's mode and eligible exact rules to a new isolated project in one durable update.</summary>
    public async Task CopyProjectSettingsAsync(string sourceProjectPath, string destinationProjectPath,
        CancellationToken cancellationToken = default)
    {
        if (!CanPersist) throw new InvalidOperationException(LoadError ?? "Command permissions are read-only.");
        var sourcePath = NormalizeProjectPath(sourceProjectPath);
        var destinationPath = NormalizeProjectPath(destinationProjectPath);
        if (PathComparer.Equals(sourcePath, destinationPath)) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = _projects.TryGetValue(sourcePath, out var currentSource)
                ? currentSource
                : new ProjectCommandPermissions(sourcePath, ProjectCommandPermissionMode.AskEveryTime, []);
            var rules = source.Rules.Where(rule => rule.Decision == ProjectCommandPermissionDecision.Deny ||
                CanCreateAllowRule(rule.Command)).ToList();
            if (rules.Count > MaxRulesPerProject)
                throw new InvalidOperationException("Copied command permissions exceed the per-project rule limit.");
            var next = new ProjectCommandPermissions(destinationPath, source.Mode, rules);
            var candidate = new Dictionary<string, ProjectCommandPermissions>(_projects, PathComparer)
            {
                [destinationPath] = next
            };
            if (candidate.Count > MaxProjects) throw new InvalidOperationException("Too many projects have saved command permissions.");
            var json = JsonSerializer.Serialize(candidate.Values.OrderBy(item => item.ProjectPath, PathComparer), JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
                throw new InvalidOperationException("Saved command permissions would exceed the size limit.");
            await AtomicTextFile.WriteAsync(_path, json, cancellationToken).ConfigureAwait(false);
            _projects[destinationPath] = next;
        }
        finally { _gate.Release(); }
    }

    public Task SetRuleAsync(string projectPath, string command, ProjectCommandPermissionDecision decision,
        ProjectCommandPermissionMode? mode = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCommand = NormalizeCommand(command);
        if (normalizedCommand.Length is 0 or > MaxCommandLength) throw new ArgumentException("Command rule must contain 1–4,000 characters.", nameof(command));
        if (mode is { } selectedMode && !Enum.IsDefined(selectedMode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (decision is not (ProjectCommandPermissionDecision.Allow or ProjectCommandPermissionDecision.Deny))
            throw new ArgumentOutOfRangeException(nameof(decision), "A saved command rule must allow or deny.");
        if (decision == ProjectCommandPermissionDecision.Allow && !CanCreateAllowRule(normalizedCommand))
            throw new InvalidOperationException("Commands that invoke Git or reference .git metadata or Codev app data cannot be saved as persistent allow rules in Ask or Allowlist modes.");
        return UpdateProjectAsync(projectPath, project =>
        {
            var rules = project.Rules.Where(rule => !string.Equals(rule.Command, normalizedCommand, StringComparison.Ordinal) || rule.Decision != decision).ToList();
            if (rules.Count >= MaxRulesPerProject) throw new InvalidOperationException("This project already has the maximum number of saved command rules.");
            rules.Add(new ProjectCommandPermissionRule(normalizedCommand, decision));
            return project with { Mode = mode ?? project.Mode, Rules = rules };
        }, cancellationToken);
    }

    public Task RemoveRuleAsync(string projectPath, string command, ProjectCommandPermissionDecision decision,
        CancellationToken cancellationToken = default)
    {
        if (decision is not (ProjectCommandPermissionDecision.Allow or ProjectCommandPermissionDecision.Deny))
            throw new ArgumentOutOfRangeException(nameof(decision), "A saved command rule must allow or deny.");
        var normalizedCommand = NormalizeCommand(command);
        return UpdateProjectAsync(projectPath, project => project with
        {
            Rules = project.Rules.Where(rule => !string.Equals(rule.Command, normalizedCommand, StringComparison.Ordinal) || rule.Decision != decision).ToList()
        }, cancellationToken);
    }

    private ProjectCommandPermissions? GetProject(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        try { return _projects.GetValueOrDefault(NormalizeProjectPath(projectPath)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    private async Task UpdateProjectAsync(string projectPath, Func<ProjectCommandPermissions, ProjectCommandPermissions> update,
        CancellationToken cancellationToken)
    {
        if (!CanPersist) throw new InvalidOperationException(LoadError ?? "Command permissions are read-only.");
        var normalizedPath = NormalizeProjectPath(projectPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _projects.TryGetValue(normalizedPath, out var current) ? current : null;
            current ??= new ProjectCommandPermissions(normalizedPath, ProjectCommandPermissionMode.AskEveryTime, []);
            var next = update(current);
            var candidate = new Dictionary<string, ProjectCommandPermissions>(_projects, PathComparer) { [normalizedPath] = next };
            if (candidate.Count > MaxProjects) throw new InvalidOperationException("Too many projects have saved command permissions.");
            var json = JsonSerializer.Serialize(candidate.Values.OrderBy(item => item.ProjectPath, PathComparer), JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new InvalidOperationException("Saved command permissions would exceed the size limit.");
            await AtomicTextFile.WriteAsync(_path, json, cancellationToken).ConfigureAwait(false);
            _projects[normalizedPath] = next;
        }
        finally { _gate.Release(); }
    }

    private static List<ProjectCommandPermissionRule> NormalizeRules(IEnumerable<ProjectCommandPermissionRule> rules)
    {
        var normalized = new List<ProjectCommandPermissionRule>();
        foreach (var rule in rules)
        {
            if (rule is null || rule.Decision is not (ProjectCommandPermissionDecision.Allow or ProjectCommandPermissionDecision.Deny))
                throw new InvalidDataException("The permission file contains an invalid command rule.");
            var command = NormalizeCommand(rule.Command);
            if (command.Length is 0 or > MaxCommandLength)
                throw new InvalidDataException("The permission file contains an invalid command rule.");
            normalized.RemoveAll(item => string.Equals(item.Command, command, StringComparison.Ordinal) && item.Decision == rule.Decision);
            if (normalized.Count < MaxRulesPerProject) normalized.Add(rule with { Command = command });
        }
        return normalized;
    }

    private static string NormalizeProjectPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? "";
        return fullPath.Length > root.Length ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : fullPath;
    }

    private static string NormalizeCommand(string? command) => (command ?? "").Trim();

    private static bool TargetsProtectedLocation(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return true;
        if (GitCommand.IsMatch(command) || GitMetadataPath.IsMatch(command)) return true;
        var normalizedCommand = command.Replace('\\', '/');
        var appData = CodevDataPaths.LocalDataRoot;
        if (!string.IsNullOrWhiteSpace(appData))
        {
            var protectedPath = Path.Combine(appData, "Codev").Replace('\\', '/').TrimEnd('/');
            if (normalizedCommand.Contains(protectedPath, StringComparison.OrdinalIgnoreCase)) return true;
        }
        var lower = normalizedCommand.ToLowerInvariant();
        return (lower.Contains("appdata", StringComparison.Ordinal) || lower.Contains(".local/share", StringComparison.Ordinal)) &&
               lower.Contains("codev", StringComparison.Ordinal);
    }
}

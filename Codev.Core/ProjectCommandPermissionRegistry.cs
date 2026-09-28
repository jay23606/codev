using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace Codev;

public enum ProjectCommandPermissionMode
{
    AskEveryTime,
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
    private readonly Dictionary<string, ProjectCommandPermissions> _projects = new(PathComparer);

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
                registry._projects[normalizedPath] = new ProjectCommandPermissions(normalizedPath, mode, rules);
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

    public IReadOnlyList<ProjectCommandPermissionRule> GetRules(string projectPath) => GetProject(projectPath)?.Rules.ToArray() ?? [];

    public ProjectCommandPermissionDecision Evaluate(string projectPath, string command, string shellName = "", bool allowReadOnly = true,
        IReadOnlyList<string>? contextExclusions = null)
    {
        var project = GetProject(projectPath);
        if (project is null) return ProjectCommandPermissionDecision.Ask;
        var normalizedCommand = NormalizeCommand(command);
        if (project.Rules.Any(rule => rule.Decision == ProjectCommandPermissionDecision.Deny &&
                                      string.Equals(rule.Command, normalizedCommand, StringComparison.Ordinal)))
            return ProjectCommandPermissionDecision.Deny;
        if (project.Mode == ProjectCommandPermissionMode.Allowlist && CanCreateAllowRule(normalizedCommand) &&
            project.Rules.Any(rule => rule.Decision == ProjectCommandPermissionDecision.Allow &&
                                      string.Equals(rule.Command, normalizedCommand, StringComparison.Ordinal)))
            return ProjectCommandPermissionDecision.Allow;
        if (allowReadOnly && project.Mode == ProjectCommandPermissionMode.ReadOnly &&
            ReadOnlyCommandClassifier.IsReadOnly(normalizedCommand, project.ProjectPath, shellName, contextExclusions))
            return ProjectCommandPermissionDecision.Allow;
        return ProjectCommandPermissionDecision.Ask;
    }

    public static bool CanCreateAllowRule(string command) => !TargetsProtectedLocation(command);

    public Task SetModeAsync(string projectPath, ProjectCommandPermissionMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return UpdateProjectAsync(projectPath, project => project with { Mode = mode }, cancellationToken);
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
            throw new InvalidOperationException("Commands that invoke Git or reference .git metadata or Codev app data must always ask for approval.");
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
        var previous = _projects.TryGetValue(normalizedPath, out var current) ? current : null;
        try
        {
            current ??= new ProjectCommandPermissions(normalizedPath, ProjectCommandPermissionMode.AskEveryTime, []);
            var next = update(current);
            _projects[normalizedPath] = next;
            if (_projects.Count > MaxProjects) throw new InvalidOperationException("Too many projects have saved command permissions.");
            var json = JsonSerializer.Serialize(_projects.Values.OrderBy(item => item.ProjectPath, PathComparer), JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new InvalidOperationException("Saved command permissions would exceed the size limit.");
            await AtomicTextFile.WriteAsync(_path, json, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (previous is null) _projects.Remove(normalizedPath);
            else _projects[normalizedPath] = previous;
            throw;
        }
        finally { _gate.Release(); }
    }

    private static List<ProjectCommandPermissionRule> NormalizeRules(IEnumerable<ProjectCommandPermissionRule> rules)
    {
        var normalized = new List<ProjectCommandPermissionRule>();
        foreach (var rule in rules)
        {
            if (rule is null || rule.Decision is not (ProjectCommandPermissionDecision.Allow or ProjectCommandPermissionDecision.Deny)) continue;
            var command = NormalizeCommand(rule.Command);
            if (command.Length is 0 or > MaxCommandLength) continue;
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
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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

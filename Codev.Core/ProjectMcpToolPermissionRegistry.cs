using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codev;

public sealed record ProjectMcpToolPermissionRule(string ServerId, string ToolName, ProjectCommandPermissionDecision Decision,
    string? ConfigurationFingerprint = null);
public sealed record ProjectMcpToolPermissions(string ProjectPath, List<ProjectMcpToolPermissionRule> Rules);

/// <summary>Stores per-project, exact MCP server/tool allow and deny decisions outside project folders.</summary>
public sealed class ProjectMcpToolPermissionRegistry
{
    private const int MaxProjects = 500;
    private const int MaxRulesPerProject = 500;
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private static readonly Regex SafeId = new("^[A-Za-z0-9_-]{1,256}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, ProjectMcpToolPermissions> _projects = new(PathComparer);

    private ProjectMcpToolPermissionRegistry(string path, bool canPersist = true, string? loadError = null)
    {
        _path = Path.GetFullPath(path);
        CanPersist = canPersist;
        LoadError = loadError;
    }

    public bool CanPersist { get; }
    public string? LoadError { get; }
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static ProjectMcpToolPermissionRegistry Load(string path)
    {
        var registry = new ProjectMcpToolPermissionRegistry(path);
        if (!File.Exists(path)) return registry;
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("The MCP permission file exceeds the size limit.");
            var projects = JsonSerializer.Deserialize<List<ProjectMcpToolPermissions>>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("The MCP permission file is empty or invalid.");
            if (projects.Count > MaxProjects) throw new InvalidDataException("The MCP permission file contains too many projects.");
            foreach (var item in projects)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.ProjectPath) || item.Rules is null || item.Rules.Count > MaxRulesPerProject)
                    throw new InvalidDataException("The MCP permission file contains an invalid project entry.");
                var projectPath = NormalizePath(item.ProjectPath);
                if (registry._projects.ContainsKey(projectPath))
                    throw new InvalidDataException("The MCP permission file contains duplicate project entries.");
                var rules = new List<ProjectMcpToolPermissionRule>();
                foreach (var rule in item.Rules)
                {
                    if (rule is null || rule.Decision is not (ProjectCommandPermissionDecision.Allow or ProjectCommandPermissionDecision.Deny))
                        throw new InvalidDataException("The MCP permission file contains an invalid tool rule.");
                    try
                    {
                        var normalized = NormalizeRule(rule.ServerId, rule.ToolName, rule.Decision, rule.ConfigurationFingerprint);
                        rules.RemoveAll(existing => SameTool(existing, normalized) && existing.Decision == normalized.Decision);
                        rules.Add(normalized);
                    }
                    catch (ArgumentException ex)
                    {
                        throw new InvalidDataException("The MCP permission file contains an invalid tool rule.", ex);
                    }
                }
                registry._projects.TryAdd(projectPath, new ProjectMcpToolPermissions(projectPath, rules));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new ProjectMcpToolPermissionRegistry(path, canPersist: false,
                loadError: $"Saved MCP permissions could not be read ({ex.GetType().Name}); MCP calls will require approval and the file was preserved.");
        }
        return registry;
    }

    public IReadOnlyList<ProjectMcpToolPermissionRule> GetRules(string projectPath) => GetProject(projectPath)?.Rules.ToArray() ?? [];

    public ProjectCommandPermissionDecision Evaluate(string projectPath, ProjectCommandPermissionMode mode, string serverId, string toolName,
        string? configurationFingerprint = null)
    {
        if (!CanPersist) return ProjectCommandPermissionDecision.Ask;
        var permission = GetProject(projectPath);
        var lookup = NormalizeRule(serverId, toolName, ProjectCommandPermissionDecision.Allow, configurationFingerprint);
        var rules = permission?.Rules ?? [];
        if (rules.Any(rule => SameTool(rule, lookup) && rule.Decision == ProjectCommandPermissionDecision.Deny))
            return ProjectCommandPermissionDecision.Deny;
        if (mode == ProjectCommandPermissionMode.Auto) return ProjectCommandPermissionDecision.Allow;
        if (mode == ProjectCommandPermissionMode.Allowlist && lookup.ConfigurationFingerprint is not null &&
            rules.Any(rule => SameTool(rule, lookup) && rule.Decision == ProjectCommandPermissionDecision.Allow &&
                string.Equals(rule.ConfigurationFingerprint, lookup.ConfigurationFingerprint, StringComparison.Ordinal)))
            return ProjectCommandPermissionDecision.Allow;
        return ProjectCommandPermissionDecision.Ask;
    }

    public Task SetRuleAsync(string projectPath, string serverId, string toolName, ProjectCommandPermissionDecision decision,
        string? configurationFingerprint = null, CancellationToken cancellationToken = default)
    {
        if (decision is not (ProjectCommandPermissionDecision.Allow or ProjectCommandPermissionDecision.Deny))
            throw new ArgumentOutOfRangeException(nameof(decision), "A saved MCP tool rule must allow or deny.");
        var rule = NormalizeRule(serverId, toolName, decision, configurationFingerprint);
        if (decision == ProjectCommandPermissionDecision.Allow && rule.ConfigurationFingerprint is null)
            throw new ArgumentException("An MCP Allow rule must be bound to a server configuration fingerprint.", nameof(configurationFingerprint));
        return UpdateAsync(projectPath, project =>
        {
            var rules = project.Rules.Where(item => !SameTool(item, rule) || item.Decision != decision).ToList();
            if (rules.Count >= MaxRulesPerProject) throw new InvalidOperationException("This project already has the maximum number of saved MCP tool rules.");
            rules.Add(rule);
            return project with { Rules = rules };
        }, cancellationToken);
    }

    public Task RemoveRuleAsync(string projectPath, string serverId, string toolName, ProjectCommandPermissionDecision decision,
        CancellationToken cancellationToken = default)
    {
        if (decision is not (ProjectCommandPermissionDecision.Allow or ProjectCommandPermissionDecision.Deny))
            throw new ArgumentOutOfRangeException(nameof(decision), "A saved MCP tool rule must allow or deny.");
        var rule = NormalizeRule(serverId, toolName, decision);
        return UpdateAsync(projectPath, project => project with
        {
            Rules = project.Rules.Where(item => !SameTool(item, rule) || item.Decision != decision).ToList()
        }, cancellationToken);
    }

    /// <summary>Copies only explicit MCP Deny rules into an isolated child project.</summary>
    public async Task CopyDenyRulesAsync(string sourceProjectPath, string destinationProjectPath,
        CancellationToken cancellationToken = default)
    {
        if (!CanPersist) throw new InvalidOperationException(LoadError ?? "MCP tool permissions are read-only.");
        var sourcePath = NormalizePath(sourceProjectPath);
        var destinationPath = NormalizePath(destinationProjectPath);
        if (PathComparer.Equals(sourcePath, destinationPath)) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var deniedRules = _projects.TryGetValue(sourcePath, out var source)
                ? source.Rules.Where(rule => rule.Decision == ProjectCommandPermissionDecision.Deny).ToArray()
                : [];
            if (deniedRules.Length == 0) return;

            var destination = _projects.TryGetValue(destinationPath, out var currentDestination)
                ? currentDestination
                : new ProjectMcpToolPermissions(destinationPath, []);
            var rules = destination.Rules.ToList();
            foreach (var deny in deniedRules)
            {
                rules.RemoveAll(existing => SameTool(existing, deny));
                rules.Add(deny);
            }
            if (rules.Count > MaxRulesPerProject)
                throw new InvalidOperationException("Copied MCP deny rules exceed the per-project rule limit.");

            var next = new ProjectMcpToolPermissions(destinationPath, rules);
            var candidate = new Dictionary<string, ProjectMcpToolPermissions>(_projects, PathComparer)
            {
                [destinationPath] = next
            };
            if (candidate.Count > MaxProjects) throw new InvalidOperationException("Too many projects have MCP tool permissions.");
            var json = JsonSerializer.Serialize(candidate.Values.OrderBy(item => item.ProjectPath, PathComparer), JsonOptions);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
                throw new InvalidOperationException("Copied MCP permissions would exceed the size limit.");
            await AtomicTextFile.WriteAsync(_path, json, cancellationToken).ConfigureAwait(false);
            _projects[destinationPath] = next;
        }
        finally { _gate.Release(); }
    }

    private ProjectMcpToolPermissions? GetProject(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        try { return _projects.GetValueOrDefault(NormalizePath(projectPath)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    private async Task UpdateAsync(string projectPath, Func<ProjectMcpToolPermissions, ProjectMcpToolPermissions> update,
        CancellationToken cancellationToken)
    {
        if (!CanPersist) throw new InvalidOperationException(LoadError ?? "MCP tool permissions are read-only.");
        var path = NormalizePath(projectPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _projects.GetValueOrDefault(path);
            var current = previous ?? new ProjectMcpToolPermissions(path, []);
            var next = update(current);
            var candidate = new Dictionary<string, ProjectMcpToolPermissions>(_projects, PathComparer) { [path] = next };
            if (candidate.Count > MaxProjects) throw new InvalidOperationException("Too many projects have MCP tool permissions.");
            var json = JsonSerializer.Serialize(candidate.Values.OrderBy(item => item.ProjectPath, PathComparer), JsonOptions);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new InvalidOperationException("MCP tool permissions would exceed the size limit.");
            await AtomicTextFile.WriteAsync(_path, json, cancellationToken).ConfigureAwait(false);
            _projects[path] = next;
        }
        finally { _gate.Release(); }
    }

    private static ProjectMcpToolPermissionRule NormalizeRule(string serverId, string toolName, ProjectCommandPermissionDecision decision,
        string? configurationFingerprint = null)
    {
        var server = (serverId ?? string.Empty).Trim();
        var tool = (toolName ?? string.Empty).Trim();
        if (!SafeId.IsMatch(server)) throw new ArgumentException("MCP server IDs must contain 1–256 safe characters.", nameof(serverId));
        if (tool.Length is 0 or > 256 || tool.Any(char.IsControl)) throw new ArgumentException("MCP tool names must contain 1–256 printable characters.", nameof(toolName));
        var fingerprint = string.IsNullOrWhiteSpace(configurationFingerprint) ? null : configurationFingerprint.Trim().ToLowerInvariant();
        if (fingerprint is not null && !Regex.IsMatch(fingerprint, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("MCP configuration fingerprints must be 64-character hexadecimal digests.", nameof(configurationFingerprint));
        return new ProjectMcpToolPermissionRule(server, tool, decision, fingerprint);
    }

    private static bool SameTool(ProjectMcpToolPermissionRule first, ProjectMcpToolPermissionRule second) =>
        first.ServerId.Equals(second.ServerId, StringComparison.OrdinalIgnoreCase) &&
        first.ToolName.Equals(second.ToolName, StringComparison.Ordinal);

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? "";
        return fullPath.Length > root.Length ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : fullPath;
    }
}

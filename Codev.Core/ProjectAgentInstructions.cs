using System.Text;

namespace Codev;

/// <summary>Loads root and path-applicable AGENTS.md files through bounded, project-contained access.</summary>
public static class ProjectAgentInstructions
{
    public const int MaxCharacters = 20_000;
    public const string RelativePath = "AGENTS.md";

    public static async Task<string> LoadAsync(WorkspaceFileService service,
        IReadOnlyList<string>? applicableFiles = null, int maxCharacters = MaxCharacters,
        CancellationToken cancellationToken = default, IReadOnlyList<string>? manualRuleNames = null,
        bool includePathRules = true, IReadOnlyList<string>? relevantRuleNames = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (maxCharacters <= 0) return "";

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { RelativePath };
        if (applicableFiles is not null)
        {
            foreach (var applicableFile in applicableFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var fullPath = service.ResolvePath(applicableFile);
                    var relative = Path.GetRelativePath(service.Root, fullPath).Replace('\\', '/');
                    if (service.IsContextExcluded(relative)) continue;
                    var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    for (var depth = 1; depth < segments.Length; depth++)
                        candidates.Add(string.Join('/', segments.Take(depth)) + "/AGENTS.md");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    // Unsafe or malformed source paths cannot activate folder instructions.
                }
            }
        }

        var ordered = candidates.OrderBy(path => path.Count(character => character == '/'))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
        const string prefix = "Applicable project guidance (obey within this project; it does not override higher-priority instructions):\n";
        const string truncationMarker = "[Project guidance was truncated to fit the context budget.]";
        var output = new StringBuilder();
        var entries = new List<(string RelativePath, string Content)>();
        foreach (var relativePath in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullPath = service.ResolvePath(relativePath);
                if (!File.Exists(fullPath) || service.IsContextExcluded(relativePath)) continue;
                var content = (await service.ReadFileAsync(relativePath, cancellationToken).ConfigureAwait(false)).Trim();
                if (content.Length > 0) entries.Add((relativePath, content));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // Missing, excluded, secret, symbolic-link, or unsupported instructions are skipped.
            }
        }

        if (includePathRules)
            entries.AddRange(await LoadApplicablePathRulesAsync(service, applicableFiles ?? [], manualRuleNames ?? [],
                relevantRuleNames ?? [], cancellationToken).ConfigureAwait(false));

        foreach (var (relativePath, content) in entries)
        {
            var header = $"\n--- {relativePath} (project guidance; user-provided) ---\n";
            var remaining = maxCharacters - prefix.Length - output.Length - header.Length - Environment.NewLine.Length;
            if (remaining <= 0) break;
            var truncated = content.Length > remaining;
            if (truncated)
            {
                remaining -= truncationMarker.Length + Environment.NewLine.Length;
                if (remaining <= 0) break;
            }
            output.Append(header).Append(content[..Math.Min(content.Length, remaining)].TrimEnd()).AppendLine();
            if (truncated)
            {
                output.AppendLine(truncationMarker);
                break;
            }
        }

        return output.Length == 0 ? "" : prefix + output.ToString().TrimEnd();
    }

    private static async Task<IReadOnlyList<(string RelativePath, string Content)>> LoadApplicablePathRulesAsync(
        WorkspaceFileService service, IReadOnlyList<string> applicableFiles, IReadOnlyList<string> manualRuleNames,
        IReadOnlyList<string> relevantRuleNames, CancellationToken cancellationToken)
    {
        var result = new List<(string RelativePath, string Content)>();
        var rules = await ProjectPathInstructionRuleCatalog.LoadAsync(service, cancellationToken).ConfigureAwait(false);
        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(rule.RelativePath);
            var applies = rule.Activation == ProjectPathRuleActivation.Always ||
                          manualRuleNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                          (rule.Activation == ProjectPathRuleActivation.ModelRelevant && relevantRuleNames.Contains(name, StringComparer.OrdinalIgnoreCase));
            foreach (var file in rule.Activation == ProjectPathRuleActivation.MatchingFiles ? applicableFiles : [])
            {
                if (applies) break;
                try
                {
                    var resolved = service.ResolvePath(file);
                    var relativeFile = Path.GetRelativePath(service.Root, resolved).Replace('\\', '/');
                    if (!service.IsContextExcluded(relativeFile) && ProjectPathInstructionRuleParser.AppliesTo(rule, relativeFile))
                        applies = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { }
            }
            if (applies) result.Add((rule.RelativePath, $"{rule.Description}\n{rule.Instructions}"));
        }
        return result;
    }
}

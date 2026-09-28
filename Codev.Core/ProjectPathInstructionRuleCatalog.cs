namespace Codev;

/// <summary>Reads valid, bounded rule definitions from a trusted project rules folder.</summary>
public static class ProjectPathInstructionRuleCatalog
{
    public const int MaxRules = 32;
    private const string RelativeDirectory = ".codev/rules";

    public static async Task<IReadOnlyList<ProjectPathInstructionRule>> LoadAsync(
        WorkspaceFileService service, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        var rules = new List<ProjectPathInstructionRule>();
        try
        {
            if (service.IsContextExcluded(RelativeDirectory)) return rules;
            var folder = service.ResolvePath(RelativeDirectory, allowWorkspaceRoot: true);
            if (!Directory.Exists(folder)) return rules;
            foreach (var path in Directory.EnumerateFiles(folder, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(MaxRules + 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(service.Root, path).Replace('\\', '/');
                if (rules.Count >= MaxRules || service.IsContextExcluded(relative)) continue;
                try
                {
                    var contents = await ProjectPathInstructionRuleParser.ReadDefinitionAsync(service, relative, cancellationToken).ConfigureAwait(false);
                    if (contents is not null && ProjectPathInstructionRuleParser.TryParse(relative, contents, out var rule) && rule is not null)
                        rules.Add(rule);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { }
        return rules;
    }
}

using System.Text;

namespace Codev;

/// <summary>Loads root and path-applicable AGENTS.md files through bounded, project-contained access.</summary>
public static class ProjectAgentInstructions
{
    public const int MaxCharacters = 20_000;
    public const string RelativePath = "AGENTS.md";

    public static async Task<string> LoadAsync(WorkspaceFileService service,
        IReadOnlyList<string>? applicableFiles = null, int maxCharacters = MaxCharacters,
        CancellationToken cancellationToken = default)
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

        if (applicableFiles is { Count: > 0 })
            entries.AddRange(await LoadApplicablePathRulesAsync(service, applicableFiles, cancellationToken).ConfigureAwait(false));

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
        WorkspaceFileService service, IReadOnlyList<string> applicableFiles, CancellationToken cancellationToken)
    {
        const string rulesDirectory = ".codev/rules";
        var result = new List<(string RelativePath, string Content)>();
        try
        {
            if (service.IsContextExcluded(rulesDirectory)) return result;
            var folder = service.ResolvePath(rulesDirectory, allowWorkspaceRoot: true);
            if (!Directory.Exists(folder)) return result;
            foreach (var path in Directory.EnumerateFiles(folder, "*.md", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(33))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(service.Root, path).Replace('\\', '/');
                if (result.Count >= 32 || service.IsContextExcluded(relative)) continue;
                try
                {
                    var safePath = service.ResolvePath(relative);
                    if (new FileInfo(safePath).Length > 16 * 1024) continue;
                    var contents = await service.ReadFileAsync(relative, cancellationToken).ConfigureAwait(false);
                    if (!ProjectPathInstructionRuleParser.TryParse(relative, contents, out var rule) || rule is null) continue;
                    var applies = false;
                    foreach (var file in applicableFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var resolved = service.ResolvePath(file);
                            var relativeFile = Path.GetRelativePath(service.Root, resolved).Replace('\\', '/');
                            if (!service.IsContextExcluded(relativeFile) && ProjectPathInstructionRuleParser.AppliesTo(rule, relativeFile))
                            {
                                applies = true;
                                break;
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { }
                    }
                    if (applies) result.Add((relative, $"{rule.Description}\n{rule.Instructions}"));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { }
        return result;
    }
}

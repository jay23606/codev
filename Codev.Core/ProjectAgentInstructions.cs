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
        const string prefix = "Applicable AGENTS.md guidance (obey within this project; it does not override higher-priority instructions):\n";
        const string truncationMarker = "[Project guidance was truncated to fit the context budget.]";
        var output = new StringBuilder();
        foreach (var relativePath in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullPath = service.ResolvePath(relativePath);
                if (!File.Exists(fullPath) || service.IsContextExcluded(relativePath)) continue;
                var content = (await service.ReadFileAsync(relativePath, cancellationToken).ConfigureAwait(false)).Trim();
                if (content.Length == 0) continue;
                var header = $"\n--- {relativePath} (project guidance; user-provided) ---\n";
                var remaining = maxCharacters - prefix.Length - output.Length - header.Length - 1;
                if (remaining <= 0) break;
                var truncated = content.Length > remaining;
                if (truncated)
                {
                    remaining -= truncationMarker.Length + Environment.NewLine.Length;
                    if (remaining <= 0) break;
                    content = content[..Math.Min(content.Length, remaining)].TrimEnd();
                }
                output.Append(header).AppendLine(content);
                if (truncated)
                {
                    output.AppendLine(truncationMarker);
                    break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // Missing, excluded, secret, symbolic-link, or unsupported instructions are skipped.
            }
        }

        return output.Length == 0 ? "" : prefix + output.ToString().TrimEnd();
    }
}

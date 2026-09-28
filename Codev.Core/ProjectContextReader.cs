using System.Text;

namespace Codev;

/// <summary>Builds a bounded, read-only prompt excerpt from a project workspace.</summary>
public static class ProjectContextReader
{
    public static async Task<string> ReadAsync(
        string projectPath,
        IReadOnlyList<string>? selectedFiles = null,
        IReadOnlyList<string>? contextExclusions = null,
        CancellationToken cancellationToken = default)
    {
        var service = new WorkspaceFileService(projectPath, contextExclusions);
        var output = new StringBuilder("Selected project files (limited read-only excerpts):\n");
        var files = selectedFiles is { Count: > 0 } ? selectedFiles : service.ListContextFiles(maxEntries: 300);
        const int footerReserve = 120;
        var count = 0;

        foreach (var relativePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count >= WorkspaceFileService.MaxContextFiles || output.Length >= WorkspaceFileService.MaxContextCharacters) break;
            if (service.IsContextExcluded(relativePath)) continue;

            try
            {
                var content = await service.ReadFileAsync(relativePath, cancellationToken).ConfigureAwait(false);
                var header = $"\n--- {relativePath} ---\n\n";
                var remaining = WorkspaceFileService.MaxContextCharacters - footerReserve - output.Length - header.Length - 1;
                if (remaining <= 0) break;
                var limit = Math.Min(WorkspaceFileService.MaxContextFileCharacters, remaining);
                var truncated = content.Length > limit;
                if (truncated)
                {
                    const string marker = "\n… [excerpt truncated]";
                    if (limit <= marker.Length) break;
                    content = content[..(limit - marker.Length)] + marker;
                }
                output.Append(header).AppendLine(content);
                count++;
                if (truncated) break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // Ignore files that disappeared, are unsafe, or cannot be decoded as supported text.
            }
        }

        if (count == 0) output.Append("No supported text source files were found. Ask the user to paste relevant code if needed.");
        else output.Append("\n[Context is limited to ").Append(count).AppendLine(" source files. Ask for specific files if you need more detail.]");
        return output.ToString();
    }
}

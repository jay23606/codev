using System.Text;

namespace Codev;

/// <summary>Builds bounded, read-only project excerpts and optionally adds trusted, path-applicable instructions.</summary>
public static class ProjectContextReader
{
    private sealed record SourceFile(string RelativePath, string Content);

    public static async Task<string> ReadAsync(
        string projectPath,
        IReadOnlyList<string>? selectedFiles = null,
        IReadOnlyList<string>? contextExclusions = null,
        bool includeProjectInstructions = false,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? manualRuleNames = null)
    {
        var service = new WorkspaceFileService(projectPath, contextExclusions);
        var files = selectedFiles is { Count: > 0 } ? selectedFiles : service.ListContextFiles(maxEntries: 300);
        var sourceFiles = new List<SourceFile>();
        foreach (var relativePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceFiles.Count >= WorkspaceFileService.MaxContextFiles) break;
            if (includeProjectInstructions && Path.GetFileName(relativePath).Equals(ProjectAgentInstructions.RelativePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (service.IsContextExcluded(relativePath)) continue;
            try
            {
                var content = await service.ReadFileAsync(relativePath, cancellationToken).ConfigureAwait(false);
                sourceFiles.Add(new SourceFile(relativePath, content));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // Ignore files that disappeared, are unsafe, or cannot be decoded as supported text.
            }
        }

        var projectInstructions = includeProjectInstructions
            ? await ProjectAgentInstructions.LoadAsync(service, sourceFiles.Select(file => file.RelativePath).ToArray(),
                maxCharacters: Math.Min(ProjectAgentInstructions.MaxCharacters, WorkspaceFileService.MaxContextCharacters / 2),
                cancellationToken: cancellationToken, manualRuleNames: manualRuleNames).ConfigureAwait(false)
            : "";
        var output = new StringBuilder();
        if (projectInstructions.Length > 0) output.AppendLine(projectInstructions).AppendLine();
        output.AppendLine("Selected project files (limited read-only excerpts):");
        const int footerReserve = 120;
        var count = 0;

        foreach (var source in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count >= WorkspaceFileService.MaxContextFiles || output.Length >= WorkspaceFileService.MaxContextCharacters - footerReserve) break;
            var header = $"\n--- {source.RelativePath} ---\n\n";
            var remaining = WorkspaceFileService.MaxContextCharacters - footerReserve - output.Length - header.Length - 1;
            if (remaining <= 0) break;
            var limit = Math.Min(WorkspaceFileService.MaxContextFileCharacters, remaining);
            var content = source.Content;
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

        if (count == 0) output.AppendLine("No supported text source files were found. Ask the user to paste relevant code if needed.");
        else output.Append("\n[Context is limited to ").Append(count).AppendLine(" source files. Ask for specific files if you need more detail.]");
        return output.Length <= WorkspaceFileService.MaxContextCharacters
            ? output.ToString()
            : output.ToString(0, WorkspaceFileService.MaxContextCharacters);
    }
}

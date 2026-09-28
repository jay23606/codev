using System.IO;

namespace Codev;

public sealed record ContextFileSelectionResult(int AddedCount, int IgnoredCount);

public static class ProjectContextSelection
{
    public static ContextFileSelectionResult AddDroppedFiles(WorkspaceFileService service, IList<string> selectedFiles, IEnumerable<string> droppedPaths)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(selectedFiles);
        ArgumentNullException.ThrowIfNull(droppedPaths);

        var availableFiles = service.ListContextFiles(maxEntries: 500).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var ignored = 0;
        foreach (var path in droppedPaths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { ignored++; continue; }
                var fullPath = Path.GetFullPath(path);
                if (!WorkspaceFileService.IsPathWithinRoot(service.Root, fullPath)) { ignored++; continue; }
                var relative = Path.GetRelativePath(service.Root, fullPath);
                service.ResolvePath(relative);
                if (!availableFiles.Contains(relative) || selectedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)) { ignored++; continue; }
                selectedFiles.Add(relative);
                added++;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                ignored++;
            }
        }

        return new ContextFileSelectionResult(added, ignored);
    }
}

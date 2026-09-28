using System.IO;

namespace Codev;

public sealed record ContextFileSelectionResult(int AddedCount, int IgnoredCount);

public static class ProjectContextSelection
{
    public static bool RemoveFile(IList<string> selectedFiles, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(selectedFiles);
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var removed = false;
        for (var index = selectedFiles.Count - 1; index >= 0; index--)
        {
            if (!string.Equals(selectedFiles[index], relativePath, StringComparison.OrdinalIgnoreCase)) continue;
            selectedFiles.RemoveAt(index);
            removed = true;
        }
        return removed;
    }

    public static ContextFileSelectionResult AddFiles(WorkspaceFileService service, IList<string> selectedFiles, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(selectedFiles);
        ArgumentNullException.ThrowIfNull(paths);

        var availableFiles = service.ListContextFiles(maxEntries: 500).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var ignored = 0;
        foreach (var path in paths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { ignored++; continue; }
                var fullPath = Path.GetFullPath(path);
                if (!WorkspaceFileService.IsPathWithinRoot(service.Root, fullPath)) { ignored++; continue; }
                var relative = Path.GetRelativePath(service.Root, fullPath);
                service.ResolvePath(relative);
                if (!availableFiles.Contains(relative) || selectedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase) || selectedFiles.Count >= WorkspaceFileService.MaxContextFiles) { ignored++; continue; }
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

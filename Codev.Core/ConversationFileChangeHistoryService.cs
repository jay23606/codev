using System.IO;

namespace Codev;

/// <summary>Keeps file-change journals and their rollback snapshots bounded per conversation.</summary>
public static class ConversationFileChangeHistoryService
{
    public const int MaxRetainedChanges = 200;

    public static bool Record(Conversation conversation, FileChangeRecord change, int maxRetainedChanges = MaxRetainedChanges)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(change);
        conversation.FileChanges.Add(change);
        return Trim(conversation, maxRetainedChanges);
    }

    public static bool Trim(Conversation conversation, int maxRetainedChanges = MaxRetainedChanges)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (maxRetainedChanges < 1) throw new ArgumentOutOfRangeException(nameof(maxRetainedChanges));
        if (conversation.FileChanges.Count <= maxRetainedChanges) return false;

        var removeCount = conversation.FileChanges.Count - maxRetainedChanges;
        var removed = conversation.FileChanges.Select((change, index) => (Change: change, Index: index))
            .OrderBy(item => item.Change.ChangedAt).ThenBy(item => item.Index).Take(removeCount).ToArray();
        foreach (var item in removed)
        {
            if (item.Change.TurnUserMessageIndex is int turnIndex)
            {
                if (conversation.FileChangesPrunedThroughMessageIndex is not int previous || turnIndex > previous)
                    conversation.FileChangesPrunedThroughMessageIndex = turnIndex;
            }
            else conversation.FileChangesPrunedUnlinked = true;
        }

        foreach (var item in removed.OrderByDescending(item => item.Index))
            conversation.FileChanges.RemoveAt(item.Index);

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var retainedCheckpoints = conversation.FileChanges
            .Where(change => !string.IsNullOrWhiteSpace(change.CheckpointPath))
            .Select(change => FullPathOrNull(change.CheckpointPath!))
            .Where(path => path is not null)
            .ToHashSet(pathComparer);
        var removedCheckpoints = removed.Select(item => item.Change.CheckpointPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => FullPathOrNull(path!))
            .Where(path => path is not null && !retainedCheckpoints.Contains(path))
            .Distinct(pathComparer);
        foreach (var checkpoint in removedCheckpoints) TryDeleteOwnedCheckpoint(conversation.Id, checkpoint!);
        return true;
    }

    public static string? GetStatusMessage(Conversation conversation)
    {
        if (conversation.FileChangesPrunedUnlinked)
            return "Older file history was trimmed, including changes without prompt links; code rewind may be unavailable for this conversation.";
        if (conversation.FileChangesPrunedThroughMessageIndex is not null)
            return "Older file history was trimmed to retain recent changes; code rewind for prompts that overlap the trimmed history is unavailable.";
        return null;
    }

    private static string? FullPathOrNull(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    private static void TryDeleteOwnedCheckpoint(Guid conversationId, string checkpointPath)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "checkpoints", conversationId.ToString("N"));
            var fullPath = Path.GetFullPath(checkpointPath);
            if (!WorkspaceFileService.IsPathWithinRoot(root, fullPath) ||
                string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(fullPath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 || !File.Exists(fullPath) ||
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) return;
            File.Delete(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
    }
}

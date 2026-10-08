using System.Security.Cryptography;

namespace Codev;

/// <summary>Builds a read-only diff for files changed by the latest completed Codev turn.</summary>
public static class ConversationLastTurnReviewService
{
    public static async Task<GitWorkingTreeReview> BuildSnapshotAsync(Conversation conversation, WorkspaceFileService files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(files);
        if (conversation.PendingRequestCount > 0)
            throw new InvalidOperationException("Wait for this conversation to finish before reviewing the assistant's last turn.");
        if (conversation.Messages.Count == 0 || !conversation.Messages[^1].IsAssistant)
            throw new InvalidOperationException("The assistant's last turn has no completed response to review.");
        var userIndex = -1;
        for (var index = conversation.Messages.Count - 2; index >= 0; index--)
        {
            if (!conversation.Messages[index].IsUser) continue;
            userIndex = index;
            break;
        }
        if (userIndex < 0)
            throw new InvalidOperationException("The assistant's last turn has no matching user prompt.");
        if (conversation.FileChangesPrunedUnlinked ||
            conversation.FileChangesPrunedThroughMessageIndex is int prunedThrough && userIndex <= prunedThrough)
            throw new InvalidOperationException("The assistant's last-turn review is unavailable because its file-change history is incomplete or was trimmed.");

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var histories = new Dictionary<string, List<(FileChangeRecord Change, int Order)>>(comparer);
        for (var order = 0; order < conversation.FileChanges.Count; order++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = conversation.FileChanges[order];
            if (change.TurnUserMessageIndex is int index &&
                (index < 0 || index >= conversation.Messages.Count || !conversation.Messages[index].IsUser))
                throw new InvalidOperationException($"The assistant's last-turn review cannot verify the recorded prompt association for '{change.RelativePath}'.");
            string fullPath;
            try { fullPath = Path.GetFullPath(files.ResolvePath(change.RelativePath)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"The assistant's last-turn review cannot safely inspect '{change.RelativePath}': {ex.Message}");
            }
            if (!histories.TryGetValue(fullPath, out var history)) histories[fullPath] = history = [];
            history.Add((change, order));
        }

        var changedFiles = new List<string>();
        var diffs = new List<string>();
        var totalSourceCharacters = 0;
        foreach (var history in histories.Values)
        {
            var turnChanges = history.Where(item => item.Change.TurnUserMessageIndex == userIndex)
                .OrderBy(item => item.Change.ChangedAt).ThenBy(item => item.Order).ToArray();
            if (turnChanges.Length == 0) continue;
            var first = turnChanges[0].Change;
            var lastHistoryItem = history.OrderBy(item => item.Change.ChangedAt).ThenBy(item => item.Order).Last();
            if (lastHistoryItem.Change.TurnUserMessageIndex != userIndex)
                throw new InvalidOperationException($"'{first.RelativePath}' has a later Codev or unlinked file-history change, so its last-turn diff is ambiguous.");

            var latest = lastHistoryItem.Change;
            if (latest.ResultFileExisted is not bool expectedExists ||
                (expectedExists && !IsSha256(latest.ResultSha256)) || (!expectedExists && latest.ResultSha256 is not null))
                throw new InvalidOperationException($"The latest recorded state of '{latest.RelativePath}' is incomplete, so its last-turn diff cannot be verified.");

            var currentPath = files.ResolvePath(latest.RelativePath);
            var currentExists = File.Exists(currentPath);
            FileSnapshot? current = currentExists ? await files.ReadFileSnapshotAsync(latest.RelativePath, cancellationToken) : null;
            if (currentExists != expectedExists || current is not null && !FixedHashEquals(current.Sha256, latest.ResultSha256!))
                throw new InvalidOperationException($"'{latest.RelativePath}' changed after Codev's last recorded edit. Review the current file separately.");

            string before;
            if (first.PreviousFileExisted)
            {
                if (string.IsNullOrWhiteSpace(first.CheckpointPath))
                    throw new InvalidOperationException($"The checkpoint for '{first.RelativePath}' is missing, so its last-turn diff cannot be built.");
                before = (await files.ReadCheckpointSnapshotAsync(first.RelativePath, conversation.Id, first.CheckpointPath, cancellationToken)).Content;
            }
            else before = "";
            var after = current?.Content ?? "";
            if (string.Equals(before, after, StringComparison.Ordinal)) continue;
            totalSourceCharacters = checked(totalSourceCharacters + before.Length + after.Length);
            if (totalSourceCharacters > GitRepositoryService.MaxReviewDiffCharacters * 4)
                throw new InvalidOperationException("The assistant's last-turn changes exceed the review size limit. Review the files individually.");

            var diff = UnifiedDiff.Format(latest.RelativePath, before, after);
            if (diff.Contains("\0", StringComparison.Ordinal) || diff.Contains('\uFFFD'))
                throw new InvalidOperationException($"'{latest.RelativePath}' does not contain readable text, so it cannot be included in a last-turn review.");
            changedFiles.Add(latest.RelativePath);
            diffs.Add(diff);
            if (changedFiles.Count > GitRepositoryService.MaxReviewFiles)
                throw new InvalidOperationException("The assistant's last turn changed too many files for one review. Review the files individually.");
        }

        if (changedFiles.Count == 0)
            throw new InvalidOperationException("Codev has no net file changes associated with the assistant's last completed turn.");
        var output = string.Join("\n", diffs);
        if (output.Length > GitRepositoryService.MaxReviewDiffCharacters)
            throw new InvalidOperationException("The assistant's last-turn diff exceeds the review size limit. Review the files individually.");
        return new GitWorkingTreeReview("assistant's last turn", changedFiles, output, Truncated: false);
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        try { return Convert.FromHexString(value).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static bool FixedHashEquals(string first, string second)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first), Convert.FromHexString(second)); }
        catch (FormatException) { return false; }
    }
}

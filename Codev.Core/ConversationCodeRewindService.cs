using System.Security.Cryptography;

namespace Codev;

/// <summary>Builds and applies a fail-closed restore plan for Codev-managed file changes after a prompt.</summary>
public static class ConversationCodeRewindService
{
    public static async Task<CodeRewindPlan> BuildPlanAsync(Conversation conversation, int userMessageIndex,
        WorkspaceFileService files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(files);
        if (conversation.PendingRequestCount > 0)
            throw new InvalidOperationException("Cancel queued turns and wait for this conversation to finish before rewinding code.");
        if (conversation.Messages is null || userMessageIndex < 0 || userMessageIndex >= conversation.Messages.Count ||
            !conversation.Messages[userMessageIndex].IsUser)
            throw new ArgumentOutOfRangeException(nameof(userMessageIndex), "Choose an existing user prompt to rewind before.");
        if (conversation.FileChangesPrunedUnlinked)
            throw new InvalidOperationException("Code rewind is unavailable because older trimmed file changes had no prompt association.");
        if (conversation.FileChangesPrunedThroughMessageIndex is int prunedThrough && userMessageIndex <= prunedThrough)
            throw new InvalidOperationException("Code rewind is unavailable for this prompt because its file-change history was trimmed. Newer prompts may still be rewindable.");

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var grouped = new Dictionary<string, List<(FileChangeRecord Change, int Order)>>(comparer);
        for (var order = 0; order < conversation.FileChanges.Count; order++)
        {
            var change = conversation.FileChanges[order];
            if (change.TurnUserMessageIndex is int turnIndex &&
                (turnIndex < 0 || turnIndex >= conversation.Messages.Count || !conversation.Messages[turnIndex].IsUser))
                throw new InvalidOperationException($"Code rewind cannot safely use the recorded prompt association for '{change.RelativePath}'. Review Files history first.");
            string canonicalPath;
            try { canonicalPath = Path.GetFullPath(files.ResolvePath(change.RelativePath)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Code rewind cannot safely inspect the recorded path '{change.RelativePath}': {ex.Message}");
            }
            if (!grouped.TryGetValue(canonicalPath, out var changes)) grouped[canonicalPath] = changes = [];
            changes.Add((change, order));
        }

        var plans = new List<CodeRewindFilePlan>();
        foreach (var (fullPath, history) in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var affected = history.Where(item => item.Change.TurnUserMessageIndex >= userMessageIndex)
                .OrderBy(item => item.Change.ChangedAt).ThenBy(item => item.Order).ToArray();
            if (affected.Length == 0) continue;
            var first = affected[0].Change;
            if (history.Any(item => item.Change.TurnUserMessageIndex is null &&
                                    (item.Change.ChangedAt > first.ChangedAt || item.Change.ChangedAt == first.ChangedAt && item.Order > affected[0].Order)))
                throw new InvalidOperationException($"Code rewind cannot safely restore '{first.RelativePath}' because a later file-history change has no prompt association.");

            var last = history.OrderBy(item => item.Change.ChangedAt).ThenBy(item => item.Order).Last().Change;
            if (last.ResultFileExisted is not bool expectedExists ||
                (expectedExists && !IsSha256(last.ResultSha256)) || (!expectedExists && last.ResultSha256 is not null))
                throw new InvalidOperationException($"Code rewind cannot verify the latest recorded state of '{last.RelativePath}'. This file change predates state tracking or has incomplete history.");

            var currentFullPath = files.ResolvePath(last.RelativePath);
            var currentExists = File.Exists(currentFullPath);
            FileSnapshot? current = currentExists ? await files.ReadFileSnapshotAsync(last.RelativePath, cancellationToken) : null;
            if (currentExists != expectedExists || current is not null && !FixedHashEquals(current.Sha256, last.ResultSha256!))
                throw new InvalidOperationException($"'{last.RelativePath}' has changed since Codev's latest recorded edit. Review the current file and use Files history before rewinding code.");

            string? targetContent = null;
            string? targetHash = null;
            if (first.PreviousFileExisted)
            {
                if (string.IsNullOrWhiteSpace(first.CheckpointPath))
                    throw new InvalidOperationException($"The checkpoint for '{first.RelativePath}' is missing; code rewind stopped without changing files.");
                var targetSnapshot = await files.ReadCheckpointSnapshotAsync(first.RelativePath, conversation.Id, first.CheckpointPath, cancellationToken);
                targetContent = targetSnapshot.Content;
                targetHash = targetSnapshot.Sha256;
            }

            if (currentExists == first.PreviousFileExisted && (!currentExists || FixedHashEquals(current!.Sha256, targetHash!)))
                continue;

            if (currentExists && first.PreviousFileExisted)
                FileHardLinkInspector.EnsureSafeToReplaceExistingFile(currentFullPath, last.RelativePath);

            plans.Add(new CodeRewindFilePlan(first.RelativePath, currentExists, current?.Sha256, current?.Content,
                first.PreviousFileExisted, first.CheckpointPath, targetContent, targetHash, first, affected.Select(item => item.Change).ToArray()));
        }

        return new CodeRewindPlan(userMessageIndex, plans);
    }

    /// <summary>Applies the complete reviewed plan, rolling back files already restored if a later restore fails.</summary>
    public static async Task ApplyPlanAsync(Conversation conversation, CodeRewindPlan plan, WorkspaceFileService files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(files);
        if (conversation.PendingRequestCount > 0)
            throw new InvalidOperationException("Cancel queued turns and wait for this conversation to finish before rewinding code.");
        if (plan.UserMessageIndex < 0 || plan.UserMessageIndex >= conversation.Messages.Count || !conversation.Messages[plan.UserMessageIndex].IsUser)
            throw new InvalidOperationException("The selected prompt changed after the code rewind was reviewed. Review the operation again.");

        var applied = new List<(CodeRewindFilePlan Plan, string? RollbackCheckpoint)>();
        try
        {
            foreach (var item in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rollback = await files.RestoreFileStateAsync(item.RelativePath, conversation.Id, item.RestoreFileExisted,
                    item.RestoreCheckpointPath, item.ExpectedCurrentSha256, cancellationToken);
                applied.Add((item, rollback));
            }
        }
        catch (Exception applyError)
        {
            var rollbackErrors = new List<string>();
            foreach (var (item, rollbackCheckpoint) in applied.AsEnumerable().Reverse())
            {
                try
                {
                    await files.RestoreFileStateAsync(item.RelativePath, conversation.Id, item.ExpectedCurrentFileExisted,
                        rollbackCheckpoint, item.RestoreFileExisted ? item.RestoreSha256 : null, CancellationToken.None);
                }
                catch (Exception rollbackError) { rollbackErrors.Add($"{item.RelativePath}: {rollbackError.Message}"); }
            }

            var explanation = rollbackErrors.Count == 0
                ? "No planned file changes remain applied."
                : "Some files may still contain the rewound version; rollback also failed for: " + string.Join("; ", rollbackErrors);
            throw new InvalidOperationException($"Code rewind stopped: {applyError.Message} {explanation}", applyError);
        }

        foreach (var (item, rollback) in applied)
            ConversationFileChangeHistoryService.Record(conversation, new FileChangeRecord(item.RelativePath, rollback, DateTimeOffset.Now, "Code rewind",
                PreviousFileExisted: item.ExpectedCurrentFileExisted, TurnUserMessageIndex: plan.UserMessageIndex,
                ResultFileExisted: item.RestoreFileExisted, ResultSha256: item.RestoreSha256));
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

public sealed record CodeRewindPlan(int UserMessageIndex, IReadOnlyList<CodeRewindFilePlan> Files);

public sealed record CodeRewindFilePlan(string RelativePath, bool ExpectedCurrentFileExisted, string? ExpectedCurrentSha256,
    string? ExpectedCurrentContent, bool RestoreFileExisted, string? RestoreCheckpointPath, string? RestoreContent, string? RestoreSha256,
    FileChangeRecord FirstAffectedChange, IReadOnlyList<FileChangeRecord> AffectedChanges);

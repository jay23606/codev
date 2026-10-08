namespace Codev;

/// <summary>Creates an independent conversation fork, including isolated copies of local rollback checkpoints.</summary>
public static class ConversationForkService
{
    public static async Task<Conversation> CreateSideChatAsync(Conversation source, int messageIndex,
        string? checkpointRoot = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (messageIndex < 0 || messageIndex >= source.Messages.Count || !source.Messages[messageIndex].IsUser)
            throw new ArgumentOutOfRangeException(nameof(messageIndex), "A side chat must start from a user prompt in the source conversation.");

        var sideChat = await CreateForkAsync(source, checkpointRoot, cancellationToken).ConfigureAwait(false);
        var sourcePrompt = source.Messages[messageIndex].Content;
        var prompt = sourcePrompt.Trim().Replace('\r', ' ').Replace('\n', ' ');
        sideChat.Title = prompt.Length == 0 ? "Side chat" : $"Side chat · {prompt}";
        if (sideChat.Title.Length > 72) sideChat.Title = sideChat.Title[..69].TrimEnd() + "…";
        sideChat.Messages = sideChat.Messages.Take(messageIndex)
            .Select((message, index) => message with { MessageIndex = index, IsQueued = false }).ToList();
        if (!string.IsNullOrWhiteSpace(sideChat.CompactionSummary) &&
            !ConversationCompactionService.IsValidRange(sideChat.Messages,
                sideChat.CompactionFromMessageCount, sideChat.CompactionThroughMessageCount))
            ConversationCompactionService.Clear(sideChat);
        sideChat.Draft = sourcePrompt;
        sideChat.PendingTurns = [];
        sideChat.PendingRequestCount = 0;
        return sideChat;
    }

    public static async Task<Conversation> CreateForkAsync(Conversation source, string? checkpointRoot = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var fork = ConversationPersistence.CreateSnapshot([source]).Single();
        var sourceId = source.Id;
        fork.Id = Guid.NewGuid();
        fork.ParentConversationId = null;
        fork.DelegatedFromMessageIndex = null;
        fork.DelegatedAgentName = null;
        fork.DelegatedResultReported = false;
        fork.ChildWorktreeBranch = null;
        fork.ChildWorktreeStartCommit = null;
        fork.ChildConversations = [];
        fork.Title = CreateForkTitle(source.Title);
        fork.UpdatedAt = DateTimeOffset.Now;

        var root = Path.GetFullPath(checkpointRoot ?? Path.Combine(
            CodevDataPaths.LocalDataRoot, "Codev", "checkpoints"));
        var sourceDirectory = Path.Combine(root, sourceId.ToString("N"));
        var targetDirectory = Path.Combine(root, fork.Id.ToString("N"));
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("The checkpoint folder is a link; the conversation was not forked.");
        if (Directory.Exists(sourceDirectory) && (File.GetAttributes(sourceDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("The source checkpoint folder is a link; the conversation was not forked.");
        if (Directory.Exists(targetDirectory) || File.Exists(targetDirectory))
            throw new IOException("A checkpoint folder already exists for the new conversation; try the fork again.");
        var sourceRootPrefix = Path.EndsInDirectorySeparator(sourceDirectory) ? sourceDirectory : sourceDirectory + Path.DirectorySeparatorChar;
        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var copiedPaths = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var remappedChanges = new List<FileChangeRecord>(fork.FileChanges.Count);
        var createdTargetDirectory = false;

        try
        {
            foreach (var change in fork.FileChanges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(change.CheckpointPath))
                {
                    remappedChanges.Add(change);
                    continue;
                }

                var checkpoint = Path.GetFullPath(change.CheckpointPath);
                if (!checkpoint.StartsWith(sourceRootPrefix, comparer) || !File.Exists(checkpoint) ||
                    (File.GetAttributes(checkpoint) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("A conversation checkpoint is missing or is outside its local checkpoint folder; the fork was not created.");
                var info = new FileInfo(checkpoint);
                if (info.Length > 500_000)
                    throw new InvalidOperationException("A conversation checkpoint is larger than the 500 KB restore limit; the fork was not created.");

                if (!copiedPaths.TryGetValue(checkpoint, out var copiedPath))
                {
                    Directory.CreateDirectory(targetDirectory);
                    createdTargetDirectory = true;
                    var targetName = $"{copiedPaths.Count:D4}-{Path.GetFileName(checkpoint)}";
                    copiedPath = Path.Combine(targetDirectory, targetName);
                    await using var input = new FileStream(checkpoint, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    await using var output = new FileStream(copiedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    copiedPaths.Add(checkpoint, copiedPath);
                }
                remappedChanges.Add(change with { CheckpointPath = copiedPath });
            }

            fork.FileChanges = remappedChanges;
            return fork;
        }
        catch
        {
            if (createdTargetDirectory && Directory.Exists(targetDirectory))
            {
                var targetAttributes = File.GetAttributes(targetDirectory);
                if ((targetAttributes & FileAttributes.ReparsePoint) == 0) Directory.Delete(targetDirectory, recursive: true);
            }
            throw;
        }
    }

    private static string CreateForkTitle(string title)
    {
        var baseTitle = string.IsNullOrWhiteSpace(title) ? "Conversation" : title.Trim();
        const string suffix = " · fork";
        const int maxLength = 100;
        if (baseTitle.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) baseTitle = baseTitle[..^suffix.Length].TrimEnd();
        if (baseTitle.Length > maxLength - suffix.Length) baseTitle = baseTitle[..(maxLength - suffix.Length)].TrimEnd();
        return baseTitle + suffix;
    }
}

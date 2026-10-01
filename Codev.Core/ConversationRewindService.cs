namespace Codev;

/// <summary>Restores conversation history to before one user prompt without changing project files.</summary>
public static class ConversationRewindService
{
    public static string RestoreConversationOnly(Conversation conversation, int userMessageIndex)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.PendingRequestCount > 0)
            throw new InvalidOperationException("Cancel queued turns and wait for this conversation to finish before rewinding.");
        if (conversation.Messages is null || userMessageIndex < 0 || userMessageIndex >= conversation.Messages.Count ||
            !conversation.Messages[userMessageIndex].IsUser)
            throw new ArgumentOutOfRangeException(nameof(userMessageIndex), "Choose an existing user prompt to rewind before.");

        var message = conversation.Messages[userMessageIndex];
        var draft = message.Content;
        for (var index = 0; index < conversation.FileChanges.Count; index++)
        {
            var change = conversation.FileChanges[index];
            if (change.TurnUserMessageIndex >= userMessageIndex)
                conversation.FileChanges[index] = change with { TurnUserMessageIndex = null };
        }
        if (userMessageIndex == 0)
        {
            conversation.FileChangesPrunedThroughMessageIndex = null;
            conversation.FileChangesPrunedUnlinked = false;
        }
        else if (conversation.FileChangesPrunedThroughMessageIndex is int prunedThrough && prunedThrough >= userMessageIndex)
            conversation.FileChangesPrunedThroughMessageIndex = userMessageIndex - 1;
        conversation.Messages.RemoveRange(userMessageIndex, conversation.Messages.Count - userMessageIndex);
        for (var index = 0; index < conversation.Messages.Count; index++)
            conversation.Messages[index] = conversation.Messages[index] with { MessageIndex = index };
        conversation.Draft = draft;
        conversation.LastPromptTokens = 0;
        conversation.LastPromptOutputTokens = null;
        conversation.LastPromptContext = 0;
        conversation.LastPromptModel = "";
        conversation.LastPromptProvider = "";
        if (userMessageIndex < conversation.CompactionThroughMessageCount ||
            conversation.CompactionThroughMessageCount >= conversation.Messages.Count)
            ConversationCompactionService.Clear(conversation);
        conversation.UpdatedAt = DateTimeOffset.Now;
        return draft;
    }
}

namespace Codev;

/// <summary>Clears chat text while preserving the conversation's project, model, context selection, and file history.</summary>
public static class ConversationHistoryClearService
{
    public static bool CanClear(Conversation? conversation, bool isGenerating) =>
        conversation is not null && !isGenerating && conversation.PendingRequestCount == 0 &&
        conversation.PendingTurns is { Count: 0 };

    public static bool Clear(Conversation conversation, bool isGenerating = false)
    {
        if (!CanClear(conversation, isGenerating)) return false;
        conversation.Messages.Clear();
        conversation.Title = "New conversation";
        conversation.Draft = "";
        conversation.LastPromptTokens = 0;
        conversation.LastPromptOutputTokens = null;
        conversation.LastPromptContext = 0;
        conversation.LastPromptModel = "";
        conversation.LastPromptProvider = "";
        conversation.TaskChecklist.Clear();
        ConversationCompactionService.Clear(conversation);
        conversation.UpdatedAt = DateTimeOffset.Now;
        return true;
    }
}

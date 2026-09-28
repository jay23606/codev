namespace Codev;

/// <summary>Builds detached conversation snapshots for persistence work done off the UI thread.</summary>
public static class ConversationPersistence
{
    public static List<Conversation> CreateSnapshot(IEnumerable<Conversation> conversations)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        return conversations.Select(conversation => new Conversation
        {
            Id = conversation.Id,
            Title = conversation.Title,
            Draft = conversation.Draft,
            Model = conversation.Model,
            Provider = conversation.Provider,
            IsPlanMode = conversation.IsPlanMode,
            IsCodeTask = conversation.IsCodeTask,
            IncludeProjectContextForHosted = conversation.IncludeProjectContextForHosted,
            IncludeRepoMap = conversation.IncludeRepoMap,
            NumCtx = conversation.NumCtx,
            Temperature = conversation.Temperature,
            LastPromptTokens = conversation.LastPromptTokens,
            LastPromptContext = conversation.LastPromptContext,
            LastPromptModel = conversation.LastPromptModel,
            IsPinned = conversation.IsPinned,
            IsArchived = conversation.IsArchived,
            UpdatedAt = conversation.UpdatedAt,
            ProjectPath = conversation.ProjectPath,
            Messages = [.. conversation.Messages],
            FileChanges = [.. conversation.FileChanges],
            ContextFiles = [.. conversation.ContextFiles],
            PendingDiffComments = conversation.PendingDiffComments?.Select(comment => comment with { }).ToList() ?? [],
            PendingTurns = conversation.PendingTurns?.Select(turn => turn with
            {
                ContextFiles = turn.ContextFiles is null ? [] : [.. turn.ContextFiles],
                ContextExclusions = turn.ContextExclusions is null ? [] : [.. turn.ContextExclusions]
            }).ToList() ?? []
        }).ToList();
    }
}

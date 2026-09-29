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
            ThinkEnabled = conversation.ThinkEnabled,
            OutputStyle = conversation.OutputStyle,
            AllowHostedCodeTask = conversation.AllowHostedCodeTask,
            IncludeProjectContextForHosted = conversation.IncludeProjectContextForHosted,
            IncludeRepoMap = conversation.IncludeRepoMap,
            NumCtx = conversation.NumCtx,
            Temperature = ConversationSamplingSettings.NormalizeTemperature(conversation.Temperature),
            TopP = ConversationSamplingSettings.NormalizeProbability(conversation.TopP),
            TopK = ConversationSamplingSettings.NormalizeTopK(conversation.TopK),
            PresencePenalty = ConversationSamplingSettings.NormalizePenalty(conversation.PresencePenalty),
            RepeatPenalty = ConversationSamplingSettings.NormalizePenalty(conversation.RepeatPenalty),
            NumPredict = ConversationSamplingSettings.NormalizeOutputTokens(conversation.NumPredict),
            OpenAiReasoningEffort = OpenAiGenerationSettings.NormalizeEffort(conversation.OpenAiReasoningEffort),
            OpenAiVerbosity = OpenAiGenerationSettings.NormalizeVerbosity(conversation.OpenAiVerbosity),
            OpenAiReasoningMode = OpenAiGenerationSettings.NormalizeReasoningMode(conversation.OpenAiReasoningMode, conversation.Model),
            LastPromptTokens = conversation.LastPromptTokens,
            LastPromptOutputTokens = conversation.LastPromptOutputTokens,
            LastPromptContext = conversation.LastPromptContext,
            LastPromptModel = conversation.LastPromptModel,
            LastPromptProvider = conversation.LastPromptProvider,
            CompactionSummary = conversation.CompactionSummary,
            CompactionFromMessageCount = conversation.CompactionFromMessageCount,
            CompactionThroughMessageCount = conversation.CompactionThroughMessageCount,
            TaskChecklist = Codev.TaskChecklistService.NormalizeImported(conversation.TaskChecklist).ToList(),
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
                ContextExclusions = turn.ContextExclusions is null ? [] : [.. turn.ContextExclusions],
                OutputStyle = ConversationOutputStyles.Normalize(turn.OutputStyle),
                TopP = ConversationSamplingSettings.NormalizeProbability(turn.TopP),
                TopK = ConversationSamplingSettings.NormalizeTopK(turn.TopK),
                PresencePenalty = ConversationSamplingSettings.NormalizePenalty(turn.PresencePenalty),
                RepeatPenalty = ConversationSamplingSettings.NormalizePenalty(turn.RepeatPenalty),
                NumPredict = ConversationSamplingSettings.NormalizeOutputTokens(turn.NumPredict),
                OpenAiReasoningEffort = OpenAiGenerationSettings.NormalizeEffort(turn.OpenAiReasoningEffort),
                OpenAiVerbosity = OpenAiGenerationSettings.NormalizeVerbosity(turn.OpenAiVerbosity),
                OpenAiReasoningMode = OpenAiGenerationSettings.NormalizeReasoningMode(turn.OpenAiReasoningMode, turn.Model)
            }).ToList() ?? []
        }).ToList();
    }
}

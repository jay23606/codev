using System.IO;
using System.Text.Json;

namespace Codev;

/// <summary>Creates and imports additive, portable conversation-history backups.</summary>
public static class ConversationBackupService
{
    public static string Export(IEnumerable<Conversation> conversations, JsonSerializerOptions options)
    {
        var items = conversations.ToArray();
        var includedIds = items.Select(conversation => conversation.Id).ToHashSet();
        var snapshot = items.Select(conversation => new Conversation
        {
            Id = conversation.Id,
            ParentConversationId = conversation.ParentConversationId is { } parentId && includedIds.Contains(parentId) ? parentId : null,
            DelegatedFromMessageIndex = conversation.DelegatedFromMessageIndex,
            DelegatedAgentName = AgentProfileCatalog.NormalizeReferenceName(conversation.DelegatedAgentName),
            DelegatedResultReported = conversation.DelegatedResultReported,
            ChildWorktreeBranch = conversation.ChildWorktreeBranch,
            ChildWorktreeStartCommit = conversation.ChildWorktreeStartCommit,
            ChildConversationsExpanded = conversation.ChildConversationsExpanded,
            Title = conversation.Title,
            Draft = conversation.Draft,
            Model = conversation.Model,
            Provider = conversation.Provider,
            IsPlanMode = conversation.IsPlanMode,
            IsCodeTask = conversation.IsCodeTask,
            AgentProfileName = AgentProfileCatalog.NormalizeReferenceName(conversation.AgentProfileName),
            ThinkEnabled = conversation.ThinkEnabled,
            OutputStyle = conversation.OutputStyle,
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
            TaskChecklist = TaskChecklistService.NormalizeImported(conversation.TaskChecklist).ToList(),
            IsPinned = conversation.IsPinned,
            IsArchived = conversation.IsArchived,
            UpdatedAt = conversation.UpdatedAt,
            ProjectPath = conversation.ProjectPath,
            Messages = [.. conversation.Messages],
            FileChanges = conversation.FileChanges.Where(change => change is not null)
                .Select(change => change with { CheckpointPath = null }).ToList(),
            FileChangesPrunedThroughMessageIndex = conversation.FileChangesPrunedThroughMessageIndex,
            FileChangesPrunedUnlinked = conversation.FileChangesPrunedUnlinked,
            ContextFiles = [.. conversation.ContextFiles],
            PendingDiffComments = conversation.PendingDiffComments?.Select(comment => comment with { }).ToList() ?? [],
            PendingTurns = null!
        }).ToList();
        return JsonSerializer.Serialize(snapshot, options);
    }

    public static IReadOnlyList<Conversation> Import(string json, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("The selected backup is empty.");
        if (json.Length > 100_000_000) throw new InvalidDataException("The selected backup is larger than the 100 MB import limit.");

        List<Conversation?>? imported;
        try { imported = JsonSerializer.Deserialize<List<Conversation?>>(json, options); }
        catch (JsonException ex) { throw new InvalidDataException("The selected file is not a valid Codev conversation backup.", ex); }
        if (imported is null || imported.Count == 0) throw new InvalidDataException("The backup contains no conversations.");
        if (imported.Count > 10_000) throw new InvalidDataException("The backup contains too many conversations.");

        var originalConversations = new Dictionary<Guid, Conversation>();
        var remappedIds = new Dictionary<Guid, Guid>();
        foreach (var item in imported)
        {
            if (item is null) throw new InvalidDataException("The backup contains an invalid empty conversation entry.");
            if (!originalConversations.TryAdd(item.Id, item)) throw new InvalidDataException("The backup contains duplicate conversation IDs.");
            remappedIds[item.Id] = Guid.NewGuid();
        }

        var result = new List<Conversation>(imported.Count);
        foreach (var item in imported)
        {
            if (item is null) throw new InvalidDataException("The backup contains an invalid empty conversation entry.");
            if (item.Messages is null || item.Messages.Count > 100_000)
                throw new InvalidDataException("A conversation has an invalid message list.");
            if (item.Messages.Any(message => message is null || (message.Role != "user" && message.Role != "assistant") || message.Content is null))
                throw new InvalidDataException("A conversation contains an unsupported or invalid message.");

            var originalId = item.Id;
            var originalParentId = item.ParentConversationId;
            item.Id = remappedIds[originalId];
            if (originalParentId is { } parentId && parentId != originalId &&
                originalConversations.TryGetValue(parentId, out var parent) && parent.ParentConversationId is null)
            {
                item.ParentConversationId = remappedIds[parentId];
                if (item.DelegatedFromMessageIndex is not { } messageIndex || messageIndex < 0 || messageIndex >= originalConversations[parentId].Messages.Count ||
                    !originalConversations[parentId].Messages[messageIndex].IsAssistant ||
                    AgentProfileCatalog.NormalizeReferenceName(item.DelegatedAgentName) is null)
                {
                    item.DelegatedFromMessageIndex = null;
                    item.DelegatedAgentName = null;
                    item.DelegatedResultReported = false;
                }
                item.ChildWorktreeBranch = item.ChildWorktreeBranch is { Length: <= 120 } branch && branch.StartsWith("codev/child-", StringComparison.Ordinal)
                    ? branch : null;
                item.ChildWorktreeStartCommit = item.ChildWorktreeStartCommit is { } commit && (commit.Length is 40 or 64) && commit.All(Uri.IsHexDigit)
                    ? commit : null;
            }
            else
            {
                item.ParentConversationId = null;
                item.DelegatedFromMessageIndex = null;
                item.DelegatedAgentName = null;
                item.DelegatedResultReported = false;
                item.ChildWorktreeBranch = null;
                item.ChildWorktreeStartCommit = null;
            }
            item.Title = Limit(item.Title, 300, "Imported conversation");
            item.Model = Limit(item.Model, 200, "");
            if (item.Provider is not ("ollama" or CloudModelProviders.OpenAI or CloudModelProviders.Anthropic)) item.Provider = "ollama";
            // Provider data-sharing approvals are local consent and never transfer through portable backups.
            item.AllowHostedCodeTask = false;
            item.IncludeProjectContextForHosted = false;
            if (item.Provider != "ollama" || item.IsPlanMode) item.IsCodeTask = false;
            item.LastPromptModel = Limit(item.LastPromptModel, 200, "");
            if (item.LastPromptProvider is not ("ollama" or CloudModelProviders.OpenAI or CloudModelProviders.Anthropic)) item.LastPromptProvider = "";
            item.CompactionSummary = Limit(item.CompactionSummary, ConversationCompactionService.MaxSummaryCharacters, "");
            if (string.IsNullOrWhiteSpace(item.CompactionSummary) ||
                !ConversationCompactionService.IsValidRange(item.Messages, item.CompactionFromMessageCount, item.CompactionThroughMessageCount))
            {
                item.CompactionSummary = "";
                item.CompactionFromMessageCount = 0;
                item.CompactionThroughMessageCount = 0;
            }
            item.TaskChecklist = TaskChecklistService.NormalizeImported(item.TaskChecklist).ToList();
            item.Temperature = ConversationSamplingSettings.Normalize(item.Temperature);
            item.TopP = ConversationSamplingSettings.NormalizeProbability(item.TopP);
            item.TopK = ConversationSamplingSettings.NormalizeTopK(item.TopK);
            item.PresencePenalty = ConversationSamplingSettings.NormalizePenalty(item.PresencePenalty);
            item.RepeatPenalty = ConversationSamplingSettings.NormalizePenalty(item.RepeatPenalty);
            item.NumPredict = ConversationSamplingSettings.NormalizeOutputTokens(item.NumPredict);
            item.OpenAiReasoningEffort = OpenAiGenerationSettings.NormalizeEffort(item.OpenAiReasoningEffort);
            item.OpenAiVerbosity = OpenAiGenerationSettings.NormalizeVerbosity(item.OpenAiVerbosity);
            item.OpenAiReasoningMode = OpenAiGenerationSettings.NormalizeReasoningMode(item.OpenAiReasoningMode, item.Model);
            item.AgentProfileName = AgentProfileCatalog.NormalizeReferenceName(item.AgentProfileName);
            item.Draft = item.Draft is null ? "" : item.Draft[..Math.Min(item.Draft.Length, 500_000)];
            item.PendingRequestCount = 0;
            item.PendingTurns = [];
            item.ContextFiles = item.ContextFiles?.Take(WorkspaceFileService.MaxContextFiles).ToList() ?? [];
            item.PendingDiffComments = item.PendingDiffComments?.Where(comment => comment is not null)
                .Take(GitDiffPromptBuilder.MaxComments)
                .Select(comment => new GitDiffComment(
                    Limit(comment.RelativePath, 500, "selected file"),
                    Limit(comment.SelectedDiff, GitDiffPromptBuilder.MaxDiffLength, ""),
                    Limit(comment.Comment, GitDiffPromptBuilder.MaxCommentLength, "")))
                .Where(comment => !string.IsNullOrWhiteSpace(comment.SelectedDiff) && !string.IsNullOrWhiteSpace(comment.Comment)).ToList() ?? [];
            item.FileChanges = item.FileChanges?.Where(change => change is not null)
                .Select(change => change with { CheckpointPath = null }).ToList() ?? [];
            ConversationFileChangeHistoryService.Trim(item);
            item.Messages = item.Messages.Select(message =>
                message.Role == "assistant" && string.IsNullOrWhiteSpace(message.Content)
                    ? new ChatMessage("assistant", "This request did not finish before it was imported.")
                    : message).ToList();
            result.Add(item);
        }

        return result;
    }

    private static string Limit(string? value, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

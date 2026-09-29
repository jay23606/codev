using System.IO;
using System.Text.Json;

namespace Codev;

/// <summary>Creates and imports additive, portable conversation-history backups.</summary>
public static class ConversationBackupService
{
    public static string Export(IEnumerable<Conversation> conversations, JsonSerializerOptions options)
    {
        var snapshot = conversations.Select(conversation => new Conversation
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
            IncludeProjectContextForHosted = conversation.IncludeProjectContextForHosted,
            IncludeRepoMap = conversation.IncludeRepoMap,
            NumCtx = conversation.NumCtx,
            Temperature = ConversationSamplingSettings.NormalizeTemperature(conversation.Temperature),
            TopP = ConversationSamplingSettings.NormalizeProbability(conversation.TopP),
            TopK = ConversationSamplingSettings.NormalizeTopK(conversation.TopK),
            PresencePenalty = ConversationSamplingSettings.NormalizePenalty(conversation.PresencePenalty),
            RepeatPenalty = ConversationSamplingSettings.NormalizePenalty(conversation.RepeatPenalty),
            NumPredict = ConversationSamplingSettings.NormalizeOutputTokens(conversation.NumPredict),
            LastPromptTokens = conversation.LastPromptTokens,
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

        var result = new List<Conversation>(imported.Count);
        foreach (var item in imported)
        {
            if (item is null) throw new InvalidDataException("The backup contains an invalid empty conversation entry.");
            if (item.Messages is null || item.Messages.Count > 100_000)
                throw new InvalidDataException("A conversation has an invalid message list.");
            if (item.Messages.Any(message => message is null || (message.Role != "user" && message.Role != "assistant") || message.Content is null))
                throw new InvalidDataException("A conversation contains an unsupported or invalid message.");

            item.Id = Guid.NewGuid();
            item.Title = Limit(item.Title, 300, "Imported conversation");
            item.Model = Limit(item.Model, 200, "");
            if (item.Provider is not ("ollama" or CloudModelProviders.OpenAI or CloudModelProviders.Anthropic)) item.Provider = "ollama";
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

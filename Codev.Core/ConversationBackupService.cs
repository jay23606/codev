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
            IncludeProjectContextForHosted = conversation.IncludeProjectContextForHosted,
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
            FileChanges = conversation.FileChanges.Where(change => change is not null)
                .Select(change => change with { CheckpointPath = null }).ToList(),
            ContextFiles = [.. conversation.ContextFiles],
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
            item.Model = Limit(item.Model, 200, "qwen3-coder:30b");
            if (item.Provider is not ("ollama" or CloudModelProviders.OpenAI or CloudModelProviders.Anthropic)) item.Provider = "ollama";
            item.LastPromptModel = Limit(item.LastPromptModel, 200, "");
            item.Temperature = ConversationSamplingSettings.Normalize(item.Temperature);
            item.Draft = item.Draft is null ? "" : item.Draft[..Math.Min(item.Draft.Length, 500_000)];
            item.PendingRequestCount = 0;
            item.PendingTurns = [];
            item.ContextFiles = item.ContextFiles?.Take(WorkspaceFileService.MaxContextFiles).ToList() ?? [];
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

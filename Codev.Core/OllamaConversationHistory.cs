namespace Codev;

/// <summary>Repairs persisted chat history to satisfy Ollama's role ordering rules.</summary>
public static class OllamaConversationHistory
{
    public static IReadOnlyList<ChatMessage> Normalize(IEnumerable<ChatMessage> messages)
    {
        var normalized = new List<ChatMessage>();
        var system = new List<string>();
        foreach (var message in messages)
        {
            var role = message.Role?.Trim().ToLowerInvariant();
            var content = message.Content?.Trim();
            if (string.IsNullOrWhiteSpace(content)) continue;
            if (role == "system") { system.Add(content); continue; }
            if (role is not ("user" or "assistant")) continue;
            if (normalized.Count == 0 && role == "assistant") continue;
            if (normalized.Count > 0 && normalized[^1].Role == role)
                normalized[^1] = normalized[^1] with { Content = normalized[^1].Content + "\n\n" + content };
            else
                normalized.Add(new ChatMessage(role, content));
        }
        if (system.Count > 0) normalized.Insert(0, new ChatMessage("system", string.Join("\n\n", system)));
        return normalized;
    }
}

namespace Codev;

public static class ConversationSearch
{
    public static bool Matches(Conversation conversation, string? query)
    {
        var search = query?.Trim();
        if (string.IsNullOrWhiteSpace(search)) return true;
        if ((conversation.Title ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)) return true;
        return conversation.Messages.Any(message => (message.Content ?? "").Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    public static string? FindMessageExcerpt(Conversation conversation, string? query, int maxLength = 120)
    {
        var search = query?.Trim();
        if (string.IsNullOrWhiteSpace(search) || maxLength < 12) return null;
        foreach (var message in conversation.Messages)
        {
            var content = message.Content ?? "";
            var index = content.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var excerptLength = Math.Max(maxLength, search.Length + 24);
            var start = Math.Max(0, index - excerptLength / 3);
            var length = Math.Min(content.Length - start, excerptLength);
            var excerpt = string.Join(' ', content.Substring(start, length).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return (start > 0 ? "…" : "") + excerpt + (start + length < content.Length ? "…" : "");
        }
        return null;
    }
}

using System.IO;

namespace Codev;

public static class ConversationSearch
{
    public static bool Matches(Conversation conversation, string? query)
    {
        var search = query?.Trim();
        if (string.IsNullOrWhiteSpace(search)) return true;
        if ((conversation.Title ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)) return true;
        if (conversation.Messages.Any(message => (message.Content ?? "").Contains(search, StringComparison.OrdinalIgnoreCase))) return true;

        var terms = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return terms.Length > 1 && terms.All(term =>
            (conversation.Title ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
            conversation.Messages.Any(message => (message.Content ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsInScope(Conversation conversation, string? activeWorkspacePath, bool includeAllProjects)
    {
        if (includeAllProjects) return true;
        if (conversation.ProjectPath is null) return activeWorkspacePath is null;
        if (activeWorkspacePath is null) return false;
        try
        {
            return string.Equals(Path.GetFullPath(conversation.ProjectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(activeWorkspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return string.Equals(conversation.ProjectPath, activeWorkspacePath, StringComparison.OrdinalIgnoreCase); }
    }

    public static List<ConversationMessageMatch> FindMessageMatches(Conversation conversation, string? query, int maxExcerptLength = 180)
    {
        var search = query?.Trim();
        if (string.IsNullOrWhiteSpace(search)) return [];
        var terms = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matches = new List<ConversationMessageMatch>();
        for (var index = 0; index < conversation.Messages.Count; index++)
        {
            var message = conversation.Messages[index];
            var content = message.Content ?? "";
            if (!content.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                (terms.Length < 2 || !terms.All(term => content.Contains(term, StringComparison.OrdinalIgnoreCase)))) continue;
            var excerpt = FindMessageExcerpt(new Conversation { Messages = [message] }, search, maxExcerptLength) ?? content;
            matches.Add(new ConversationMessageMatch(index, message.Role, excerpt));
        }
        return matches;
    }

    public static string? FindMessageExcerpt(Conversation conversation, string? query, int maxLength = 120)
    {
        var search = query?.Trim();
        if (string.IsNullOrWhiteSpace(search) || maxLength < 12) return null;
        var excerpts = new[] { search }.Concat(search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(term => !string.Equals(term, search, StringComparison.OrdinalIgnoreCase)));
        foreach (var term in excerpts)
        {
            foreach (var message in conversation.Messages)
            {
                var content = message.Content ?? "";
                var index = content.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;
                var excerptLength = Math.Max(maxLength, term.Length + 24);
                var start = Math.Max(0, index - excerptLength / 3);
                var length = Math.Min(content.Length - start, excerptLength);
                var excerpt = string.Join(' ', content.Substring(start, length).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                return (start > 0 ? "…" : "") + excerpt + (start + length < content.Length ? "…" : "");
            }
        }
        return null;
    }
}

public sealed record ConversationMessageMatch(int MessageIndex, string Role, string Excerpt)
{
    public string DisplayText => $"{(Role == "user" ? "You" : "Codev")} · {Excerpt}";
}

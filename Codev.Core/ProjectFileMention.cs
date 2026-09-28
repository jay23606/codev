namespace Codev;

public sealed record ProjectFileMention(int StartIndex, int EndIndex, string Prefix);

/// <summary>Parses an @file token at a text cursor and inserts a selected project-relative path.</summary>
public static class ProjectFileMentionParser
{
    public static bool TryGet(string text, int caretIndex, out ProjectFileMention mention)
    {
        ArgumentNullException.ThrowIfNull(text);
        caretIndex = Math.Clamp(caretIndex, 0, text.Length);
        if (caretIndex == 0)
        {
            mention = default!;
            return false;
        }
        var start = text.LastIndexOf('@', caretIndex - 1, caretIndex);
        if (start < 0)
        {
            mention = default!;
            return false;
        }

        if (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '@'))
        {
            mention = default!;
            return false;
        }
        var prefix = text[(start + 1)..caretIndex];
        if (prefix.Any(char.IsWhiteSpace))
        {
            mention = default!;
            return false;
        }

        var end = caretIndex;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        mention = new ProjectFileMention(start, end, prefix);
        return true;
    }

    public static (string Text, int CaretIndex) Insert(string text, ProjectFileMention mention, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(mention);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (mention.StartIndex < 0 || mention.EndIndex < mention.StartIndex || mention.EndIndex > text.Length)
            throw new ArgumentOutOfRangeException(nameof(mention));
        var updated = string.Concat(text.AsSpan(0, mention.StartIndex), relativePath, text.AsSpan(mention.EndIndex));
        return (updated, mention.StartIndex + relativePath.Length);
    }
}

public static class ProjectFileMentionSuggestions
{
    public static IReadOnlyList<string> Find(WorkspaceFileService service, string prefix, int maxResults = 12)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(prefix);
        if (maxResults <= 0) return [];
        var normalizedPrefix = prefix.Replace('\\', '/');
        return service.ListContextFiles(maxEntries: 500)
            .Where(path => path.Replace('\\', '/').StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults).ToArray();
    }
}

namespace Codev;

/// <summary>Recognizes interruption markers and preserves partial assistant text and metadata.</summary>
public static class InterruptedResponse
{
    public const string StopMarker = "[Generation stopped.]";
    public const string ClosedMarker = "[Generation interrupted when Codev closed.]";

    public static ChatMessage MarkClosed(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message with { Content = ClosedMarker };
    }

    public static bool TryGetPartial(string? content, out string partial)
    {
        partial = "";
        if (content is null) return false;
        var end = content.TrimEnd();
        if (end.Equals("Generation stopped.", StringComparison.OrdinalIgnoreCase)) return true;
        var suffix = "\n\n" + StopMarker;
        if (!end.EndsWith(suffix, StringComparison.Ordinal)) return false;
        partial = end[..^suffix.Length].TrimEnd();
        return true;
    }
}

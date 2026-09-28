namespace Codev;

/// <summary>Recognizes and removes Codev's explicit stop marker while preserving partial assistant text.</summary>
public static class InterruptedResponse
{
    public const string StopMarker = "[Generation stopped.]";

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

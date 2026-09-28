namespace Codev;

/// <summary>Matches a saved model name to Ollama's installed tag, including an optional :latest suffix.</summary>
public static class OllamaModelSelection
{
    public static string? ResolveInstalledTag(string? preferred, IEnumerable<string> installedTags)
    {
        ArgumentNullException.ThrowIfNull(installedTags);
        var tags = installedTags.Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tags.Length == 0) return null;
        if (string.IsNullOrWhiteSpace(preferred)) return tags[0];

        return tags.FirstOrDefault(tag => Normalize(tag).Equals(Normalize(preferred), StringComparison.OrdinalIgnoreCase))
            ?? tags[0];
    }

    private static string Normalize(string tag) => tag.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
        ? tag[..^7]
        : tag;
}

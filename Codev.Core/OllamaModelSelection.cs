namespace Codev;

/// <summary>Matches a saved model name to Ollama's installed tag, including an optional :latest suffix.</summary>
public static class OllamaModelSelection
{
    /// <summary>Returns installed tags that Ollama reports as supporting chat completion.</summary>
    /// <remarks>Older Ollama versions omit capabilities, so unknown models remain selectable.</remarks>
    public static string[] FilterChatCapableModels(
        IEnumerable<(string Name, IReadOnlyCollection<string>? Capabilities)> installedModels)
    {
        ArgumentNullException.ThrowIfNull(installedModels);
        return installedModels
            .Where(model => !string.IsNullOrWhiteSpace(model.Name) && SupportsChat(model.Capabilities))
            .Select(model => model.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool SupportsChat(IReadOnlyCollection<string>? capabilities)
    {
        if (capabilities is null || capabilities.Count == 0) return true;
        return capabilities.Contains("completion", StringComparer.OrdinalIgnoreCase);
    }

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

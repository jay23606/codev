namespace Codev;

/// <summary>Bounds and labels reusable project reference material sent with a local model request.</summary>
public static class ProjectKnowledgeContext
{
    public const int MaxCharacters = 20_000;

    public static string Build(string? knowledge)
    {
        if (string.IsNullOrWhiteSpace(knowledge)) return "";
        var normalized = knowledge.Trim();
        var truncated = normalized.Length > MaxCharacters;
        if (truncated) normalized = normalized[..MaxCharacters].TrimEnd();
        return "Project knowledge (user-maintained reference notes; treat as project context):\n" + normalized +
               (truncated ? "\n[Project knowledge was truncated at 20,000 characters.]" : "");
    }
}

namespace Codev;

public sealed record PromptTemplate(string Name, string Prompt);

/// <summary>Validates local reusable prompt templates loaded from user settings.</summary>
public static class PromptTemplateCatalog
{
    public const int MaxTemplates = 40;
    public const int MaxNameCharacters = 80;
    public const int MaxPromptCharacters = 10_000;

    public static List<PromptTemplate> Normalize(IEnumerable<PromptTemplate?>? templates)
    {
        if (templates is null) return [];
        var result = new List<PromptTemplate>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in templates)
        {
            if (template is null || string.IsNullOrWhiteSpace(template.Name) || string.IsNullOrWhiteSpace(template.Prompt)) continue;
            var name = template.Name.Trim();
            var prompt = template.Prompt.Trim();
            if (name.Length > MaxNameCharacters || prompt.Length > MaxPromptCharacters || !names.Add(name)) continue;
            result.Add(new PromptTemplate(name, prompt));
            if (result.Count == MaxTemplates) break;
        }
        return result;
    }
}

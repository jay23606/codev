using System.Text.Json;

namespace Codev;

public sealed record PromptTemplate(string Name, string Prompt);

/// <summary>Validates local reusable prompt templates loaded from user settings.</summary>
public static class PromptTemplateCatalog
{
    public const int MaxTemplates = 40;
    public const int MaxNameCharacters = 80;
    public const int MaxPromptCharacters = 10_000;

    public static List<PromptTemplate> DeserializeLegacySettings(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!property.Name.Equals("PromptTemplates", StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind != JsonValueKind.Array) return [];
            var templates = property.Value.Deserialize<List<PromptTemplate>>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return Normalize(templates);
        }
        return [];
    }

    /// <summary>Expose existing settings-backed templates in the slash menu while users migrate to Markdown commands.</summary>
    public static IReadOnlyList<SlashCommandDefinition> ToSlashCommands(IEnumerable<PromptTemplate?>? templates,
        IEnumerable<string>? reservedNames = null)
    {
        var result = new List<SlashCommandDefinition>();
        var names = SlashCommandCatalog.All.Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (reservedNames is not null) names.UnionWith(reservedNames);
        foreach (var template in Normalize(templates))
        {
            var slug = new string(template.Name.ToLowerInvariant()
                .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
                .ToArray());
            slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
            if (slug.Length > 31) slug = slug[..31].TrimEnd('-');
            if (slug.Length == 0) slug = "saved";
            var baseName = "/template-" + slug;
            var name = baseName;
            var suffix = 2;
            while (!names.Add(name))
            {
                var suffixText = "-" + suffix++;
                var prefix = baseName[..Math.Min(baseName.Length, 40 - suffixText.Length)];
                name = prefix + suffixText;
            }
            result.Add(new SlashCommandDefinition(name, $"Saved prompt template: {template.Name}",
                SlashCommandAction.UserPrompt, template.Prompt, [], "template"));
        }
        return result;
    }

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

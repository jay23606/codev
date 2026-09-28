namespace Codev.Tests;

public sealed class PromptTemplateCatalogTests
{
    [Fact]
    public void Normalizes_whitespace_and_drops_empty_duplicate_or_oversized_templates()
    {
        var templates = new PromptTemplate?[]
        {
            new("  Review  ", "  Check correctness and tests.  "),
            new("review", "duplicate name"),
            new("   ", "prompt"),
            new("missing prompt", " "),
            new(new string('n', PromptTemplateCatalog.MaxNameCharacters + 1), "prompt"),
            new("long prompt", new string('p', PromptTemplateCatalog.MaxPromptCharacters + 1))
        };

        Assert.Equal([new PromptTemplate("Review", "Check correctness and tests.")], PromptTemplateCatalog.Normalize(templates));
    }

    [Fact]
    public void Catalog_is_bounded_to_forty_entries()
    {
        var templates = Enumerable.Range(0, 60).Select(index => new PromptTemplate($"Template {index}", $"Prompt {index}"));

        Assert.Equal(PromptTemplateCatalog.MaxTemplates, PromptTemplateCatalog.Normalize(templates).Count);
    }

    [Fact]
    public void Missing_catalog_restores_as_empty_for_older_settings_files() =>
        Assert.Empty(PromptTemplateCatalog.Normalize(null));

    [Fact]
    public void Imports_prompt_templates_from_case_insensitive_legacy_settings_json()
    {
        var imported = PromptTemplateCatalog.DeserializeLegacySettings("""{"theme":"light","prompttemplates":[{"name":"Review","prompt":"Review this."},{"name":"","prompt":"ignored"}]}""");

        Assert.Equal([new PromptTemplate("Review", "Review this.")], imported);
    }

    [Fact]
    public void Maps_saved_templates_to_unique_slash_commands_for_migration()
    {
        var commands = PromptTemplateCatalog.ToSlashCommands(
        [
            new PromptTemplate("Review", "Review this carefully."),
            new PromptTemplate("Review!", "Look for edge cases."),
            new PromptTemplate("!!!", "Use the saved fallback prompt."),
            new PromptTemplate("A very long template name that needs truncation", "Keep it bounded.")
        ]);

        Assert.Equal(4, commands.Count);
        Assert.Equal("/template-review", commands[0].Name);
        Assert.Equal("/template-review-2", commands[1].Name);
        Assert.Equal("/template-saved", commands[2].Name);
        var expansion = CustomSlashCommandService.Expand(commands[0], commands[0].Name);
        Assert.True(expansion.Success, expansion.Error);
        Assert.Equal("Review this carefully.", expansion.Prompt);
        Assert.All(commands, command =>
        {
            Assert.True(command.IsCustom);
            Assert.Equal("template", command.Scope);
            Assert.True(command.Name.Length <= 40);
        });
        var reserved = PromptTemplateCatalog.ToSlashCommands([new PromptTemplate("Review", "Prompt")], ["/template-review"]);
        Assert.Equal("/template-review-2", Assert.Single(reserved).Name);
    }
}

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
}

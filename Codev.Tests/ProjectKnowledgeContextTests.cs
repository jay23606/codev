namespace Codev.Tests;

public sealed class ProjectKnowledgeContextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public void Empty_knowledge_adds_no_context(string? knowledge) =>
        Assert.Equal("", ProjectKnowledgeContext.Build(knowledge));

    [Fact]
    public void Labels_and_trims_saved_reference_notes()
    {
        var context = ProjectKnowledgeContext.Build("\nUses PostgreSQL and the repository pattern.\n");

        Assert.Contains("Project knowledge", context);
        Assert.EndsWith("Uses PostgreSQL and the repository pattern.", context);
    }

    [Fact]
    public void Truncates_oversized_knowledge_with_an_explicit_notice()
    {
        var context = ProjectKnowledgeContext.Build(new string('x', ProjectKnowledgeContext.MaxCharacters + 50));

        Assert.Contains("[Project knowledge was truncated at 20,000 characters.]", context);
        Assert.Equal(ProjectKnowledgeContext.MaxCharacters + "Project knowledge (user-maintained reference notes; treat as project context):\n".Length + "\n[Project knowledge was truncated at 20,000 characters.]".Length, context.Length);
    }
}

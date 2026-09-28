namespace Codev.Tests;

public sealed class ProjectPathInstructionRuleTests
{
    [Fact]
    public void Finds_unique_explicit_rule_mentions()
    {
        Assert.Equal(["javascript", "review"], ProjectPathInstructionRuleParser.FindManualMentions(
            "Please check @rule:javascript and @rule:review, then reapply @rule:javascript."));
        Assert.Empty(ProjectPathInstructionRuleParser.FindManualMentions("email@rule:javascript"));
    }

    [Fact]
    public async Task Lists_only_valid_rule_definitions_for_manual_mention_suggestions()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-rule-suggestions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".codev", "rules"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".codev", "rules", "javascript.md"),
                "---\ndescription: JavaScript\nglobs: **/*.js\n---\nUse semicolons.");
            await File.WriteAllTextAsync(Path.Combine(root, ".codev", "rules", "broken.md"), "not frontmatter");
            await File.WriteAllTextAsync(Path.Combine(root, ".codev", "rules", "oversized.md"), new string('x', ProjectPathInstructionRuleParser.MaxDefinitionBytes + 1));

            var suggestions = ProjectPathInstructionRuleParser.FindMentionSuggestions(new WorkspaceFileService(root), "rule:ja");
            var excluded = ProjectPathInstructionRuleParser.FindMentionSuggestions(new WorkspaceFileService(root, [".codev/rules"]), "rule:");

            Assert.Equal(["rule:javascript"], suggestions);
            Assert.Empty(excluded);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("**/*.ts", "src/ui/app.ts", true)]
    [InlineData("**/*.ts", "app.ts", true)]
    [InlineData("*.cs", "src/App.cs", true)]
    [InlineData("src/**/*.cs", "src/App.cs", true)]
    [InlineData("src/**/*.cs", "src/ui/App.cs", true)]
    [InlineData("src/*.cs", "src/ui/App.cs", false)]
    [InlineData("*.ts", "src/app.tsx", false)]
    public void Matches_file_globs(string glob, string path, bool expected)
    {
        Assert.True(ProjectPathInstructionRuleParser.TryParse("rule.md",
            $"---\ndescription: Rule\nglobs: {glob}\n---\nUse this rule.", out var rule));

        Assert.Equal(expected, ProjectPathInstructionRuleParser.AppliesTo(rule!, path));
    }

    [Theory]
    [InlineData("../*.cs")]
    [InlineData("src/../../*.cs")]
    [InlineData("/etc/*.cs")]
    [InlineData("\\\\server\\share\\*.cs")]
    [InlineData("C:\\src\\*.cs")]
    public void Rejects_globs_that_escape_or_root_outside_project(string glob)
    {
        Assert.False(ProjectPathInstructionRuleParser.TryParse("rule.md",
            $"---\ndescription: Rule\nglobs: {glob}\n---\nUse this rule.", out _));
    }

    [Fact]
    public void Rejects_unknown_frontmatter_and_missing_pattern()
    {
        Assert.False(ProjectPathInstructionRuleParser.TryParse("rule.md",
            "---\ndescription: Rule\nglobs: *.cs\nmode: always\n---\nUse this rule.", out _));
        Assert.False(ProjectPathInstructionRuleParser.TryParse("rule.md",
            "---\ndescription: Rule\n---\nUse this rule.", out _));
    }
}

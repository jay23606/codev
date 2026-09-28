namespace Codev.Tests;

public sealed class ProjectPathInstructionRuleTests
{
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

namespace Codev.Tests;

public sealed class GitDiffPromptBuilderTests
{
    [Fact]
    public void Appends_a_diff_question_without_replacing_existing_composer_text()
    {
        var prompt = GitDiffPromptBuilder.AppendSelection("Please review this change.", "src/`odd`.cs", "- old\n+ new");

        Assert.StartsWith("Please review this change.\n\nExplain this selected Git diff for `src/\\`odd\\`.cs`", prompt, StringComparison.Ordinal);
        Assert.Contains("```diff\n- old\n+ new\n```", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Uses_a_longer_fence_when_the_selected_diff_contains_backticks()
    {
        var prompt = GitDiffPromptBuilder.AppendSelection("", "file.cs", "```\ninside\n```");

        Assert.Contains("````diff\n```\ninside\n```\n````", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Requires_a_nonempty_selection()
    {
        Assert.Throws<ArgumentException>(() => GitDiffPromptBuilder.AppendSelection("prompt", "file.cs", " \n "));
    }
}

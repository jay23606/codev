namespace Codev.Tests;

public sealed class GitDiffPromptBuilderTests
{
    [Fact]
    public void Appends_comments_with_selected_file_and_diff_to_unsent_prompt()
    {
        var comment = new GitDiffComment("src/app.cs", "- old\n+ new", "Please preserve the existing behavior.");
        var prompt = GitDiffPromptBuilder.AppendComments("Review this.", [comment]);
        Assert.Contains("Review this.", prompt);
        Assert.Contains("Diff comment 1 · `src/app.cs`", prompt);
        Assert.Contains("- old\n+ new", prompt);
        Assert.Contains("Comment: Please preserve the existing behavior.", prompt);
    }

    [Fact]
    public void Comment_builder_uses_longer_fence_when_selected_diff_contains_backticks()
    {
        var prompt = GitDiffPromptBuilder.AppendComments("", [new("f.cs", "```\ncode\n```", "review")]);
        Assert.Contains("````diff", prompt);
    }

    [Fact]
    public void Comment_builder_rejects_oversized_or_incomplete_comments()
    {
        Assert.Throws<ArgumentException>(() => GitDiffPromptBuilder.AppendComments("", [new("f.cs", "diff", " ")]));
        Assert.Throws<ArgumentException>(() => GitDiffPromptBuilder.AppendComments("", [new("f.cs", new string('x', GitDiffPromptBuilder.MaxDiffLength + 1), "review")]));
    }

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

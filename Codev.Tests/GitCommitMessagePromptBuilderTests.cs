namespace Codev.Tests;

public sealed class GitCommitMessagePromptBuilderTests
{
    [Fact]
    public void Build_requests_an_editable_conventional_message_and_marks_inputs_untrusted()
    {
        var messages = GitCommitMessagePromptBuilder.Build("+add feature", ["fix: prior issue"]);

        Assert.Contains("at most 72 characters", messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("untrusted data", messages[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fix: prior issue", messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("+add feature", messages[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_rejects_an_oversized_diff()
    {
        Assert.Throws<ArgumentException>(() => GitCommitMessagePromptBuilder.Build(new string('x', GitCommitMessagePromptBuilder.MaxDiffCharacters + 1)));
    }

    [Fact]
    public void Build_caps_style_examples_to_five_and_sanitizes_multiline_subjects()
    {
        var messages = GitCommitMessagePromptBuilder.Build("diff", ["one\ntwo", "three", "four", "five", "six", "seven"]);

        Assert.Contains("- one two", messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("- five", messages[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("- seven", messages[1].Content, StringComparison.Ordinal);
    }
}

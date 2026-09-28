namespace Codev.Tests;

public sealed class GitReviewPromptBuilderTests
{
    [Fact]
    public void Build_marks_changes_as_untrusted_and_requests_prioritized_findings()
    {
        var messages = GitReviewPromptBuilder.Build(new GitWorkingTreeReview("main", ["src/a.cs"], "- old\n+ new", false));

        Assert.Equal("system", messages[0].Role);
        Assert.Contains("untrusted data", messages[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("severity", messages[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("user", messages[1].Role);
        Assert.Contains("src/a.cs", messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("- old\n+ new", messages[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_reports_when_diff_was_truncated()
    {
        var messages = GitReviewPromptBuilder.Build(new GitWorkingTreeReview("main", ["src/a.cs"], "diff", true));

        Assert.Contains("truncated", messages[1].Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_rejects_an_oversized_diff()
    {
        var review = new GitWorkingTreeReview("main", [], new string('x', GitReviewPromptBuilder.MaxDiffCharacters + 1), false);

        Assert.Throws<ArgumentException>(() => GitReviewPromptBuilder.Build(review));
    }

    [Fact]
    public void Build_security_review_focuses_on_security_and_hides_secret_values()
    {
        var messages = GitReviewPromptBuilder.Build(new GitWorkingTreeReview("main", ["src/a.cs"], "+token = 'example'", false), securityFocused: true);

        Assert.Contains("security review", messages[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not repeat secret values", messages[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("untrusted data", messages[0].Content, StringComparison.OrdinalIgnoreCase);
    }
}

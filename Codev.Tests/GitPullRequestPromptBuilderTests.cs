namespace Codev.Tests;

public sealed class GitPullRequestPromptBuilderTests
{
    [Fact]
    public void Build_requests_title_summary_and_honest_testing_section()
    {
        var messages = GitPullRequestPromptBuilder.Build(new GitWorkingTreeReview("branch main...HEAD", ["src/a.cs"], "+new change", false));

        Assert.Contains("title and description", messages[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Testing section", messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("untrusted data", messages[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/a.cs", messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("+new change", messages[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_rejects_an_oversized_diff()
    {
        var review = new GitWorkingTreeReview("main...HEAD", [], new string('x', GitPullRequestPromptBuilder.MaxDiffCharacters + 1), false);

        Assert.Throws<ArgumentException>(() => GitPullRequestPromptBuilder.Build(review));
    }
}

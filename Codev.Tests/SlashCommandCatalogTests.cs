namespace Codev.Tests;

public sealed class SlashCommandCatalogTests
{
    [Fact]
    public void Slash_prefix_returns_implemented_commands_with_descriptions()
    {
        var suggestions = SlashCommandCatalog.Suggest("/");

        Assert.Equal(SlashCommandCatalog.All.Count, suggestions.Count);
        Assert.Contains(suggestions, command => command.Name == "/status" && command.Action == SlashCommandAction.ShowStatus);
        Assert.All(suggestions, command => Assert.False(string.IsNullOrWhiteSpace(command.Description)));
    }

    [Theory]
    [InlineData("/pl", "/plan")]
    [InlineData("/STATUS", "/status")]
    [InlineData("/exp", "/export")]
    public void Suggestions_match_command_prefix_case_insensitively(string draft, string expected)
    {
        var suggestion = Assert.Single(SlashCommandCatalog.Suggest(draft));
        Assert.Equal(expected, suggestion.Name);
    }

    [Theory]
    [InlineData("hello /pl", 9)]
    [InlineData("/plan something", 14)]
    [InlineData("/pl", 1)]
    public void Suggestions_only_appear_for_a_slash_command_at_the_composer_caret(string draft, int caret)
    {
        Assert.Empty(SlashCommandCatalog.Suggest(draft, caret));
    }

    [Fact]
    public void Exact_command_check_does_not_match_arguments_or_partial_names()
    {
        var status = SlashCommandCatalog.All.Single(command => command.Name == "/status");

        Assert.True(SlashCommandCatalog.IsExactCommand(" /STATUS ", status));
        Assert.False(SlashCommandCatalog.IsExactCommand("/status now", status));
        Assert.False(SlashCommandCatalog.IsExactCommand("/stat", status));
    }

    [Fact]
    public void Review_and_init_commands_prepare_prompts_without_hidden_execution()
    {
        Assert.All(SlashCommandCatalog.All.Where(command => command.Action is SlashCommandAction.ReviewProject or SlashCommandAction.InitProject),
            command => Assert.False(string.IsNullOrWhiteSpace(command.Prompt)));
        var review = Assert.Single(SlashCommandCatalog.All, command => command.Name == "/review");
        Assert.Equal(SlashCommandAction.ReviewWorkingTree, review.Action);
        var securityReview = Assert.Single(SlashCommandCatalog.All, command => command.Name == "/security-review");
        Assert.Equal(SlashCommandAction.SecurityReviewWorkingTree, securityReview.Action);
        Assert.Equal(SlashCommandAction.ReviewCommit,
            Assert.Single(SlashCommandCatalog.All, command => command.Name == "/review-commit").Action);
        Assert.Equal(SlashCommandAction.SecurityReviewCommit,
            Assert.Single(SlashCommandCatalog.All, command => command.Name == "/security-review-commit").Action);
        Assert.Contains("local model", review.Description, StringComparison.OrdinalIgnoreCase);
    }
}

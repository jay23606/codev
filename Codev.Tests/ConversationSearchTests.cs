namespace Codev.Tests;

public sealed class ConversationSearchTests
{
    [Fact]
    public void Search_matches_titles_and_message_content_without_case_sensitivity()
    {
        var conversation = new Conversation
        {
            Title = "Feature planning",
            Messages = [new("user", "Can you explain the async queue?"), new("assistant", "It processes requests serially.")]
        };

        Assert.True(ConversationSearch.Matches(conversation, "FEATURE"));
        Assert.True(ConversationSearch.Matches(conversation, "ASYNC QUEUE"));
        Assert.False(ConversationSearch.Matches(conversation, "not present"));
        Assert.True(ConversationSearch.Matches(conversation, "  "));
    }

    [Fact]
    public void Search_excerpt_includes_context_around_the_first_message_match()
    {
        var conversation = new Conversation
        {
            Messages = [new("user", "Before the requested phrase, then important surrounding context, and finally after the phrase appears.")]
        };

        var excerpt = ConversationSearch.FindMessageExcerpt(conversation, "important surrounding");

        Assert.NotNull(excerpt);
        Assert.Contains("important surrounding", excerpt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Before", excerpt, StringComparison.Ordinal);
    }
}

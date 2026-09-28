namespace Codev.Tests;

public sealed class ConversationCompactionServiceTests
{
    private static List<ChatMessage> CreateTurns(int count) => Enumerable.Range(1, count)
        .SelectMany(index => new[] { new ChatMessage("user", $"Question {index}"), new ChatMessage("assistant", $"Answer {index}") })
        .ToList();

    [Fact]
    public void Boundary_keeps_the_four_most_recent_complete_turns()
    {
        var messages = CreateTurns(6);

        var boundary = ConversationCompactionService.FindBoundary(messages);

        Assert.Equal(4, boundary);
        Assert.True(ConversationCompactionService.IsValidBoundary(messages, boundary));
        Assert.Equal(0, ConversationCompactionService.FindBoundary(messages, alreadyCompactedThrough: boundary));
    }

    [Fact]
    public void Applying_summary_preserves_transcript_and_future_prompt_uses_summary_plus_tail()
    {
        var originalMessages = CreateTurns(6);
        var conversation = new Conversation { Messages = [.. originalMessages] };
        var proposal = new ConversationCompactionProposal(conversation.Id, 4, "The user is building a widget; two early decisions were made.", 2, 4);

        Assert.True(ConversationCompactionService.Apply(conversation, proposal));
        var promptHistory = ConversationCompactionService.BuildPromptHistory(conversation, conversation.Messages);

        Assert.Equal(originalMessages, conversation.Messages);
        Assert.Equal(4, conversation.CompactionThroughMessageCount);
        Assert.Contains("historical context only", promptHistory[0].Content);
        Assert.Contains(proposal.Summary, promptHistory[0].Content);
        Assert.Equal(originalMessages.Skip(4), promptHistory.Skip(1));
    }

    [Fact]
    public void Next_summary_uses_previous_summary_and_only_newly_compacted_messages()
    {
        var conversation = new Conversation
        {
            Messages = CreateTurns(7),
            CompactionSummary = "Accepted summary of the first two turns.",
            CompactionThroughMessageCount = 4
        };

        var boundary = ConversationCompactionService.FindBoundary(conversation.Messages, conversation.CompactionThroughMessageCount);
        var input = ConversationCompactionService.BuildSummaryMessages(conversation, boundary);

        Assert.Equal(6, boundary);
        Assert.Contains("Accepted summary of the first two turns.", input[1].Content);
        Assert.Contains("Question 3", input[1].Content);
        Assert.DoesNotContain("Question 1", input[1].Content);
        Assert.DoesNotContain("Question 7", input[1].Content);
    }

    [Fact]
    public void Selected_message_boundary_can_compact_a_prefix_shorter_than_the_default_four_turn_tail()
    {
        var conversation = new Conversation { Messages = CreateTurns(6) };

        var input = ConversationCompactionService.BuildSummaryMessages(conversation, throughMessageCount: 2);

        Assert.Contains("Question 1", input[1].Content);
        Assert.Contains("Answer 1", input[1].Content);
        Assert.DoesNotContain("Question 2", input[1].Content);
        var compacted = new Conversation { Messages = conversation.Messages, CompactionSummary = "Summary", CompactionThroughMessageCount = 2 };
        Assert.Equal(11, ConversationCompactionService.BuildPromptHistory(compacted, conversation.Messages).Count);
    }

    [Theory]
    [InlineData(79, 100, false)]
    [InlineData(80, 100, true)]
    [InlineData(90, 100, true)]
    [InlineData(80, 0, false)]
    [InlineData(0, 100, false)]
    public void Context_limit_offer_requires_known_limit_and_at_least_eighty_percent(int promptTokens, int contextLimit, bool expected) =>
        Assert.Equal(expected, ConversationCompactionService.ShouldOfferCompaction(promptTokens, contextLimit));

    [Fact]
    public void Apply_rejects_pending_requests_invalid_cutoffs_and_oversized_summaries()
    {
        var conversation = new Conversation { Messages = CreateTurns(6) };
        var valid = new ConversationCompactionProposal(conversation.Id, 4, "Summary", 2, 4);
        var oversized = valid with { Summary = new string('x', ConversationCompactionService.MaxSummaryCharacters + 1) };

        Assert.False(ConversationCompactionService.Apply(conversation, valid with { ThroughMessageCount = 2 }, isGenerating: true));
        conversation.PendingRequestCount = 1;
        Assert.False(ConversationCompactionService.Apply(conversation, valid));
        conversation.PendingRequestCount = 0;
        Assert.False(ConversationCompactionService.Apply(conversation, oversized));
        Assert.False(ConversationCompactionService.Apply(conversation, valid with { ThroughMessageCount = 3 }));
        Assert.Equal("", conversation.CompactionSummary);
    }
}

using Codev;

namespace Codev.Tests;

public sealed class ConversationRewindServiceTests
{
    [Fact]
    public void Rewind_removes_the_selected_turn_and_later_messages_and_restores_its_prompt_as_draft()
    {
        var conversation = new Conversation
        {
            Messages = [new("user", "first"), new("assistant", "answer 1"), new("user", "second"), new("assistant", "answer 2")],
            FileChanges = [new("src/app.cs", null, DateTimeOffset.UnixEpoch, "Create", PreviousFileExisted: false)],
            LastPromptTokens = 500,
            LastPromptOutputTokens = 90,
            LastPromptContext = 4096,
            LastPromptModel = "local-model"
        };

        var draft = ConversationRewindService.RestoreConversationOnly(conversation, 2);

        Assert.Equal("second", draft);
        Assert.Equal("second", conversation.Draft);
        Assert.Equal([new ChatMessage("user", "first") { MessageIndex = 0 }, new ChatMessage("assistant", "answer 1") { MessageIndex = 1 }], conversation.Messages);
        Assert.Equal(0, conversation.Messages[0].MessageIndex);
        Assert.Equal(1, conversation.Messages[1].MessageIndex);
        Assert.Single(conversation.FileChanges);
        Assert.Equal(0, conversation.LastPromptTokens);
        Assert.Null(conversation.LastPromptOutputTokens);
        Assert.Equal(0, conversation.LastPromptContext);
        Assert.Empty(conversation.LastPromptModel);
    }

    [Fact]
    public void Rewind_rejects_non_user_targets_and_conversations_with_queued_turns()
    {
        var conversation = new Conversation { Messages = [new("user", "prompt"), new("assistant", "answer")] };
        Assert.Throws<ArgumentOutOfRangeException>(() => ConversationRewindService.RestoreConversationOnly(conversation, 1));
        conversation.PendingRequestCount = 1;
        Assert.Throws<InvalidOperationException>(() => ConversationRewindService.RestoreConversationOnly(conversation, 0));
        Assert.Equal(2, conversation.Messages.Count);
    }

    [Fact]
    public void Rewind_before_compacted_boundary_restores_uncompacted_history_mode()
    {
        var conversation = new Conversation
        {
            Messages = [new("user", "first"), new("assistant", "answer 1"), new("user", "second"), new("assistant", "answer 2"), new("user", "third"), new("assistant", "answer 3")],
            CompactionSummary = "Summary of first two exchanges.",
            CompactionFromMessageCount = 2,
            CompactionThroughMessageCount = 4
        };

        ConversationRewindService.RestoreConversationOnly(conversation, 2);

        Assert.Empty(conversation.CompactionSummary);
        Assert.Equal(0, conversation.CompactionFromMessageCount);
        Assert.Equal(0, conversation.CompactionThroughMessageCount);
    }

    [Fact]
    public void Rewind_after_a_compacted_range_preserves_the_summary_and_its_boundaries()
    {
        var messages = Enumerable.Range(1, 7).SelectMany(index => new[]
            { new ChatMessage("user", $"Question {index}"), new ChatMessage("assistant", $"Answer {index}") }).ToList();
        var conversation = new Conversation
        {
            Messages = messages,
            CompactionSummary = "Middle turns summarized.",
            CompactionFromMessageCount = 4,
            CompactionThroughMessageCount = 8
        };

        ConversationRewindService.RestoreConversationOnly(conversation, userMessageIndex: 10);

        Assert.Equal("Middle turns summarized.", conversation.CompactionSummary);
        Assert.Equal(4, conversation.CompactionFromMessageCount);
        Assert.Equal(8, conversation.CompactionThroughMessageCount);
        Assert.Equal(7, ConversationCompactionService.BuildPromptHistory(conversation, conversation.Messages).Count);
    }
}

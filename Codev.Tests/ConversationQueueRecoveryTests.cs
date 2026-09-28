namespace Codev.Tests;

public sealed class ConversationQueueRecoveryTests
{
    [Fact]
    public void Restores_only_valid_unstarted_turns_in_their_original_global_order()
    {
        var early = new Conversation { Messages = [new("user", "first"), new("assistant", "Queued request was not sent before Codev closed.")] };
        var late = new Conversation { Messages = [new("user", "second"), new("assistant", "")] };
        var invalid = new Conversation { Messages = [new("user", "not an assistant placeholder")] };
        var earlyTurn = new PersistedQueuedTurn(1, "model-a", 8192, true, false, @"C:\project", ["a.cs"], ["obj"], DateTimeOffset.UnixEpoch, 0.2, ThinkEnabled: true);
        var lateTurn = new PersistedQueuedTurn(1, "model-b", 16384, false, true, null, [], [], DateTimeOffset.UnixEpoch.AddMinutes(1), Provider: CloudModelProviders.OpenAI, IncludeProjectContext: true, IncludeRepoMap: true);
        early.PendingTurns = [earlyTurn];
        late.PendingTurns = [lateTurn];
        invalid.PendingTurns = [new PersistedQueuedTurn(0, "model-c", 0, false, false, null, [], [], DateTimeOffset.UnixEpoch)];

        var restored = ConversationQueueRecovery.Restore([late, invalid, early]);

        Assert.Equal([early, late], restored.Select(item => item.Conversation));
        Assert.Equal(earlyTurn, restored[0].Turn);
        Assert.True(restored[0].Turn.IsCodeTask);
        Assert.Equal(0.2, restored[0].Turn.Temperature);
        Assert.True(restored[0].Turn.ThinkEnabled);
        Assert.False(restored[1].Turn.ThinkEnabled);
        Assert.Equal(["a.cs"], restored[0].Turn.ContextFiles);
        Assert.Equal(CloudModelProviders.OpenAI, restored[1].Turn.Provider);
        Assert.True(restored[1].Turn.IsPlanMode);
        Assert.True(restored[1].Turn.IncludeProjectContext);
        Assert.True(restored[1].Turn.IncludeRepoMap);
        Assert.Single(early.PendingTurns);
        Assert.Empty(invalid.PendingTurns);
    }

    [Fact]
    public void Drops_duplicate_slots_and_turns_without_a_model()
    {
        var conversation = new Conversation { Messages = [new("user", "one"), new("assistant", "")] };
        var first = new PersistedQueuedTurn(1, "model-a", 8192, false, false, null, [], [], DateTimeOffset.UnixEpoch);
        conversation.PendingTurns =
        [
            new PersistedQueuedTurn(1, "", 8192, false, false, null, [], [], DateTimeOffset.UnixEpoch.AddMinutes(-1)),
            first,
            new PersistedQueuedTurn(1, "model-b", 8192, false, false, null, [], [], DateTimeOffset.UnixEpoch.AddMinutes(1))
        ];

        var restored = Assert.Single(ConversationQueueRecovery.Restore([conversation]));

        Assert.Equal(first, restored.Turn);
        Assert.Equal([first], conversation.PendingTurns);
    }

    [Fact]
    public void Ignores_null_message_lists_and_null_queue_entries_from_old_or_damaged_stores()
    {
        var conversation = new Conversation { Messages = null!, PendingTurns = null! };

        var restored = ConversationQueueRecovery.Restore([conversation]);

        Assert.Empty(restored);
        Assert.Empty(conversation.PendingTurns);
    }

    [Fact]
    public void Keeps_queue_slot_valid_after_close_marks_it_as_not_sent()
    {
        var conversation = new Conversation
        {
            Messages = [new("user", "retry after restart"), new("assistant", "Queued request was not sent before Codev closed.")],
            PendingTurns = [new(1, "model-a", 8192, true, false, @"C:\work", ["src/a.cs"], ["bin"], DateTimeOffset.UtcNow)]
        };

        var restored = Assert.Single(ConversationQueueRecovery.Restore([conversation]));

        Assert.Equal("model-a", restored.Turn.Model);
        Assert.Equal(1, restored.Turn.AssistantIndex);
        Assert.Equal("Queued request was not sent before Codev closed.", conversation.Messages[1].Content);
    }

    [Fact]
    public void Drops_queue_entries_when_the_message_list_has_null_slots()
    {
        var conversation = new Conversation
        {
            Messages = [null!, new("assistant", "")],
            PendingTurns = [new(0, "model-a", 4096, false, false, null, [], [], DateTimeOffset.UtcNow)]
        };

        var restored = ConversationQueueRecovery.Restore([conversation]);

        Assert.Empty(restored);
        Assert.Empty(conversation.PendingTurns);
    }
}

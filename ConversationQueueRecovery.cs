namespace Codev;

public sealed record RestoredConversationTurn(Conversation Conversation, PersistedQueuedTurn Turn);

public static class ConversationQueueRecovery
{
    public static IReadOnlyList<RestoredConversationTurn> Restore(IEnumerable<Conversation> conversations)
    {
        var restored = new List<RestoredConversationTurn>();
        foreach (var conversation in conversations)
        {
            conversation.PendingTurns ??= [];
            var valid = conversation.PendingTurns
                .Where(turn => turn is not null && turn.AssistantIndex >= 0 &&
                    turn.AssistantIndex < conversation.Messages.Count &&
                    conversation.Messages[turn.AssistantIndex].Role == "assistant" &&
                    !string.IsNullOrWhiteSpace(turn.Model))
                .GroupBy(turn => turn.AssistantIndex)
                .Select(group => group.OrderBy(turn => turn.EnqueuedAt).First())
                .ToList();
            conversation.PendingTurns = valid;
            restored.AddRange(valid.Select(turn => new RestoredConversationTurn(conversation, turn)));
        }
        return restored.OrderBy(item => item.Turn.EnqueuedAt).ToArray();
    }
}

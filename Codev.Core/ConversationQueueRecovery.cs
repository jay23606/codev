namespace Codev;

public sealed record RestoredConversationTurn(Conversation Conversation, PersistedQueuedTurn Turn);

public static class ConversationQueueRecovery
{
    public static IReadOnlyList<RestoredConversationTurn> Restore(IEnumerable<Conversation> conversations)
    {
        var restored = new List<RestoredConversationTurn>();
        foreach (var conversation in conversations)
        {
            if (conversation is null) continue;
            conversation.Messages ??= [];
            conversation.PendingTurns ??= [];
            var valid = conversation.PendingTurns
                .Where(turn => turn is not null && turn.AssistantIndex >= 0 &&
                    turn.AssistantIndex < conversation.Messages.Count &&
                    conversation.Messages[turn.AssistantIndex] is { Role: "assistant" } &&
                    !string.IsNullOrWhiteSpace(turn.Model))
                .GroupBy(turn => turn.AssistantIndex)
                .Select(group => group.OrderBy(turn => turn.EnqueuedAt).First())
                .ToList();
            conversation.PendingTurns = valid;
            restored.AddRange(valid.Select(turn => new RestoredConversationTurn(conversation, turn)));
            if (conversation.DelegatedFromMessageIndex is { } parentMessageIndex &&
                (parentMessageIndex < 0 || parentMessageIndex >= 100_000 ||
                 AgentProfileCatalog.NormalizeReferenceName(conversation.DelegatedAgentName) is null))
            {
                conversation.DelegatedFromMessageIndex = null;
                conversation.DelegatedAgentName = null;
                conversation.DelegatedResultReported = false;
            }
        }
        return restored.OrderBy(item => item.Turn.EnqueuedAt).ToArray();
    }
}

namespace Codev;

/// <summary>Decides whether a prompt may wait behind work that is already running or queued.</summary>
public static class ConversationQueuePolicy
{
    public static bool CanSubmit(bool queueEnabled, bool otherWorkIsPending) => queueEnabled || !otherWorkIsPending;
}

namespace Codev;

/// <summary>Allows an agent to create an isolated child during its own active root turn.</summary>
public static class ChildSessionCreationPolicy
{
    public static bool CanCreateChild(bool conversationIsBusy, bool isCurrentParentTurn) =>
        !conversationIsBusy || isCurrentParentTurn;
}

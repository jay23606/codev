namespace Codev;

/// <summary>Moves top-level conversations within a sidebar section and persists their manual order.</summary>
public static class ConversationSidebarOrdering
{
    public static bool TryMove(IList<Conversation> conversations, Guid draggedId, Guid targetId, bool insertAfter)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        if (draggedId == targetId) return false;

        var sourceIndex = IndexOf(draggedId);
        var targetIndex = IndexOf(targetId);
        if (sourceIndex < 0 || targetIndex < 0) return false;

        var dragged = conversations[sourceIndex];
        conversations.RemoveAt(sourceIndex);
        targetIndex = IndexOf(targetId);
        conversations.Insert(targetIndex + (insertAfter ? 1 : 0), dragged);
        for (var index = 0; index < conversations.Count; index++)
            conversations[index].SidebarOrder = index;
        return true;

        int IndexOf(Guid id)
        {
            for (var index = 0; index < conversations.Count; index++)
                if (conversations[index].Id == id) return index;
            return -1;
        }
    }
}

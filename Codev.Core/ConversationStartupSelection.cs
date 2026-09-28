namespace Codev;

public static class ConversationStartupSelection
{
    public static Conversation? Choose(IEnumerable<Conversation> conversations, Guid? lastActiveId)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        var items = conversations.Where(conversation => conversation is not null).ToArray();
        if (items.Length == 0) return null;

        return items.FirstOrDefault(conversation => !conversation.IsArchived && conversation.Id == lastActiveId)
            ?? items.Where(conversation => !conversation.IsArchived).OrderByDescending(conversation => conversation.UpdatedAt).FirstOrDefault()
            ?? items.OrderByDescending(conversation => conversation.UpdatedAt).First();
    }
}

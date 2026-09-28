namespace Codev;

public enum ConversationMode
{
    Chat,
    Plan,
    CodeTask
}

public static class ConversationModeCycle
{
    public static ConversationMode Next(ConversationMode current, bool canEnterCodeTask) => current switch
    {
        ConversationMode.Chat => ConversationMode.Plan,
        ConversationMode.Plan => canEnterCodeTask ? ConversationMode.CodeTask : ConversationMode.Chat,
        ConversationMode.CodeTask => ConversationMode.Chat,
        _ => throw new ArgumentOutOfRangeException(nameof(current), current, "Unknown conversation mode.")
    };
}

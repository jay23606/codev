namespace Codev.Tests;

public sealed class OllamaConversationHistoryTests
{
    [Fact]
    public void Merges_adjacent_duplicate_user_messages_without_losing_content()
    {
        var result = OllamaConversationHistory.Normalize([
            new("system", "Rules"), new("user", "First"), new("user", "Second"), new("assistant", "Answer")]);

        Assert.Equal([new ChatMessage("system", "Rules"), new ChatMessage("user", "First\n\nSecond"), new ChatMessage("assistant", "Answer")], result);
    }

    [Fact]
    public void Merges_duplicate_assistant_messages_and_keeps_system_messages_first()
    {
        var result = OllamaConversationHistory.Normalize([
            new("user", "Question"), new("assistant", "Part one"), new("assistant", "Part two"), new("system", "Extra")]);

        Assert.Equal([new ChatMessage("system", "Extra"), new ChatMessage("user", "Question"), new ChatMessage("assistant", "Part one\n\nPart two")], result);
    }

    [Fact]
    public void Drops_orphan_assistant_blank_and_non_chat_tool_messages()
    {
        var result = OllamaConversationHistory.Normalize([
            new("assistant", "Orphan"), new("user", " "), new("tool", "Tool result"), new("user", "Question")]);

        Assert.Equal([new ChatMessage("user", "Question")], result);
    }
}

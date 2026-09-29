namespace Codev.Tests;

public sealed class ConversationModeCycleTests
{
    [Theory]
    [InlineData("openai", ConversationMode.CodeTask)]
    [InlineData("anthropic", ConversationMode.Chat)]
    [InlineData("ollama", ConversationMode.Chat)]
    public void Defaults_hosted_OpenAI_conversations_to_code_task(string provider, ConversationMode expected) =>
        Assert.Equal(expected, ConversationModeCycle.Default(provider));

    [Theory]
    [InlineData(ConversationMode.Chat, false, ConversationMode.Plan)]
    [InlineData(ConversationMode.Chat, true, ConversationMode.Plan)]
    [InlineData(ConversationMode.Plan, false, ConversationMode.Chat)]
    [InlineData(ConversationMode.Plan, true, ConversationMode.CodeTask)]
    [InlineData(ConversationMode.CodeTask, false, ConversationMode.Chat)]
    [InlineData(ConversationMode.CodeTask, true, ConversationMode.Chat)]
    public void Cycles_modes_and_never_enters_code_task_without_eligibility(
        ConversationMode current, bool canEnterCodeTask, ConversationMode expected)
    {
        Assert.Equal(expected, ConversationModeCycle.Next(current, canEnterCodeTask));
    }
}

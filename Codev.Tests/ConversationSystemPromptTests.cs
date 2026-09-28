namespace Codev.Tests;

public sealed class ConversationSystemPromptTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Plan_mode_requests_a_read_only_ordered_plan_for_both_local_and_hosted_models(bool isLocal)
    {
        var prompt = ConversationSystemPrompt.Build(isPlanMode: true, isLocal);

        Assert.Contains("read-only Plan mode", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("concise ordered implementation plan", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not edit files, run commands", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Chat_mode_remains_read_only_and_does_not_claim_tools()
    {
        var prompt = ConversationSystemPrompt.Build(isPlanMode: false, isLocal: false);

        Assert.Contains("Ordinary chat is read-only", prompt, StringComparison.Ordinal);
        Assert.Contains("you have no tools", prompt, StringComparison.Ordinal);
    }
}

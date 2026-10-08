namespace Codev.Tests;

public sealed class ConversationQueuePolicyTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void Queue_setting_controls_whether_a_prompt_can_wait(bool queueEnabled, bool workIsPending, bool expected) =>
        Assert.Equal(expected, ConversationQueuePolicy.CanSubmit(queueEnabled, workIsPending));
}

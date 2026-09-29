namespace Codev.Tests;

public sealed class InterruptedResponseTests
{
    [Fact]
    public void Marking_a_closed_turn_preserves_hosted_usage_and_other_message_metadata()
    {
        var usage = new OpenAiCodeTaskUsage(120, 18, 2, 2, 2);
        var message = new ChatMessage("assistant", "")
        {
            HostedUsage = usage,
            Thinking = "local reasoning",
            GenerationStats = new OllamaGenerationStats(TimeSpan.FromMilliseconds(40), 8, 20d, TimeSpan.Zero)
        };

        var interrupted = InterruptedResponse.MarkClosed(message);

        Assert.Equal(InterruptedResponse.ClosedMarker, interrupted.Content);
        Assert.Equal(usage, interrupted.HostedUsage);
        Assert.Equal(message.Thinking, interrupted.Thinking);
        Assert.Equal(message.GenerationStats, interrupted.GenerationStats);
    }

    [Theory]
    [InlineData("Generation stopped.", "")]
    [InlineData("Partial answer\n\n[Generation stopped.]", "Partial answer")]
    [InlineData("Partial answer\n\n[Generation stopped.]   ", "Partial answer")]
    public void Recognizes_stopped_responses_and_preserves_their_partial_text(string content, string expectedPartial)
    {
        Assert.True(InterruptedResponse.TryGetPartial(content, out var partial));
        Assert.Equal(expectedPartial, partial);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A complete response.")]
    [InlineData("This request did not finish before Codev closed.")]
    [InlineData("A user quoted [Generation stopped.] in their prompt.")]
    [InlineData("A user quoted [Generation stopped.]")]
    public void Does_not_offer_continue_for_non_interrupted_responses(string? content)
    {
        Assert.False(InterruptedResponse.TryGetPartial(content, out var partial));
        Assert.Equal("", partial);
    }
}

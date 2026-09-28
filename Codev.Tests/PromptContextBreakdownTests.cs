using Xunit;

namespace Codev.Tests;

public sealed class PromptContextBreakdownTests
{
    [Fact]
    public void Snapshot_shows_component_breakdown_exact_messages_and_actual_usage()
    {
        var messages = new List<ChatMessage>
        {
            new("system", "Be concise."),
            new("user", "Fix the failing test.")
        };
        var snapshot = PromptContextBreakdown.Create("ollama", "coder:latest", 65_536,
            [new("System instruction", "Be concise."), new("Project context", "source excerpt")], messages) with
        { ActualPromptTokens = 42 };
        messages[1] = new ChatMessage("user", "mutated after snapshot");

        var display = snapshot.ToDisplayText();
        Assert.Contains("Provider: ollama", display);
        Assert.Contains("Context limit: 65,536 tokens", display);
        Assert.Contains("System instruction: 11 characters", display);
        Assert.Contains("Provider-reported input: 42 tokens", display);
        Assert.Contains("[user]" + Environment.NewLine + "Fix the failing test.", display);
        Assert.DoesNotContain("mutated after snapshot", display);
    }

    [Fact]
    public void Snapshot_marks_estimates_as_rough_and_hosted_usage_unavailable()
    {
        var snapshot = PromptContextBreakdown.Create("anthropic", "claude-test", 0,
            [new("System instruction", "abcd")], [new("system", "abcd")]);

        var display = snapshot.ToDisplayText();
        Assert.Contains("Estimated input: ≈2 tokens (rough text estimate)", display);
        Assert.Contains("Context limit: model default", display);
        Assert.Contains("Provider-reported input: unavailable for this provider", display);
    }
}



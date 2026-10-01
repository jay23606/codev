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
        const string requestBody = "{\"model\":\"coder:latest\",\"messages\":[]}";
        var snapshot = PromptContextBreakdown.Create("ollama", "coder:latest", 65_536,
            [new("System instruction", "Be concise."), new("Project context", "source excerpt")], messages,
            requestBody) with { ActualPromptTokens = 42 };
        messages[1] = new ChatMessage("user", "mutated after snapshot");

        var display = snapshot.ToDisplayText();
        Assert.Contains("Provider: ollama", display);
        Assert.Contains("Context limit: 65,536 tokens", display);
        Assert.Contains("System instruction: 11 characters", display);
        Assert.Contains("Provider-reported input: 42 tokens", display);
        Assert.Contains("[user]" + Environment.NewLine + "Fix the failing test.", display);
        Assert.Contains("Exact request JSON body (before HTTP headers):", display);
        Assert.Contains(requestBody, display);
        Assert.Equal((requestBody.Length + 3) / 4, snapshot.EstimatedPromptTokens);
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
        Assert.Contains("Provider-reported input: not reported", display);
    }

    [Fact]
    public void Code_task_round_sections_keep_captured_context_separate_from_tool_interactions()
    {
        var sections = PromptContextBreakdown.BuildCodeTaskRoundSections(
            [new("System instructions", "system prompt"), new("Repository map", "src/app.cs: App")],
            "[{\"type\":\"function_call_output\",\"output\":\"read result\"}]", "[tool schema]", "think=false");

        Assert.Equal(new[] { "System instructions", "Repository map", "Tool calls and results", "Available tool schemas", "Generation controls" },
            sections.Select(section => section.Name));
        Assert.Equal("src/app.cs: App", sections[1].Content);
    }

    [Fact]
    public void Open_ai_input_display_includes_function_calls_and_outputs_from_the_current_round()
    {
        using var functionCall = System.Text.Json.JsonDocument.Parse("{\"type\":\"function_call\",\"call_id\":\"call-1\",\"name\":\"list_files\",\"arguments\":\"{}\"}");
        using var functionOutput = System.Text.Json.JsonDocument.Parse("{\"type\":\"function_call_output\",\"call_id\":\"call-1\",\"output\":\"src/app.cs\"}");
        var input = new object[]
        {
            new { role = "user", content = "Inspect the project" },
            functionCall.RootElement.Clone(),
            functionOutput.RootElement.Clone()
        };

        var messages = PromptContextBreakdown.ToOpenAiInputDisplayMessages(input);

        Assert.Equal(new[] { "user", "function_call", "function_call_output" }, messages.Select(message => message.Role));
        Assert.Contains("list_files", messages[1].Content);
        Assert.Contains("src/app.cs", messages[2].Content);
    }
}



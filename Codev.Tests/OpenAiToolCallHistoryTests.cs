using System.Text.Json;
using Codev;

public sealed class OpenAiToolCallHistoryTests
{
    [Fact]
    public void Openai_code_task_usage_accumulates_provider_reports_and_marks_missing_rounds_as_lower_bounds()
    {
        var usage = new OpenAiCodeTaskUsageAccumulator();

        Assert.Equal(new OpenAiCodeTaskUsage(12, 7, 1, 1, 1), usage.Add(new([], [], "", 12, 7)));
        var total = usage.Add(new([], [], "", 5, null));

        Assert.Equal(new OpenAiCodeTaskUsage(17, 7, 2, 1, 2), total);
        Assert.Contains("17 input", total!.DisplayLabel);
        Assert.DoesNotContain("at least 17 input", total.DisplayLabel);
        Assert.Contains("at least 7 output", total.DisplayLabel);
        Assert.Contains("2 completed API request(s)", total.DisplayLabel);
    }

    [Fact]
    public void Appends_all_provider_items_then_matching_tool_results_in_call_order()
    {
        using var document = JsonDocument.Parse("""
            {"type":"function_call","call_id":"call-a","name":"read_file","arguments":"{}"}
            """);
        var response = new OpenAiToolResponse(
            [JsonDocument.Parse("""{"type":"reasoning","id":"rs-a"}""").RootElement.Clone(), document.RootElement.Clone()],
            [document.RootElement.Clone()], "", 12);
        var input = new List<object> { new { role = "user", content = "inspect files" } };

        OpenAiToolCallHistory.AppendResponseAndOutputs(input, response, [new("call-a", "file contents")]);

        using var result = JsonDocument.Parse(JsonSerializer.Serialize(input));
        var items = result.RootElement.EnumerateArray().ToArray();
        Assert.Equal(4, items.Length);
        Assert.Equal("reasoning", items[1].GetProperty("type").GetString());
        Assert.Equal("function_call", items[2].GetProperty("type").GetString());
        Assert.Equal("function_call_output", items[3].GetProperty("type").GetString());
        Assert.Equal("call-a", items[3].GetProperty("call_id").GetString());
        Assert.Equal("file contents", items[3].GetProperty("output").GetString());
    }

    [Fact]
    public void Rejects_missing_or_unknown_results_before_mutating_input()
    {
        using var document = JsonDocument.Parse("""{"type":"function_call","call_id":"expected"}""");
        var response = new OpenAiToolResponse([document.RootElement.Clone()], [document.RootElement.Clone()], "", null);
        var input = new List<object> { "existing" };

        Assert.Throws<ArgumentException>(() => OpenAiToolCallHistory.AppendResponseAndOutputs(input, response, [new("other", "result")]));

        Assert.Single(input);
    }
}

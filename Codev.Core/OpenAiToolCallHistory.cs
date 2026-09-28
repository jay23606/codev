namespace Codev;

public sealed record OpenAiFunctionOutput(string CallId, string Output);

/// <summary>Appends a completed Responses API turn and its locally handled function results in protocol order.</summary>
public static class OpenAiToolCallHistory
{
    public static void AppendResponseAndOutputs(IList<object> input, OpenAiToolResponse response,
        IReadOnlyList<OpenAiFunctionOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(outputs);
        if (response.FunctionCalls.Count != outputs.Count)
            throw new ArgumentException("Every OpenAI function call must have exactly one local result.", nameof(outputs));

        var outputByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            if (string.IsNullOrWhiteSpace(output.CallId) || !outputByCallId.TryAdd(output.CallId, output.Output))
                throw new ArgumentException("Function result call IDs must be present and unique.", nameof(outputs));
        }

        var orderedOutputs = new List<object>(response.FunctionCalls.Count);
        foreach (var call in response.FunctionCalls)
        {
            var callId = call.TryGetProperty("call_id", out var callIdElement) ? callIdElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(callId) || !outputByCallId.Remove(callId, out var output))
                throw new ArgumentException("Function results must match every OpenAI call ID exactly once.", nameof(outputs));
            orderedOutputs.Add(new { type = "function_call_output", call_id = callId, output });
        }

        if (outputByCallId.Count != 0)
            throw new ArgumentException("Function results contained an unknown call ID.", nameof(outputs));

        foreach (var item in response.OutputItems) input.Add(item);
        foreach (var item in orderedOutputs) input.Add(item);
    }
}

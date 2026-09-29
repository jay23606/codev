using System.Text;
using System.Text.Json;

namespace Codev;

public sealed record OpenAiCodeTaskRunResult(string Transcript, OpenAiCodeTaskUsage? Usage);

/// <summary>Runs the shared OpenAI Responses function-call protocol while leaving workspace policy and tool execution to the desktop host.</summary>
public sealed class OpenAiCodeTaskRunner(CloudModelApiClient client)
{
    public const int MaxToolResultTranscriptCharacters = 6000;

    public async Task<OpenAiCodeTaskRunResult> RunAsync(string model, IReadOnlyList<object> initialInput,
        IReadOnlyList<object> tools,
        Func<int, IReadOnlyList<object>, CancellationToken, Task<string>> prepareRequestAsync,
        Func<string, JsonElement, CancellationToken, Task<string>> executeToolAsync,
        Func<string, JsonElement, CancellationToken, Task<bool>> confirmRepeatedToolCallAsync,
        Func<string, Task>? status = null,
        Func<OpenAiToolResponse, OpenAiCodeTaskUsage?, Task>? onResponse = null,
        Func<string, Task>? onRequestPayload = null,
        Func<string, Task>? onTranscript = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(initialInput);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(prepareRequestAsync);
        ArgumentNullException.ThrowIfNull(executeToolAsync);
        ArgumentNullException.ThrowIfNull(confirmRepeatedToolCallAsync);

        var input = initialInput.ToList();
        var transcript = new StringBuilder();
        var repeatedCalls = new RepeatedToolCallGuard();
        var usage = new OpenAiCodeTaskUsageAccumulator();
        for (var step = 0; step < OpenAiCodeTaskLimits.MaxModelStepsPerTurn; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (status is not null) await status($"OpenAI Code task · thinking · step {step + 1}/{OpenAiCodeTaskLimits.MaxModelStepsPerTurn}");
            var apiKey = await prepareRequestAsync(step, input, cancellationToken);
            var response = await client.CreateOpenAiToolResponseAsync(apiKey, model, input, tools, cancellationToken,
                onRequestPayload);
            var turnUsage = usage.Add(response);
            if (onResponse is not null) await onResponse(response, turnUsage);

            if (response.FunctionCalls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(response.OutputText)) transcript.Append(response.OutputText);
                if (onTranscript is not null) await onTranscript(transcript.ToString());
                return new OpenAiCodeTaskRunResult(transcript.ToString(), turnUsage);
            }

            if (!string.IsNullOrWhiteSpace(response.OutputText)) transcript.AppendLine(response.OutputText);
            var outputs = new List<OpenAiFunctionOutput>(response.FunctionCalls.Count);
            foreach (var call in response.FunctionCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = call.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                var callId = call.TryGetProperty("call_id", out var callIdElement) ? callIdElement.GetString() : null;
                var rawArguments = call.TryGetProperty("arguments", out var argumentsElement) ? argumentsElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(rawArguments))
                    throw new InvalidOperationException("OpenAI returned a malformed function call; Codev did not run it.");

                using var argumentsDocument = JsonDocument.Parse(rawArguments);
                var arguments = argumentsDocument.RootElement.Clone();
                if (repeatedCalls.Record(name, arguments) >= RepeatedToolCallGuard.ConfirmationThreshold)
                {
                    var confirmed = await confirmRepeatedToolCallAsync(name, arguments, cancellationToken);
                    if (!confirmed)
                    {
                        transcript.AppendLine().AppendLine("Code task stopped because the same tool call repeated. Send a follow-up with more guidance to continue.");
                        if (onTranscript is not null) await onTranscript(transcript.ToString());
                        return new OpenAiCodeTaskRunResult(transcript.ToString(), turnUsage);
                    }
                    repeatedCalls.AllowOneMore();
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (status is not null) await status($"OpenAI Code task · {name.Replace('_', ' ')}");
                var result = await executeToolAsync(name, arguments, cancellationToken);
                outputs.Add(new OpenAiFunctionOutput(callId, result));
                transcript.AppendLine().Append("**").Append(name.Replace('_', ' ')).AppendLine("**")
                    .AppendLine(result.Length > MaxToolResultTranscriptCharacters
                        ? result[..MaxToolResultTranscriptCharacters] + "\n… [tool output truncated]" : result);
                if (onTranscript is not null) await onTranscript(transcript.ToString());
            }
            OpenAiToolCallHistory.AppendResponseAndOutputs(input, response, outputs);
        }

        throw new InvalidOperationException($"OpenAI Code task reached the {OpenAiCodeTaskLimits.MaxModelStepsPerTurn}-step limit. Send a follow-up to continue.");
    }
}

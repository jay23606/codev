using System.Text;
using System.Text.Json;
using System.Diagnostics;

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
        CancellationToken cancellationToken = default,
        string? reasoningEffort = null,
        string? verbosity = null,
        string? reasoningMode = null,
        int? maxSteps = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(initialInput);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(prepareRequestAsync);
        ArgumentNullException.ThrowIfNull(executeToolAsync);
        ArgumentNullException.ThrowIfNull(confirmRepeatedToolCallAsync);

        var input = initialInput.ToList();
        var transcript = new StringBuilder();
        string? lastPublishedTranscript = null;

        async Task PublishTranscriptAsync(string value)
        {
            if (onTranscript is null || string.Equals(lastPublishedTranscript, value, StringComparison.Ordinal)) return;
            lastPublishedTranscript = value;
            await onTranscript(value).ConfigureAwait(false);
        }

        var repeatedCalls = new RepeatedToolCallGuard();
        var usage = new OpenAiCodeTaskUsageAccumulator();
        var stepLimit = Math.Clamp(maxSteps ?? OpenAiCodeTaskLimits.MaxModelStepsPerTurn, 1, OpenAiCodeTaskLimits.MaxModelStepsPerTurn);
        var executedTools = 0;
        var protocolCorrectionAttempts = 0;
        var executedToolsAtLastCorrection = 0;
        for (var step = 0; step < stepLimit; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (status is not null) await status($"OpenAI Code task · thinking · step {step + 1}/{stepLimit}");
            var apiKey = await prepareRequestAsync(step, input, cancellationToken);
            var streamedText = new StringBuilder();
            var lastPublished = Stopwatch.GetTimestamp();
            OpenAiToolResponse response;
            try
            {
                response = await client.StreamOpenAiToolResponseAsync(apiKey, model, input, tools, delta =>
                {
                    streamedText.Append(delta);
                    if (onTranscript is null || Stopwatch.GetElapsedTime(lastPublished) < TimeSpan.FromMilliseconds(100))
                        return Task.CompletedTask;
                    if (ToolOutputTranscriptParser.Parse(streamedText.ToString()).Outputs.Count > 0)
                        return Task.CompletedTask;
                    lastPublished = Stopwatch.GetTimestamp();
                    return PublishTranscriptAsync(transcript.ToString() + streamedText);
                }, cancellationToken, onRequestPayload, reasoningEffort, verbosity, reasoningMode);
            }
            catch
            {
                if (onTranscript is not null && streamedText.Length > 0)
                {
                    var partial = ToolOutputTranscriptParser.Parse(streamedText.ToString());
                    if (partial.Outputs.Count > 0)
                    {
                        if (!string.IsNullOrWhiteSpace(partial.DisplayText)) transcript.Append(partial.DisplayText);
                        transcript.AppendLine().AppendLine("Codev received tool-shaped text without a completed structured tool call. Treat any action claims in it as unverified.");
                        await PublishTranscriptAsync(transcript.ToString()).ConfigureAwait(false);
                    }
                    else
                    {
                        await PublishTranscriptAsync(transcript.ToString() + streamedText).ConfigureAwait(false);
                    }
                }
                throw;
            }
            var turnUsage = usage.Add(response);
            if (onResponse is not null) await onResponse(response, turnUsage);

            if (response.FunctionCalls.Count == 0)
            {
                var parsedText = ToolOutputTranscriptParser.Parse(response.OutputText);
                if (parsedText.Outputs.Count > 0)
                {
                    if (protocolCorrectionAttempts == 0 && step + 1 < stepLimit)
                    {
                        protocolCorrectionAttempts++;
                        executedToolsAtLastCorrection = executedTools;
                        input.Add(new { role = "assistant", content = response.OutputText });
                        input.Add(new { role = "user", content = "Your previous message contained text formatted like Codev tool output, but it did not contain a structured function call and was not executed. Do not imitate tool headings or tool-output JSON. If the user's request requires an action, call the corresponding structured function now and report only its real result. Otherwise answer normally without a tool-output envelope." });
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(parsedText.DisplayText)) transcript.Append(parsedText.DisplayText);
                    transcript.AppendLine().AppendLine("Codev could not verify the tool-shaped text in this response because no structured function call executed for it. Treat any action claims in that text as unverified; only the expanded tool entries above represent actions Codev actually ran.");
                    await PublishTranscriptAsync(transcript.ToString()).ConfigureAwait(false);
                    return new OpenAiCodeTaskRunResult(transcript.ToString(), turnUsage);
                }

                if (protocolCorrectionAttempts > 0 && executedTools == executedToolsAtLastCorrection)
                {
                    transcript.AppendLine().AppendLine("Codev received no structured function call after correcting tool-shaped text, so this turn did not perform or verify a requested action. Inspect the workspace before relying on any action claim.");
                    await PublishTranscriptAsync(transcript.ToString()).ConfigureAwait(false);
                    return new OpenAiCodeTaskRunResult(transcript.ToString(), turnUsage);
                }

                if (!string.IsNullOrWhiteSpace(response.OutputText)) transcript.Append(response.OutputText);
                await PublishTranscriptAsync(transcript.ToString()).ConfigureAwait(false);
                return new OpenAiCodeTaskRunResult(transcript.ToString(), turnUsage);
            }

            var callText = ToolOutputTranscriptParser.Parse(response.OutputText);
            if (!string.IsNullOrWhiteSpace(callText.DisplayText)) transcript.AppendLine(callText.DisplayText);
            if (callText.Outputs.Count > 0)
                transcript.AppendLine().AppendLine("Codev ignored tool-shaped assistant text because only structured function calls execute tools.");
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
                        await PublishTranscriptAsync(transcript.ToString()).ConfigureAwait(false);
                        return new OpenAiCodeTaskRunResult(transcript.ToString(), turnUsage);
                    }
                    repeatedCalls.AllowOneMore();
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (status is not null) await status($"OpenAI Code task · {name.Replace('_', ' ')}");
                var result = await executeToolAsync(name, arguments, cancellationToken);
                executedTools++;
                outputs.Add(new OpenAiFunctionOutput(callId, result));
                transcript.AppendLine().Append("**").Append(name.Replace('_', ' ')).AppendLine("**")
                    .AppendLine(UntrustedToolOutput.Truncate(result, MaxToolResultTranscriptCharacters));
                await PublishTranscriptAsync(transcript.ToString()).ConfigureAwait(false);
            }
            OpenAiToolCallHistory.AppendResponseAndOutputs(input, response, outputs);

            if (step == stepLimit - 1)
            {
                transcript.AppendLine().AppendLine()
                    .Append($"OpenAI Code task reached its {stepLimit}-request limit. The completed tool results are shown above; send a follow-up to continue.");
                await PublishTranscriptAsync(transcript.ToString()).ConfigureAwait(false);
                if (status is not null) await status("OpenAI Code task · request limit reached");
                return new OpenAiCodeTaskRunResult(transcript.ToString(), turnUsage);
            }
        }

        throw new InvalidOperationException("OpenAI Code task ended unexpectedly.");
    }
}

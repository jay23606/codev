using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codev;

public sealed record OllamaCodeTaskMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("tool_calls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? ToolCalls = null,
    [property: JsonPropertyName("tool_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolName = null);

public sealed record OllamaCodeTaskRunResult(string Transcript, bool ReachedStepLimit, int Requests);

/// <summary>Runs the Ollama tool-call protocol independently of a UI. Hosts retain control of workspace policy and tool effects through callbacks.</summary>
public sealed class OllamaCodeTaskRunner(HttpClient httpClient)
{
    public async Task<OllamaCodeTaskRunResult> RunAsync(Uri endpoint, string model,
        IReadOnlyList<OllamaCodeTaskMessage> initialHistory, IReadOnlyList<object> tools,
        bool think, int numCtx, double? temperature, double? topP, int? topK,
        double? presencePenalty, double? repeatPenalty, int? numPredict,
        Func<int, IReadOnlyList<OllamaCodeTaskMessage>, string, Task>? onRequest = null,
        Func<string, Task>? onThinking = null,
        Func<int, Task>? onPromptTokens = null,
        Func<string, Task>? status = null,
        Func<string, JsonElement, CancellationToken, Task<string>>? executeTool = null,
        Func<string, JsonElement, CancellationToken, Task<bool>>? confirmRepeatedToolCall = null,
        Func<string, Task>? onTranscript = null,
        string initialTranscript = "", int? maxSteps = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(initialHistory);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(executeTool);
        ArgumentNullException.ThrowIfNull(confirmRepeatedToolCall);

        var history = initialHistory.ToList();
        var transcript = new StringBuilder(initialTranscript);
        var repeatedCalls = new RepeatedToolCallGuard();
        var stepLimit = Math.Clamp(maxSteps ?? CodeTaskLimits.MaxModelStepsPerTurn, 1, CodeTaskLimits.MaxModelStepsPerTurn);
        var requests = 0;

        async Task PublishTranscriptAsync() =>
            await (onTranscript?.Invoke(transcript.ToString()) ?? Task.CompletedTask).ConfigureAwait(false);

        for (var round = 0; round < stepLimit; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (status is not null) await status($"Code task · thinking · step {round + 1}/{stepLimit}").ConfigureAwait(false);
            var payload = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = history,
                ["keep_alive"] = OllamaRuntimeClient.ConversationKeepAlive,
                ["tools"] = tools,
                ["think"] = think,
                ["stream"] = false
            };
            if (OllamaRequestOptions.Build(numCtx, temperature, topP, topK, presencePenalty, repeatPenalty, numPredict) is { } options)
                payload["options"] = options;
            var payloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
            if (onRequest is not null) await onRequest(round, history, payloadJson).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, OllamaEndpoint.ApiUri(endpoint, "api/chat"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Ollama returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).\n{body}");

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var apiError))
                throw new InvalidOperationException(apiError.GetString() ?? apiError.ToString());
            if (root.TryGetProperty("prompt_eval_count", out var promptCount) && promptCount.TryGetInt32(out var promptTokens) && onPromptTokens is not null)
                await onPromptTokens(promptTokens).ConfigureAwait(false);

            var message = root.GetProperty("message");
            if (message.TryGetProperty("thinking", out var thinkingChunk) && thinkingChunk.GetString() is { Length: > 0 } thinkingText && onThinking is not null)
                await onThinking(thinkingText).ConfigureAwait(false);
            var text = message.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "";
            var calls = message.TryGetProperty("tool_calls", out var callArray) && callArray.ValueKind == JsonValueKind.Array
                ? callArray.EnumerateArray().Select(call => call.Clone()).ToArray()
                : [];
            requests++;

            if (calls.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(text)) transcript.Append(text);
                await PublishTranscriptAsync().ConfigureAwait(false);
                return new OllamaCodeTaskRunResult(transcript.ToString(), false, requests);
            }

            history.Add(new OllamaCodeTaskMessage("assistant", text, JsonSerializer.SerializeToElement(calls)));
            if (!string.IsNullOrWhiteSpace(text)) transcript.AppendLine(text);
            foreach (var call in calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var function = call.GetProperty("function");
                var name = function.GetProperty("name").GetString() ?? "";
                var arguments = function.TryGetProperty("arguments", out var args) ? args.Clone() : default;
                if (status is not null) await status($"Code task · {name.Replace('_', ' ')}").ConfigureAwait(false);
                if (repeatedCalls.Record(name, arguments) >= RepeatedToolCallGuard.ConfirmationThreshold)
                {
                    var confirmed = await confirmRepeatedToolCall(name, arguments, cancellationToken).ConfigureAwait(false);
                    if (!confirmed)
                    {
                        transcript.AppendLine().AppendLine("Code task stopped because the same tool call repeated. Send a follow-up with more guidance to continue.");
                        await PublishTranscriptAsync().ConfigureAwait(false);
                        return new OllamaCodeTaskRunResult(transcript.ToString(), false, requests);
                    }
                    repeatedCalls.AllowOneMore();
                }
                var result = await executeTool(name, arguments, cancellationToken).ConfigureAwait(false);
                history.Add(new OllamaCodeTaskMessage("tool", result, null, name));
                transcript.AppendLine().Append("**").Append(name.Replace('_', ' ')).AppendLine("**")
                    .AppendLine(UntrustedToolOutput.Truncate(result, 6_000));
                await PublishTranscriptAsync().ConfigureAwait(false);
            }
        }

        transcript.AppendLine().AppendLine()
            .Append($"Code task reached its {stepLimit}-step limit. The completed tool results are shown above; send a follow-up to continue.");
        await PublishTranscriptAsync().ConfigureAwait(false);
        if (status is not null) await status("Code task · profile step limit reached").ConfigureAwait(false);
        return new OllamaCodeTaskRunResult(transcript.ToString(), true, requests);
    }
}

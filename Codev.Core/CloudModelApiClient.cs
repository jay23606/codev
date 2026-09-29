using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace Codev;

public static class CloudModelProviders
{
    public const string OpenAI = "openai";
    public const string Anthropic = "anthropic";
    public static bool IsCloud(string? provider) => provider is OpenAI or Anthropic;
}

public static class OpenAiCodeTaskLimits
{
    public const int MaxOutputTokensPerRequest = 4096;
    public const int MaxModelStepsPerTurn = 8;
}

public sealed record CloudModel(string Provider, string Id, string DisplayName);
public sealed record CloudChatMessage(string Role, string Content);
public sealed record OpenAiToolResponse(IReadOnlyList<JsonElement> OutputItems, IReadOnlyList<JsonElement> FunctionCalls,
    string OutputText, int? InputTokens, int? OutputTokens = null);

public sealed record OpenAiCodeTaskUsage(int? InputTokens, int? OutputTokens, int InputReports, int OutputReports, int Responses)
{
    public string DisplayLabel
    {
        get
        {
            var input = InputTokens is { } inputTokens ? $"{(InputReports < Responses ? "at least " : "")}{inputTokens:N0} input" : "input unavailable";
            var output = OutputTokens is { } outputTokens ? $"{(OutputReports < Responses ? "at least " : "")}{outputTokens:N0} output" : "output unavailable";
            return $"OpenAI Code task · {Responses} completed API request(s) · {input} · {output} tokens";
        }
    }
}

public sealed class OpenAiCodeTaskUsageAccumulator
{
    private int _responses;
    private int _inputReports;
    private int _outputReports;
    private int _inputTokens;
    private int _outputTokens;

    public OpenAiCodeTaskUsage? Add(OpenAiToolResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        _responses++;
        if (response.InputTokens is { } input) { _inputReports++; _inputTokens += input; }
        if (response.OutputTokens is { } output) { _outputReports++; _outputTokens += output; }
        return _inputReports > 0 || _outputReports > 0
            ? new OpenAiCodeTaskUsage(_inputReports > 0 ? _inputTokens : null, _outputReports > 0 ? _outputTokens : null,
                _inputReports, _outputReports, _responses)
            : null;
    }
}

/// <summary>Small REST client for hosted model discovery and text streaming. API keys are supplied per request; persistence is handled by the OS credential vault.</summary>
public sealed class CloudModelApiClient(HttpClient http)
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);

    private static readonly Uri OpenAiBase = new("https://api.openai.com/v1/");
    private static readonly Uri AnthropicBase = new("https://api.anthropic.com/v1/");
    private const string AnthropicVersion = "2023-06-01";

    public async Task<OpenAiToolResponse> CreateOpenAiToolResponseAsync(string apiKey, string model,
        object input, IReadOnlyList<object> tools, CancellationToken cancellationToken = default,
        Func<string, Task>? onRequestPayload = null)
    {
        Validate(CloudModelProviders.OpenAI, apiKey);
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Choose an OpenAI model first.", nameof(model));
        ValidateRequestTimeout();
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["input"] = input,
            ["tools"] = tools,
            ["tool_choice"] = "auto",
            ["stream"] = false,
            ["store"] = false,
            ["max_output_tokens"] = OpenAiCodeTaskLimits.MaxOutputTokensPerRequest
        };
        var payloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var request = CreateRequest(HttpMethod.Post, new Uri(OpenAiBase, "responses"), CloudModelProviders.OpenAI, apiKey);
        request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        if (onRequestPayload is not null) await onRequestPayload(payloadJson).ConfigureAwait(false);
        using var requestTimeout = CreateRequestTimeout(cancellationToken);
        using var response = await AwaitWithRequestTimeoutAsync(token => http.SendAsync(request, token),
            requestTimeout, cancellationToken, "OpenAI Code task").ConfigureAwait(false);
        await EnsureSuccessAsync(response, requestTimeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false),
            cancellationToken: requestTimeout.Token).ConfigureAwait(false);
        var root = document.RootElement;
        if (GetString(root, "status") is { } status && status != "completed")
        {
            var detail = root.TryGetProperty("error", out var error) && GetString(error, "message") is { Length: > 0 } message
                ? message
                : root.TryGetProperty("incomplete_details", out var incomplete) && GetString(incomplete, "reason") is { Length: > 0 } reason
                    ? $"The response was incomplete ({reason})." : $"The response ended with status '{status}'.";
            throw new InvalidOperationException($"OpenAI {detail}");
        }
        return ParseOpenAiToolResponse(root);
    }

    public async Task<OpenAiToolResponse> StreamOpenAiToolResponseAsync(string apiKey, string model,
        object input, IReadOnlyList<object> tools, Func<string, Task>? onTextDelta = null,
        CancellationToken cancellationToken = default, Func<string, Task>? onRequestPayload = null)
    {
        Validate(CloudModelProviders.OpenAI, apiKey);
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Choose an OpenAI model first.", nameof(model));
        ValidateRequestTimeout();
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["input"] = input,
            ["tools"] = tools,
            ["tool_choice"] = "auto",
            ["stream"] = true,
            ["store"] = false,
            ["max_output_tokens"] = OpenAiCodeTaskLimits.MaxOutputTokensPerRequest
        };
        var payloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var request = CreateRequest(HttpMethod.Post, new Uri(OpenAiBase, "responses"), CloudModelProviders.OpenAI, apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        if (onRequestPayload is not null) await onRequestPayload(payloadJson).ConfigureAwait(false);
        using var requestTimeout = CreateRequestTimeout(cancellationToken);
        using var response = await AwaitWithRequestTimeoutAsync(
            token => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token),
            requestTimeout, cancellationToken, "OpenAI Code task").ConfigureAwait(false);
        await AwaitWithRequestTimeoutAsync(token => EnsureSuccessAsync(response, token),
            requestTimeout, cancellationToken, "OpenAI Code task").ConfigureAwait(false);
        await using var stream = await AwaitWithRequestTimeoutAsync(
            token => response.Content.ReadAsStreamAsync(token), requestTimeout, cancellationToken, "OpenAI Code task").ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var eventName = "";
        var eventData = new StringBuilder();
        OpenAiToolResponse? completedResponse = null;
        while (await AwaitWithRequestTimeoutAsync(
                   token => reader.ReadLineAsync(token).AsTask(), requestTimeout, cancellationToken, "OpenAI Code task").ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (eventData.Length == 0) { eventName = ""; continue; }
                var data = eventData.ToString();
                eventData.Clear();
                if (data == "[DONE]") break;
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                var type = GetString(root, "type") ?? eventName;
                if (type is "error" or "response.failed") throw new InvalidOperationException(ReadApiError(root));
                if (type == "response.incomplete") throw new InvalidOperationException(ReadIncompleteResponse(root));
                if (type == "response.output_text.delta" && GetString(root, "delta") is { Length: > 0 } delta && onTextDelta is not null)
                    await onTextDelta(delta).ConfigureAwait(false);
                if (type == "response.completed" && root.TryGetProperty("response", out var responseRoot))
                    completedResponse = ParseOpenAiToolResponse(responseRoot);
                eventName = "";
                continue;
            }
            if (line.StartsWith(':')) continue;
            if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].TrimStart();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (eventData.Length > 0) eventData.Append('\n');
                eventData.Append(line[5..].TrimStart());
            }
        }
        return completedResponse ?? throw new IOException("The OpenAI stream ended before the provider reported a completed response.");
    }

    private static OpenAiToolResponse ParseOpenAiToolResponse(JsonElement root)
    {
        var outputItems = root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array
            ? output.EnumerateArray().Select(item => item.Clone()).ToArray() : [];
        var calls = outputItems.Where(item => GetString(item, "type") == "function_call").ToArray();
        var outputText = new StringBuilder();
        foreach (var item in outputItems.Where(item => GetString(item, "type") == "message"))
            if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var block in content.EnumerateArray())
                    if (GetString(block, "type") == "output_text" && GetString(block, "text") is { } text) outputText.Append(text);
                    else if (GetString(block, "type") == "refusal" && GetString(block, "refusal") is { } refusal) outputText.Append(refusal);
        int? inputTokens = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("input_tokens", out var inputTokenValue) &&
            inputTokenValue.TryGetInt32(out var count) && count >= 0 ? count : null;
        int? outputTokens = root.TryGetProperty("usage", out usage) && usage.TryGetProperty("output_tokens", out var outputTokenValue) &&
            outputTokenValue.TryGetInt32(out var outputCount) && outputCount >= 0 ? outputCount : null;
        return new OpenAiToolResponse(outputItems, calls, outputText.ToString(), inputTokens, outputTokens);
    }

    public async Task<IReadOnlyList<CloudModel>> ListModelsAsync(string provider, string apiKey, CancellationToken cancellationToken = default)
    {
        Validate(provider, apiKey);
        ValidateRequestTimeout();
        if (provider == CloudModelProviders.OpenAI)
        {
            var openAiModels = new List<CloudModel>();
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            string? after = null;
            do
            {
                var url = after is null
                    ? new Uri(OpenAiBase, "models")
                    : new UriBuilder(new Uri(OpenAiBase, "models")) { Query = $"after={Uri.EscapeDataString(after)}" }.Uri;
                using var request = CreateRequest(HttpMethod.Get, url, provider, apiKey);
                using var requestTimeout = CreateRequestTimeout(cancellationToken);
                using var response = await AwaitWithRequestTimeoutAsync(token => http.SendAsync(request, token),
                    requestTimeout, cancellationToken, "OpenAI model discovery").ConfigureAwait(false);
                await EnsureSuccessAsync(response, requestTimeout.Token).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(requestTimeout.Token),
                    cancellationToken: requestTimeout.Token).ConfigureAwait(false);
                var root = document.RootElement;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                        if (IsOpenAiTextModel(id)) openAiModels.Add(new CloudModel(provider, id!, id!));
                    }
                }
                var hasMore = root.TryGetProperty("has_more", out var hasMoreValue) && hasMoreValue.ValueKind == JsonValueKind.True;
                var next = root.TryGetProperty("last_id", out var lastId) && lastId.ValueKind == JsonValueKind.String ? lastId.GetString() : null;
                if (!hasMore || string.IsNullOrWhiteSpace(next) || !seenCursors.Add(next)) break;
                after = next;
            } while (true);
            return openAiModels.DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var models = new List<CloudModel>();
        var seenAnthropicCursors = new HashSet<string>(StringComparer.Ordinal);
        string? afterId = null;
        do
        {
            var url = new UriBuilder(new Uri(AnthropicBase, "models")) { Query = afterId is null ? "limit=1000" : $"limit=1000&after_id={Uri.EscapeDataString(afterId)}" }.Uri;
            using var request = CreateRequest(HttpMethod.Get, url, provider, apiKey);
            using var requestTimeout = CreateRequestTimeout(cancellationToken);
            using var response = await AwaitWithRequestTimeoutAsync(token => http.SendAsync(request, token),
                requestTimeout, cancellationToken, "Anthropic model discovery").ConfigureAwait(false);
            await EnsureSuccessAsync(response, requestTimeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(requestTimeout.Token),
                cancellationToken: requestTimeout.Token).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idValue) || string.IsNullOrWhiteSpace(idValue.GetString())) continue;
                    var id = idValue.GetString()!;
                    var displayName = item.TryGetProperty("display_name", out var display) && !string.IsNullOrWhiteSpace(display.GetString()) ? display.GetString()! : id;
                    models.Add(new CloudModel(provider, id, displayName));
                }
            }
            var hasMore = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
            var next = root.TryGetProperty("last_id", out var last) && last.ValueKind == JsonValueKind.String ? last.GetString() : null;
            afterId = hasMore && !string.IsNullOrWhiteSpace(next) && seenAnthropicCursors.Add(next) ? next : null;
        } while (afterId is not null);
        return models.DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string provider,
        string apiKey,
        string model,
        IReadOnlyList<CloudChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        int? maxOutputTokens = null,
        Func<int, Task>? onInputTokenCount = null,
        Func<string, Task>? onRequestPayload = null,
        Func<int, Task>? onOutputTokenCount = null)
    {
        Validate(provider, apiKey);
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Choose a hosted model first.", nameof(model));
        if (maxOutputTokens is <= 0 or > 2048) throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        ValidateRequestTimeout();

        var endpoint = provider == CloudModelProviders.OpenAI ? new Uri(OpenAiBase, "responses") : new Uri(AnthropicBase, "messages");
        var payload = provider == CloudModelProviders.OpenAI
            ? BuildOpenAiPayload(model, messages, maxOutputTokens)
            : BuildAnthropicPayload(model, messages, maxOutputTokens);
        var payloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var request = CreateRequest(HttpMethod.Post, endpoint, provider, apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        if (onRequestPayload is not null) await onRequestPayload(payloadJson).ConfigureAwait(false);
        using var requestTimeout = CreateRequestTimeout(cancellationToken);
        using var response = await AwaitWithRequestTimeoutAsync(
            token => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token),
            requestTimeout, cancellationToken, $"{ProviderDisplayName(provider)} chat").ConfigureAwait(false);
        await AwaitWithRequestTimeoutAsync(token => EnsureSuccessAsync(response, token),
            requestTimeout, cancellationToken, $"{ProviderDisplayName(provider)} chat").ConfigureAwait(false);
        await using var stream = await AwaitWithRequestTimeoutAsync(
            token => response.Content.ReadAsStreamAsync(token), requestTimeout, cancellationToken,
            $"{ProviderDisplayName(provider)} chat").ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var eventName = "";
        var eventData = new StringBuilder();
        var completed = false;
        while (await AwaitWithRequestTimeoutAsync(
                   token => reader.ReadLineAsync(token).AsTask(), requestTimeout, cancellationToken,
                   $"{ProviderDisplayName(provider)} chat").ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (eventData.Length == 0) { eventName = ""; continue; }
                var data = eventData.ToString();
                eventData.Clear();
                if (data == "[DONE]") { completed = true; eventName = ""; continue; }
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                var type = GetString(root, "type") ?? eventName;
                if (type is "error" or "response.failed")
                    throw new InvalidOperationException(ReadApiError(root));
                if (onInputTokenCount is not null && TryReadInputTokenCount(provider, type, root) is { } inputTokenCount)
                    await onInputTokenCount(inputTokenCount).ConfigureAwait(false);
                if (onOutputTokenCount is not null && TryReadOutputTokenCount(provider, type, root) is { } outputTokenCount)
                    await onOutputTokenCount(outputTokenCount).ConfigureAwait(false);
                if (provider == CloudModelProviders.OpenAI)
                {
                    if (type == "response.output_text.delta" && GetString(root, "delta") is { Length: > 0 } delta)
                        yield return delta;
                    else if (type == "response.incomplete")
                        throw new InvalidOperationException(ReadIncompleteResponse(root));
                    else if (type == "response.completed") completed = true;
                }
                else
                {
                    if (type == "content_block_delta" && root.TryGetProperty("delta", out var block) && GetString(block, "type") == "text_delta" && GetString(block, "text") is { Length: > 0 } text)
                        yield return text;
                    else if (type == "message_delta" && root.TryGetProperty("delta", out var messageDelta) && GetString(messageDelta, "stop_reason") == "max_tokens")
                        throw new InvalidOperationException("Anthropic stopped at the output token limit; the reply may be incomplete.");
                    else if (type == "message_stop") completed = true;
                }
                eventName = "";
                continue;
            }

            if (line.StartsWith(':')) continue;
            if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].TrimStart();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (eventData.Length > 0) eventData.Append('\n');
                eventData.Append(line[5..].TrimStart());
            }
        }
        if (!completed)
            throw new IOException($"The {ProviderDisplayName(provider)} stream ended before the provider reported a completed response.");
    }

    private void ValidateRequestTimeout()
    {
        if (RequestTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("The hosted request timeout must be greater than zero.");
    }

    private CancellationTokenSource CreateRequestTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        return timeout;
    }

    private async Task<T> AwaitWithRequestTimeoutAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationTokenSource requestTimeout, CancellationToken callerToken, string requestName)
    {
        try
        {
            return await operation(requestTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested)
        {
            throw CreateRequestTimeoutException(requestName, exception);
        }
    }

    private async Task AwaitWithRequestTimeoutAsync(Func<CancellationToken, Task> operation,
        CancellationTokenSource requestTimeout, CancellationToken callerToken, string requestName)
    {
        try
        {
            await operation(requestTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested)
        {
            throw CreateRequestTimeoutException(requestName, exception);
        }
    }

    private TimeoutException CreateRequestTimeoutException(string requestName, OperationCanceledException innerException)
    {
        var timeoutLabel = RequestTimeout >= TimeSpan.FromMinutes(1)
            ? $"{RequestTimeout.TotalMinutes:N0} minutes"
            : $"{RequestTimeout.TotalSeconds:N0} seconds";
        return new TimeoutException($"{requestName} request timed out after {timeoutLabel}.", innerException);
    }

    private static object BuildOpenAiPayload(string model, IReadOnlyList<CloudChatMessage> messages, int? maxOutputTokens)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["input"] = messages.Select(message => new { role = NormalizeRole(message.Role), content = message.Content }).ToArray(),
            ["stream"] = true,
            ["store"] = false
        };
        if (maxOutputTokens is { } max) payload["max_output_tokens"] = max;
        return payload;
    }

    private static object BuildAnthropicPayload(string model, IReadOnlyList<CloudChatMessage> messages, int? maxOutputTokens)
    {
        var system = string.Join("\n\n", messages.Where(message => message.Role is "system" or "developer").Select(message => message.Content).Where(content => !string.IsNullOrWhiteSpace(content)));
        var chatMessages = messages.Where(message => message.Role is "user" or "assistant")
            .Select(message => new { role = message.Role, content = message.Content }).ToArray();
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxOutputTokens ?? 4096,
            ["messages"] = chatMessages,
            ["stream"] = true
        };
        if (!string.IsNullOrWhiteSpace(system)) payload["system"] = system;
        return payload;
    }

    private static string NormalizeRole(string role) => role == "developer" ? "system" : role;

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string provider, string apiKey)
    {
        var request = new HttpRequestMessage(method, uri);
        if (provider == CloudModelProviders.OpenAI)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        else
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var details = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string message;
        try
        {
            using var document = JsonDocument.Parse(details);
            message = ReadApiError(document.RootElement);
        }
        catch (JsonException) { message = details; }
        throw new HttpRequestException($"Hosted provider returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {message}{FormatRetryAfter(response.Headers.RetryAfter)}", null, response.StatusCode);
    }

    private static string FormatRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            var seconds = Math.Max(0, Math.Ceiling(delta.TotalSeconds));
            return seconds == 0 ? " Retry immediately." : $" Retry after about {seconds:N0} seconds.";
        }
        return retryAfter?.Date is { } date
            ? $" Retry after {date.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)}."
            : "";
    }

    private static string ReadApiError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object && GetString(error, "message") is { Length: > 0 } detail) return detail;
            if (error.ValueKind == JsonValueKind.String) return error.GetString()!;
        }
        if (root.TryGetProperty("response", out var response) && response.TryGetProperty("error", out var responseError) && GetString(responseError, "message") is { Length: > 0 } responseDetail)
            return responseDetail;
        return "The provider returned an error without details.";
    }

    private static string ReadIncompleteResponse(JsonElement root)
    {
        if (root.TryGetProperty("response", out var response) && response.TryGetProperty("incomplete_details", out var details) && GetString(details, "reason") is { Length: > 0 } reason)
            return $"The OpenAI response is incomplete ({reason}).";
        return "The OpenAI response is incomplete.";
    }

    private static int? TryReadInputTokenCount(string provider, string type, JsonElement root)
    {
        if (provider == CloudModelProviders.OpenAI && type == "response.completed" &&
            root.TryGetProperty("response", out var response) && response.TryGetProperty("usage", out var openAiUsage) &&
            openAiUsage.TryGetProperty("input_tokens", out var openAiInput) && openAiInput.TryGetInt32(out var openAiCount) && openAiCount >= 0)
            return openAiCount;
        if (provider == CloudModelProviders.Anthropic && type == "message_start" &&
            root.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var anthropicUsage) &&
            anthropicUsage.TryGetProperty("input_tokens", out var anthropicInput) && anthropicInput.TryGetInt32(out var anthropicCount) && anthropicCount >= 0)
            return anthropicCount;
        return null;
    }

    private static int? TryReadOutputTokenCount(string provider, string type, JsonElement root)
    {
        if (provider == CloudModelProviders.OpenAI && type == "response.completed" &&
            root.TryGetProperty("response", out var response) && response.TryGetProperty("usage", out var openAiUsage) &&
            openAiUsage.TryGetProperty("output_tokens", out var openAiOutput) && openAiOutput.TryGetInt32(out var openAiCount) && openAiCount >= 0)
            return openAiCount;
        if (provider == CloudModelProviders.Anthropic && type == "message_delta" &&
            root.TryGetProperty("usage", out var anthropicUsage) && anthropicUsage.TryGetProperty("output_tokens", out var anthropicOutput) &&
            anthropicOutput.TryGetInt32(out var anthropicCount) && anthropicCount >= 0)
            return anthropicCount;
        return null;
    }

    private static string ProviderDisplayName(string provider) => provider == CloudModelProviders.OpenAI ? "OpenAI" : "Anthropic";

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsOpenAiTextModel(string? id) => id is not null &&
        (id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) || id.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
         id.StartsWith("o3", StringComparison.OrdinalIgnoreCase) || id.StartsWith("o4", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(id, "chat-latest", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(id, "codex-mini-latest", StringComparison.OrdinalIgnoreCase)) &&
        !ContainsAny(id, "audio", "transcribe", "realtime", "embedding", "moderation", "search", "tts", "image", "whisper");

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static void Validate(string provider, string apiKey)
    {
        if (provider is not (CloudModelProviders.OpenAI or CloudModelProviders.Anthropic))
            throw new ArgumentOutOfRangeException(nameof(provider), "Choose OpenAI or Anthropic.");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException($"Enter a {provider} API key before using hosted models.");
    }
}

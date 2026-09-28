using System.Net;
using System.Text;

namespace Codev.Tests;

public sealed class CloudModelApiClientTests
{
    [Fact]
    public async Task Lists_openai_chat_models_with_bearer_auth_and_omits_unrelated_models()
    {
        HttpRequestMessage? observed = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            observed = request;
            return Json("""
                {"data":[{"id":"gpt-6-sol"},{"id":"gpt-6-codex"},{"id":"gpt-audio-preview"},{"id":"text-embedding-3-large"},{"id":"whisper-1"}]}
                """);
        }));

        var models = await new CloudModelApiClient(http).ListModelsAsync(CloudModelProviders.OpenAI, "test-openai-key");

        Assert.Equal(["gpt-6-codex", "gpt-6-sol"], models.Select(model => model.Id));
        Assert.Equal("Bearer", observed!.Headers.Authorization!.Scheme);
        Assert.Equal("test-openai-key", observed.Headers.Authorization.Parameter);
        Assert.DoesNotContain("test-openai-key", observed.RequestUri!.ToString());

        var emptyResponseHttp = new HttpClient(new StubHandler(_ => Json("{\"data\":[]}")));
        using (emptyResponseHttp)
            Assert.Empty(await new CloudModelApiClient(emptyResponseHttp).ListModelsAsync(CloudModelProviders.OpenAI, "key"));
    }

    [Fact]
    public async Task Lists_anthropic_models_across_pages_with_native_auth_headers()
    {
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request);
            var response = request.RequestUri!.Query.Contains("after_id=", StringComparison.Ordinal)
                ? """{"data":[{"id":"claude-sonnet-5","display_name":"Claude Sonnet 5"}],"has_more":false,"last_id":"claude-sonnet-5"}"""
                : """{"data":[{"id":"claude-opus-5","display_name":"Claude Opus 5"}],"has_more":true,"last_id":"claude-opus-5"}""";
            return Json(response);
        }));

        var models = await new CloudModelApiClient(http).ListModelsAsync(CloudModelProviders.Anthropic, "test-anthropic-key");

        Assert.Equal(["claude-opus-5", "claude-sonnet-5"], models.Select(model => model.Id));
        Assert.Equal(2, requests.Count);
        Assert.Equal("test-anthropic-key", requests[0].Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", requests[0].Headers.GetValues("anthropic-version").Single());
        Assert.DoesNotContain("test-anthropic-key", requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Lists_openai_models_across_pages_and_stops_on_last_id()
    {
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request);
            return request.RequestUri!.Query.Contains("after=", StringComparison.Ordinal)
                ? Json("{\"data\":[{\"id\":\"gpt-6-sol\"}],\"has_more\":true,\"last_id\":\"cursor+value\"}")
                : Json("{\"data\":[{\"id\":\"gpt-6-codex\"}],\"has_more\":true,\"last_id\":\"cursor+value\"}");
        }));

        var models = await new CloudModelApiClient(http).ListModelsAsync(CloudModelProviders.OpenAI, "key");

        Assert.Equal(["gpt-6-codex", "gpt-6-sol"], models.Select(model => model.Id));
        Assert.Equal(2, requests.Count);
        Assert.Contains("after=cursor%2Bvalue", requests[1].RequestUri!.Query);
    }

    [Theory]
    [InlineData(CloudModelProviders.OpenAI, """
        data: {"type":"response.output_text.delta","delta":"hello "}

        data: {"type":"response.output_text.delta","delta":"world"}

        data: {"type":"response.completed"}


        """)]
    [InlineData(CloudModelProviders.Anthropic, """
        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"hello "}}

        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"world"}}

        event: message_stop
        data: {"type":"message_stop"}


        """)]
    public async Task Streams_text_deltas_and_never_puts_the_key_in_the_body(string provider, string sse)
    {
        HttpRequestMessage? observed = null;
        string? observedBody = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            observed = request;
            observedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }));
        var client = new CloudModelApiClient(http);
        var messages = new[] { new CloudChatMessage("system", "Be concise"), new CloudChatMessage("user", "Say hello") };
        var output = new StringBuilder();
        await foreach (var delta in client.StreamChatAsync(provider, "never-in-body", "test-model", messages)) output.Append(delta);

        Assert.Equal("hello world", output.ToString());
        Assert.DoesNotContain("never-in-body", observedBody);
        if (provider == CloudModelProviders.OpenAI)
        {
            Assert.Equal("Bearer", observed!.Headers.Authorization!.Scheme);
            Assert.Contains("\"store\":false", observedBody);
        }
        else
            Assert.Equal("never-in-body", observed!.Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public async Task Anthropic_requests_put_system_in_its_top_level_field()
    {
        HttpRequestMessage? observed = null;
        string? observedBody = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            observed = request;
            observedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"message_stop\"}\n\n", Encoding.UTF8, "text/event-stream") };
        }));
        await foreach (var _ in new CloudModelApiClient(http).StreamChatAsync(CloudModelProviders.Anthropic, "key", "claude-test",
            [new("system", "instructions"), new("user", "prompt"), new("assistant", "answer"), new("user", "follow-up")])) { }

        Assert.Contains("\"system\":\"instructions\"", observedBody);
        Assert.Contains("\"role\":\"assistant\"", observedBody);
        Assert.DoesNotContain("\"role\":\"system\"", observedBody);
    }

    [Fact]
    public async Task Anthropic_omits_empty_system_prompt()
    {
        string? body = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"message_stop\"}\n\n", Encoding.UTF8, "text/event-stream") };
        }));

        await foreach (var _ in new CloudModelApiClient(http).StreamChatAsync(CloudModelProviders.Anthropic, "key", "claude-test", [new("user", "hello")])) { }

        Assert.DoesNotContain("system", body);
    }

    [Theory]
    [InlineData(CloudModelProviders.OpenAI, "max_output_tokens")]
    [InlineData(CloudModelProviders.Anthropic, "max_tokens")]
    public async Task Can_cap_hosted_preflight_output_tokens(string provider, string outputTokenField)
    {
        string? body = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var stream = provider == CloudModelProviders.OpenAI
                ? "data: {\"type\":\"response.completed\"}\n\n"
                : "data: {\"type\":\"message_stop\"}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }));

        await foreach (var _ in new CloudModelApiClient(http).StreamChatAsync(provider, "key", "model",
                           [new("user", "select rules")], maxOutputTokens: 192)) { }

        Assert.Contains($"\"{outputTokenField}\":192", body);
    }

    [Theory]
    [InlineData(CloudModelProviders.OpenAI, "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":25}}}\n\n", 25)]
    [InlineData(CloudModelProviders.Anthropic, "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":37}}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", 37)]
    public async Task Reports_provider_input_token_usage_from_stream_events(string provider, string stream, int expectedInputTokens)
    {
        string? sentBody = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
            };
        }));
        int? inputTokens = null;
        string? capturedRequestBody = null;

        await foreach (var _ in new CloudModelApiClient(http).StreamChatAsync(provider, "key", "model",
                           [new("user", "hello")], onInputTokenCount: count =>
                           {
                               inputTokens = count;
                               return Task.CompletedTask;
                           }, onRequestPayload: body =>
                           {
                               capturedRequestBody = body;
                               return Task.CompletedTask;
                           })) { }

        Assert.Equal(expectedInputTokens, inputTokens);
        Assert.Equal(sentBody, capturedRequestBody);
        Assert.Contains("\"model\":\"model\"", sentBody);
    }

    [Theory]
    [InlineData(CloudModelProviders.OpenAI, "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n")]
    [InlineData(CloudModelProviders.Anthropic, "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n")]
    public async Task Rejects_a_stream_that_ends_without_a_provider_completion_event(string provider, string sse)
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        }));
        var client = new CloudModelApiClient(http);

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var _ in client.StreamChatAsync(provider, "key", "model", [new("user", "hello")])) { }
        });

        Assert.Contains("ended before", exception.Message);
    }

    [Fact]
    public async Task Reports_openai_stream_errors_with_provider_message()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"message\":\"model unavailable\"}}}\n\n", Encoding.UTF8, "text/event-stream")
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in new CloudModelApiClient(http).StreamChatAsync(CloudModelProviders.OpenAI, "key", "model", [new("user", "hello")])) { }
        });

        Assert.Equal("model unavailable", exception.Message);
    }

    [Fact]
    public async Task Identifies_an_openai_truncated_response_instead_of_treating_it_as_complete()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"response.incomplete\",\"response\":{\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}\n\n", Encoding.UTF8, "text/event-stream")
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in new CloudModelApiClient(http).StreamChatAsync(CloudModelProviders.OpenAI, "key", "model", [new("user", "hello")])) { }
        });

        Assert.Contains("max_output_tokens", exception.Message);
    }

    [Fact]
    public async Task Identifies_an_anthropic_output_limit()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}\n\n", Encoding.UTF8, "text/event-stream")
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in new CloudModelApiClient(http).StreamChatAsync(CloudModelProviders.Anthropic, "key", "model", [new("user", "hello")])) { }
        });

        Assert.Contains("output token limit", exception.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "invalid API key")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit exceeded")]
    public async Task Surfaces_http_api_errors_without_putting_the_key_in_the_url(HttpStatusCode statusCode, string message)
    {
        HttpRequestMessage? observed = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            observed = request;
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent($"{{\"error\":{{\"message\":\"{message}\"}}}}", Encoding.UTF8, "application/json")
            };
        }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => new CloudModelApiClient(http).ListModelsAsync(CloudModelProviders.OpenAI, "sensitive-key"));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Contains(message, exception.Message);
        Assert.DoesNotContain("sensitive-key", observed!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Rejects_invalid_provider_or_missing_credentials()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("No request expected.")));
        var client = new CloudModelApiClient(http);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListModelsAsync("unknown", "key"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListModelsAsync(CloudModelProviders.OpenAI, " "));
    }

    [Fact]
    public async Task Honors_cancellation_before_sending_a_model_discovery_request()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("No request expected.")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CloudModelApiClient(http).ListModelsAsync(CloudModelProviders.OpenAI, "key", cancellation.Token));
    }

    [Fact]
    public async Task Honors_cancellation_while_waiting_for_stream_data()
    {
        using var http = new HttpClient(new BlockingStreamHandler());
        using var cancellation = new CancellationTokenSource();
        var enumerator = new CloudModelApiClient(http)
            .StreamChatAsync(CloudModelProviders.OpenAI, "key", "model", [new("user", "hello")], cancellation.Token)
            .GetAsyncEnumerator();
        await using (enumerator)
        {
            var next = enumerator.MoveNextAsync().AsTask();
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(30));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        }
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<HttpResponseMessage>(cancellationToken)
                : Task.FromResult(handler(request));
    }

    private sealed class BlockingStreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingStream())
            });
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}

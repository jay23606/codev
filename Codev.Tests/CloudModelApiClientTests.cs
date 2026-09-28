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
            Assert.Equal("Bearer", observed!.Headers.Authorization!.Scheme);
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

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}

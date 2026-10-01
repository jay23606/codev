using System.Net;
using System.Text;
using System.Text.Json;

namespace Codev.Tests;

public sealed class OllamaStructuredSummaryClientTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:11434");
    private static readonly ChatMessage[] Messages = [new("system", "Summarize safely."), new("user", "Older chat")];

    [Fact]
    public async Task Sends_schema_and_accepts_a_valid_structured_summary()
    {
        using var http = new HttpClient(new StubHandler(request =>
        {
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("/api/chat", request.RequestUri!.AbsolutePath);
            Assert.Equal("object", payload.RootElement.GetProperty("format").GetProperty("type").GetString());
            Assert.Equal("summary", payload.RootElement.GetProperty("format").GetProperty("required")[0].GetString());
            Assert.False(payload.RootElement.GetProperty("stream").GetBoolean());
            return Json("""{"message":{"content":"{\"summary\":\"Preserve the key decision.\"}"}}""");
        }));

        var result = await new OllamaStructuredSummaryClient(http, Endpoint).SummarizeAsync("local-model", Messages, 8192);

        Assert.True(result.UsedStructuredOutput);
        Assert.Equal("Preserve the key decision.", result.Summary);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"summary\":\"\"}")]
    [InlineData("{\"other\":\"missing summary\"}")]
    public async Task Invalid_structured_output_retries_as_plain_text(string malformed)
    {
        var requestCount = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            requestCount++;
            if (requestCount == 1)
            {
                Assert.True(payload.RootElement.TryGetProperty("format", out _));
                return Json(JsonSerializer.Serialize(new { message = new { content = malformed } }));
            }
            Assert.False(payload.RootElement.TryGetProperty("format", out _));
            Assert.Contains("plain-text summary", payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            return Json("""{"message":{"content":"Keep the unresolved issue and next step."}}""");
        }));

        var result = await new OllamaStructuredSummaryClient(http, Endpoint).SummarizeAsync("local-model", Messages, 0);

        Assert.False(result.UsedStructuredOutput);
        Assert.Equal("Keep the unresolved issue and next step.", result.Summary);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task Unsupported_format_error_retries_without_schema()
    {
        var requestCount = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("format is not supported") };
            return Json("""{"message":{"content":"Plain summary."}}""");
        }));

        var result = await new OllamaStructuredSummaryClient(http, Endpoint).SummarizeAsync("local-model", Messages, 2048);

        Assert.False(result.UsedStructuredOutput);
        Assert.Equal("Plain summary.", result.Summary);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task Non_format_http_errors_are_not_hidden_by_a_fallback_retry()
    {
        var requestCount = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("model not found") };
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OllamaStructuredSummaryClient(http, Endpoint).SummarizeAsync("missing-model", Messages, 2048));

        Assert.Contains("model not found", error.Message);
        Assert.Equal(1, requestCount);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}

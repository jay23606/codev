using Codev;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Codev.Tests;

public sealed class McpBoundedHttpMessageHandlerTests
{
    [Fact]
    public async Task Http_json_response_is_bounded_by_total_bytes()
    {
        using var handler = new McpBoundedHttpMessageHandler(new StaticResponseHandler(
            () => new StringContent(new string('x', 65), Encoding.UTF8, "application/json")), maximumBodyBytes: 64);
        using var client = new HttpClient(handler);

        var error = await Record.ExceptionAsync(async () =>
        {
            using var response = await client.GetAsync("https://mcp.example.test/messages");
            _ = await response.Content.ReadAsStringAsync();
        });

        Assert.NotNull(error);
        Assert.Contains("MCP HTTP response exceeded", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_sse_response_limits_each_event_and_allows_multiple_small_events()
    {
        using var largeEventHandler = new McpBoundedHttpMessageHandler(new StaticResponseHandler(() =>
        {
            var content = new StringContent("data:" + new string('x', 65) + "\n\n", Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return content;
        }), maximumBodyBytes: 64);
        using var largeEventClient = new HttpClient(largeEventHandler);

        var error = await Record.ExceptionAsync(async () =>
        {
            using var response = await largeEventClient.GetAsync("https://mcp.example.test/events");
            _ = await response.Content.ReadAsStringAsync();
        });

        Assert.NotNull(error);
        Assert.Contains("MCP SSE event exceeded", error.ToString(), StringComparison.Ordinal);

        using var smallEventsHandler = new McpBoundedHttpMessageHandler(new StaticResponseHandler(() =>
        {
            var content = new StringContent("data:one\n\ndata:two\n\n", Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return content;
        }), maximumBodyBytes: 64);
        using var smallEventsClient = new HttpClient(smallEventsHandler);
        using var smallEventsResponse = await smallEventsClient.GetAsync("https://mcp.example.test/events");

        Assert.Equal("data:one\n\ndata:two\n\n", await smallEventsResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Http_finite_body_exactly_at_limit_is_accepted()
    {
        using var handler = new McpBoundedHttpMessageHandler(new StaticResponseHandler(
            () => new StringContent(new string('x', 64), Encoding.UTF8, "application/json")), maximumBodyBytes: 64);
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("https://mcp.example.test/messages");

        Assert.Equal(64, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    private sealed class StaticResponseHandler(Func<HttpContent> contentFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = contentFactory() });
    }
}

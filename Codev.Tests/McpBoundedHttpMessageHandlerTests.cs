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

    [Fact]
    public async Task Bounded_http_body_probe_honors_cancellation_after_exact_limit()
    {
        await using var source = new BlockingAtEndStream(Encoding.UTF8.GetBytes("1234"));
        await using var bounded = new BoundedTotalReadStream(source, maximumBytes: 4, knownLength: null, leaveOpen: true);
        var buffer = new byte[4];
        Assert.Equal(4, await bounded.ReadAsync(buffer));

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bounded.ReadAsync(new byte[1], timeout.Token).AsTask());
    }

    private sealed class StaticResponseHandler(Func<HttpContent> contentFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = contentFactory() });
    }

    private sealed class BlockingAtEndStream(byte[] data) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < data.Length)
            {
                var count = Math.Min(buffer.Length, data.Length - _position);
                data.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

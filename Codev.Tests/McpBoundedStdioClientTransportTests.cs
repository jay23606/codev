using Codev;
using System.Runtime.InteropServices;
using System.Text;
using ModelContextProtocol.Client;

namespace Codev.Tests;

public sealed class McpBoundedStdioClientTransportTests
{
    [Fact]
    public async Task Bounded_stream_accepts_a_frame_at_the_limit_and_resets_for_the_next_frame()
    {
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("1234\nnext\noversize\n"));
        await using var bounded = new BoundedLineReadStream(source, maximumLineBytes: 4, leaveOpen: true);
        using var reader = new StreamReader(bounded, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        Assert.Equal("1234", await reader.ReadLineAsync());
        Assert.Equal("next", await reader.ReadLineAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync());
    }

    [Fact]
    public async Task Bounded_stream_rejects_an_oversized_unterminated_frame()
    {
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("12345"));
        await using var bounded = new BoundedLineReadStream(source, maximumLineBytes: 4, leaveOpen: true);
        using var reader = new StreamReader(bounded, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync());
    }

    [Fact]
    public async Task Bounded_stream_measures_utf8_bytes_before_decoding()
    {
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("ééé\n"));
        await using var bounded = new BoundedLineReadStream(source, maximumLineBytes: 4, leaveOpen: true);
        using var reader = new StreamReader(bounded, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync());
    }

    [Fact]
    public async Task Process_transport_rejects_an_oversized_server_frame()
    {
        var options = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new StdioClientTransportOptions
            {
                Command = "powershell.exe",
                Arguments = ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write('x' * 100)"],
                Name = "oversized-frame-test"
            }
            : new StdioClientTransportOptions
            {
                Command = "/bin/sh",
                Arguments = ["-c", "printf '%100s' x"],
                Name = "oversized-frame-test"
            };
        var clientTransport = new McpBoundedStdioClientTransport(options, maxMessageLineBytes: 64);
        var session = await clientTransport.ConnectAsync();
        await using var lifetime = (IAsyncDisposable)session;

        var error = await Record.ExceptionAsync(() => session.MessageReader.Completion.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.NotNull(error);
        Assert.Contains("frame limit", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}

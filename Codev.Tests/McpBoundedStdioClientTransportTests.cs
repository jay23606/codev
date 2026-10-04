using Codev;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ModelContextProtocol.Client;

namespace Codev.Tests;

public sealed class McpBoundedStdioClientTransportTests
{
    [Fact]
    public async Task Windows_executable_arguments_preserve_literal_percent_sequences()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scriptPath = Path.Combine(Path.GetTempPath(), $"Codev-mcp-args-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "param([string]$argument) [Console]::Write($argument)");
        try
        {
            var options = new StdioClientTransportOptions
            {
                Command = "powershell.exe",
                Arguments = ["-NoProfile", "-NonInteractive", "-File", scriptPath, "%PATH%"]
            };
            var startInfo = McpBoundedStdioClientTransport.CreateStartInfo(options);
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            using var process = Process.Start(startInfo)!;
            var output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("%PATH%", output);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch (IOException) { }
        }
    }

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
    public async Task Bounded_stream_honors_cancellation_after_partial_frame()
    {
        await using var source = new BlockingAtEndStream(Encoding.UTF8.GetBytes("partial"));
        await using var bounded = new BoundedLineReadStream(source, maximumLineBytes: 64, leaveOpen: true);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bounded.ReadAsync(new byte[64], timeout.Token).AsTask());
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

        // Starting PowerShell and observing its redirected pipe can exceed ten seconds on a
        // busy Windows CI runner; retain a finite bound while allowing that startup variance.
        var error = await Record.ExceptionAsync(() => session.MessageReader.Completion.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.NotNull(error);
        Assert.Contains("frame limit", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Process_transport_delivers_a_valid_json_rpc_message()
    {
        var message = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
        var options = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new StdioClientTransportOptions
            {
                Command = "powershell.exe",
                Arguments = ["-NoProfile", "-NonInteractive", "-Command", "[Console]::WriteLine('" + message.Replace("'", "''") + "')"],
                Name = "valid-message-test"
            }
            : new StdioClientTransportOptions
            {
                Command = "/bin/sh",
                Arguments = ["-c", "printf '%s\\n' '" + message + "'"],
                Name = "valid-message-test"
            };
        var clientTransport = new McpBoundedStdioClientTransport(options, maxMessageLineBytes: 256);
        var session = await clientTransport.ConnectAsync();
        await using var lifetime = (IAsyncDisposable)session;

        var received = await session.MessageReader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(received);
    }

    [Fact]
    public async Task Disposing_process_transport_terminates_its_server_process_tree()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-mcp-stdio-lifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var processIdPath = Path.Combine(root, "server.pid");
        var safeProcessIdPath = processIdPath.Replace("'", "''", StringComparison.Ordinal);
        var options = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new StdioClientTransportOptions
            {
                Command = "powershell.exe",
                Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                    $"$child = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30') -PassThru; Set-Content -NoNewline -LiteralPath '{safeProcessIdPath}' -Value \"$PID $($child.Id)\"; Start-Sleep -Seconds 30"],
                Name = "process-tree-lifecycle-test"
            }
            : new StdioClientTransportOptions
            {
                Command = "/bin/sh",
                Arguments = ["-c", $"sleep 30 & child=$!; printf '%s %s\\n' \"$$\" \"$child\" > '{safeProcessIdPath}'; wait"],
                Name = "process-tree-lifecycle-test"
            };

        try
        {
            var clientTransport = new McpBoundedStdioClientTransport(options);
            var session = await clientTransport.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await using var lifetime = (IAsyncDisposable)session;
            var processIds = await WaitForProcessIdsAsync(processIdPath, TimeSpan.FromSeconds(10));
            Assert.Equal(2, processIds.Length);
            Assert.All(processIds, processId => Assert.True(IsProcessRunning(processId),
                "The fixture server and child should still be running before transport disposal."));

            await lifetime.DisposeAsync();

            await WaitForProcessesExitAsync(processIds, TimeSpan.FromSeconds(10));
            Assert.All(processIds, processId => Assert.False(IsProcessRunning(processId),
                "Disposing the stdio transport should terminate its server process tree."));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<int[]> WaitForProcessIdsAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    var processIds = (await File.ReadAllTextAsync(path)).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(value => int.TryParse(value, out var processId) ? processId : 0).ToArray();
                    if (processIds.Length == 2 && processIds.All(processId => processId > 0)) return processIds;
                }
                catch (IOException) { /* PowerShell briefly holds the PID file without sharing on Windows. */ }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("The MCP fixture server did not write both process IDs.");
    }

    private static async Task WaitForProcessesExitAsync(IReadOnlyList<int> processIds, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (processIds.All(processId => !IsProcessRunning(processId))) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("An MCP stdio server process or child remained alive after its transport was disposed.");
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
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

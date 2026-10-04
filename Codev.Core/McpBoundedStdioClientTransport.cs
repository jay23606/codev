using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Codev;

/// <summary>
/// Starts a stdio MCP server with the SDK stream protocol and rejects oversized frames before
/// the SDK's line reader can allocate an unbounded string.
/// </summary>
internal sealed class McpBoundedStdioClientTransport : IClientTransport
{
    public const int MaxMessageLineBytes = 8 * 1024 * 1024;
    private static readonly Regex WindowsShellCharacters = new("[&^><|]", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Whitespace = new("\\s", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly StdioClientTransportOptions _options;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly int _maxMessageLineBytes;

    public McpBoundedStdioClientTransport(StdioClientTransportOptions options, ILoggerFactory? loggerFactory = null,
        int maxMessageLineBytes = MaxMessageLineBytes)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Command)) throw new ArgumentException("An MCP server command is required.", nameof(options));
        if (maxMessageLineBytes is < 1 or > MaxMessageLineBytes) throw new ArgumentOutOfRangeException(nameof(maxMessageLineBytes));
        _loggerFactory = loggerFactory;
        _maxMessageLineBytes = maxMessageLineBytes;
        Name = options.Name ?? "stdio-" + Path.GetFileName(options.Command);
    }

    public string Name { get; }

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        Process? process = null;
        BoundedLineReadStream? boundedOutput = null;
        try
        {
            process = new Process { StartInfo = CreateStartInfo(_options), EnableRaisingEvents = true };
            if (!process.Start()) throw new IOException("The MCP server process did not start.");

            // Drain stderr without retaining it, so a verbose server cannot block on its pipe.
            var stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            boundedOutput = new BoundedLineReadStream(process.StandardOutput.BaseStream, _maxMessageLineBytes, leaveOpen: true);
            var streamTransport = new StreamClientTransport(process.StandardInput.BaseStream, boundedOutput, _loggerFactory);
            var session = await streamTransport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessOwnedTransport(session, process, boundedOutput, stderrDrain);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (boundedOutput is not null) await boundedOutput.DisposeAsync().ConfigureAwait(false);
            await StopProcessAsync(process).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            if (boundedOutput is not null) await boundedOutput.DisposeAsync().ConfigureAwait(false);
            await StopProcessAsync(process).ConfigureAwait(false);
            throw new IOException("Failed to connect to the bounded stdio MCP server transport.", ex);
        }
    }

    private static ProcessStartInfo CreateStartInfo(StdioClientTransportOptions options)
    {
        var command = options.Command;
        IList<string> arguments = options.Arguments ?? [];
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            !string.Equals(Path.GetFileName(command), "cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            arguments = ["/c", command, .. arguments];
            command = "cmd.exe";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        foreach (var argument in arguments)
        {
            var escaped = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !Whitespace.IsMatch(argument)
                ? WindowsShellCharacters.Replace(argument, static match => "^" + match.Value)
                : argument;
            startInfo.ArgumentList.Add(escaped);
        }

        if (!options.InheritEnvironmentVariables) startInfo.Environment.Clear();
        if (options.EnvironmentVariables is not null)
            foreach (var (key, value) in options.EnvironmentVariables) startInfo.Environment[key] = value;
        return startInfo;
    }

    private static async Task StopProcessAsync(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new IOException("The MCP stdio server process did not exit after process-tree termination was requested.", ex);
        }
        finally { process.Dispose(); }
    }

    private sealed class ProcessOwnedTransport(ITransport inner, Process process, BoundedLineReadStream output,
        Task stderrDrain) : ITransport, IAsyncDisposable
    {
        private int _disposed;

        public string SessionId => inner.SessionId ?? string.Empty;
        public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
            inner.SendMessageAsync(message, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await StopProcessAsync(process).ConfigureAwait(false); }
            finally
            {
                try
                {
                    if (inner is IAsyncDisposable asyncDisposable)
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    else if (inner is IDisposable disposable)
                        disposable.Dispose();
                }
                finally
                {
                    await output.DisposeAsync().ConfigureAwait(false);
                    try { await stderrDrain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                }
            }
        }
    }
}

/// <summary>Limits each LF-terminated UTF-8 transport frame before a line reader can buffer it.</summary>
internal sealed class BoundedLineReadStream(Stream inner, int maximumLineBytes, bool leaveOpen) : Stream
{
    private readonly byte[] _buffer = new byte[4096];
    private int _bufferOffset;
    private int _bufferCount;
    private int _lineBytes;
    private int _disposed;

    public override bool CanRead => Volatile.Read(ref _disposed) == 0 && inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (destination.IsEmpty) return 0;
        var written = 0;
        while (written < destination.Length)
        {
            if (!EnsureBuffered()) return written;
            var value = _buffer[_bufferOffset++];
            if (value == (byte)'\n')
            {
                destination[written++] = value;
                _lineBytes = 0;
                return written;
            }
            if (++_lineBytes > maximumLineBytes)
                throw new InvalidDataException($"An MCP stdio message exceeded the {maximumLineBytes}-byte frame limit.");
            destination[written++] = value;
        }
        return written;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.IsEmpty) return 0;
        var written = 0;
        while (written < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_bufferOffset >= _bufferCount)
            {
                _bufferCount = await inner.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _bufferOffset = 0;
                if (_bufferCount == 0) return written;
            }
            var value = _buffer[_bufferOffset++];
            if (value == (byte)'\n')
            {
                destination.Span[written++] = value;
                _lineBytes = 0;
                return written;
            }
            if (++_lineBytes > maximumLineBytes)
                throw new InvalidDataException($"An MCP stdio message exceeded the {maximumLineBytes}-byte frame limit.");
            destination.Span[written++] = value;
        }
        return written;
    }

    private bool EnsureBuffered()
    {
        if (_bufferOffset < _bufferCount) return true;
        _bufferCount = inner.Read(_buffer, 0, _buffer.Length);
        _bufferOffset = 0;
        return _bufferCount > 0;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0 && !leaveOpen) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && !leaveOpen) await inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

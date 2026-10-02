using System.Net;

namespace Codev;

/// <summary>Caps finite MCP HTTP payloads and individual SSE events before SDK deserialization.</summary>
internal sealed class McpBoundedHttpMessageHandler : DelegatingHandler
{
    public const int MaxBodyBytes = 16 * 1024 * 1024;
    private const string ServerSentEvents = "text/event-stream";
    private readonly int _maximumBodyBytes;

    public McpBoundedHttpMessageHandler(HttpMessageHandler innerHandler, int maximumBodyBytes = MaxBodyBytes) : base(innerHandler)
    {
        if (maximumBodyBytes is < 1 or > MaxBodyBytes) throw new ArgumentOutOfRangeException(nameof(maximumBodyBytes));
        _maximumBodyBytes = maximumBodyBytes;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Content is not { } content) return response;
        var isEventStream = string.Equals(content.Headers.ContentType?.MediaType, ServerSentEvents, StringComparison.OrdinalIgnoreCase);
        response.Content = new BoundedHttpContent(content, _maximumBodyBytes, isEventStream);
        return response;
    }

    private sealed class BoundedHttpContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly int _limit;
        private readonly bool _isEventStream;
        private readonly long? _knownLength;

        public BoundedHttpContent(HttpContent inner, int limit, bool isEventStream)
        {
            _inner = inner;
            _limit = limit;
            _isEventStream = isEventStream;
            _knownLength = inner.Headers.ContentLength;
            foreach (var header in inner.Headers)
                if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            CopyAsync(stream, cancellationToken);

        private async Task CopyAsync(Stream destination, CancellationToken cancellationToken)
        {
            await using var source = await CreateLimitedStreamAsync(cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
            CreateLimitedStream(_inner.ReadAsStream(cancellationToken));

        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            CreateLimitedStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false));

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            CreateLimitedStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));

        private async Task<Stream> CreateLimitedStreamAsync(CancellationToken cancellationToken) =>
            CreateLimitedStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));

        private Stream CreateLimitedStream(Stream stream) => _isEventStream
            ? new BoundedSseEventReadStream(stream, _limit, leaveOpen: true)
            : new BoundedTotalReadStream(stream, _limit, _knownLength, leaveOpen: true);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

internal sealed class BoundedTotalReadStream(Stream inner, long maximumBytes, long? knownLength, bool leaveOpen) : Stream
{
    private long _bytesRead;
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
        ThrowIfKnownOversize();
        if (_bytesRead >= maximumBytes)
        {
            Span<byte> probe = stackalloc byte[1];
            if (inner.Read(probe) == 0) return 0;
            throw Oversize();
        }
        var count = (int)Math.Min(destination.Length, maximumBytes - _bytesRead);
        var read = inner.Read(destination[..count]);
        _bytesRead += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (destination.IsEmpty) return 0;
        ThrowIfKnownOversize();
        if (_bytesRead >= maximumBytes)
        {
            var probe = new byte[1];
            if (await inner.ReadAsync(probe, cancellationToken).ConfigureAwait(false) == 0) return 0;
            throw Oversize();
        }
        var count = (int)Math.Min(destination.Length, maximumBytes - _bytesRead);
        var read = await inner.ReadAsync(destination[..count], cancellationToken).ConfigureAwait(false);
        _bytesRead += read;
        return read;
    }

    private void ThrowIfKnownOversize()
    {
        if (knownLength > maximumBytes) throw Oversize();
    }

    private InvalidDataException Oversize() => new($"An MCP HTTP response exceeded its {maximumBytes}-byte limit.");

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

internal sealed class BoundedSseEventReadStream(Stream inner, int maximumEventBytes, bool leaveOpen) : Stream
{
    private readonly byte[] _buffer = new byte[4096];
    private int _bufferOffset;
    private int _bufferCount;
    private int _eventBytes;
    private int _lineBytes;
    private bool _lastLineByteWasCarriageReturn;
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
            if (_bufferOffset >= _bufferCount)
            {
                _bufferCount = inner.Read(_buffer, 0, _buffer.Length);
                _bufferOffset = 0;
                if (_bufferCount == 0) return written;
            }
            var value = _buffer[_bufferOffset++];
            if (value == (byte)'\n')
            {
                var blankLine = _lineBytes == 0 || (_lineBytes == 1 && _lastLineByteWasCarriageReturn);
                if (blankLine) _eventBytes = 0;
                else if (++_eventBytes > maximumEventBytes) throw Oversize();
                _lineBytes = 0;
                _lastLineByteWasCarriageReturn = false;
                destination[written++] = value;
                return written;
            }
            _lineBytes++;
            _lastLineByteWasCarriageReturn = value == (byte)'\r';
            if (++_eventBytes > maximumEventBytes) throw Oversize();
            destination[written++] = value;
        }
        return written;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (destination.IsEmpty) return 0;
        var written = 0;
        while (written < destination.Length)
        {
            if (_bufferOffset >= _bufferCount)
            {
                _bufferCount = await inner.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _bufferOffset = 0;
                if (_bufferCount == 0) return written;
            }
            var value = _buffer[_bufferOffset++];
            if (value == (byte)'\n')
            {
                var blankLine = _lineBytes == 0 || (_lineBytes == 1 && _lastLineByteWasCarriageReturn);
                if (blankLine) _eventBytes = 0;
                else if (++_eventBytes > maximumEventBytes) throw Oversize();
                _lineBytes = 0;
                _lastLineByteWasCarriageReturn = false;
                destination.Span[written++] = value;
                return written;
            }
            _lineBytes++;
            _lastLineByteWasCarriageReturn = value == (byte)'\r';
            if (++_eventBytes > maximumEventBytes) throw Oversize();
            destination.Span[written++] = value;
        }
        return written;
    }

    private InvalidDataException Oversize() => new($"An MCP SSE event exceeded its {maximumEventBytes}-byte limit.");
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

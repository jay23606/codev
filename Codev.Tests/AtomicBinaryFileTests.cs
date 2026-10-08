using Codev;

namespace Codev.Tests;

public sealed class AtomicBinaryFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-atomic-binary", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Replaces_existing_binary_after_a_complete_write()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "index.idx");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        await AtomicBinaryFile.WriteAsync(path, async (stream, cancellationToken) =>
            await stream.WriteAsync(new byte[] { 4, 5, 6 }, cancellationToken));

        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Preserves_the_previous_binary_when_serialization_fails()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "index.idx");
        var previous = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(path, previous);

        await Assert.ThrowsAsync<IOException>(() => AtomicBinaryFile.WriteAsync(path, async (stream, cancellationToken) =>
        {
            await stream.WriteAsync(new byte[] { 9, 9, 9 }, cancellationToken);
            throw new IOException("injected serialization failure");
        }));

        Assert.Equal(previous, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}

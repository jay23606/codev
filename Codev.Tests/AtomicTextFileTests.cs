using System.IO;

namespace Codev.Tests;

public sealed class AtomicTextFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-atomic-store", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Replaces_existing_text_with_a_fully_written_file()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "conversations.json");
        await File.WriteAllTextAsync(path, "old store");

        await AtomicTextFile.WriteAsync(path, "new store");

        Assert.Equal("new store", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Cleans_up_the_temporary_file_when_the_destination_cannot_be_replaced()
    {
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, "destination.json");
        Directory.CreateDirectory(destination);

        var error = await Record.ExceptionAsync(() => AtomicTextFile.WriteAsync(destination, "content"));
        Assert.NotNull(error);
        Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected exception type: {error.GetType().Name}");

        Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}

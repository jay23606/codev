using System.IO;

namespace Codev.Tests;

public sealed class ConversationStoreRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-store-recovery-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void PreserveUnreadableStore_copies_original_bytes_without_changing_source()
    {
        Directory.CreateDirectory(_root);
        var store = Path.Combine(_root, "conversations.json");
        const string invalidJson = "[not valid history";
        File.WriteAllText(store, invalidJson);

        var backup = ConversationStoreRecovery.PreserveUnreadableStore(store);

        Assert.True(File.Exists(store));
        Assert.NotEqual(store, backup);
        Assert.Equal(invalidJson, File.ReadAllText(backup));
        Assert.StartsWith("conversations.corrupt-", Path.GetFileName(backup), StringComparison.Ordinal);
        Assert.Equal(backup, ConversationStoreRecovery.PreserveUnreadableStore(store));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}

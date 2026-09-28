using System.IO;

namespace Codev.Tests;

public sealed class LocalJsonStoreRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-store-recovery-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void PreserveUnreadableStore_copies_original_bytes_without_changing_source_or_duplicating_backups()
    {
        Directory.CreateDirectory(_root);
        var store = Path.Combine(_root, "conversations.json");
        const string invalidJson = "[not valid history";
        File.WriteAllText(store, invalidJson);

        var backup = LocalJsonStoreRecovery.PreserveUnreadableStore(store);

        Assert.True(File.Exists(store));
        Assert.NotEqual(store, backup);
        Assert.Equal(invalidJson, File.ReadAllText(backup));
        Assert.StartsWith("conversations.corrupt-", Path.GetFileName(backup), StringComparison.Ordinal);
        Assert.Equal(backup, LocalJsonStoreRecovery.PreserveUnreadableStore(store));
    }

    [Fact]
    public void Backup_name_uses_the_store_name_for_other_local_json_files()
    {
        Directory.CreateDirectory(_root);
        var projects = Path.Combine(_root, "projects.json");
        File.WriteAllText(projects, "broken");

        var backup = LocalJsonStoreRecovery.PreserveUnreadableStore(projects);

        Assert.StartsWith("projects.corrupt-", Path.GetFileName(backup), StringComparison.Ordinal);
        Assert.Equal("broken", File.ReadAllText(backup));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}

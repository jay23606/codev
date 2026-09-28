using System.IO;

namespace Codev;

public static class ConversationStoreRecovery
{
    public static string PreserveUnreadableStore(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        var fullPath = Path.GetFullPath(storePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Conversation history store not found.", fullPath);

        var directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("A store directory is required.", nameof(storePath));
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        var sourceInfo = new FileInfo(fullPath);
        var existingBackup = Directory.EnumerateFiles(directory, $"{name}.corrupt-*{extension}")
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .FirstOrDefault(info => info.Exists && info.Length == sourceInfo.Length && info.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc);
        if (existingBackup is not null) return existingBackup.FullName;

        var backupPath = Path.Combine(directory, $"{name}.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}{extension}");
        File.Copy(fullPath, backupPath, overwrite: false);
        return backupPath;
    }
}

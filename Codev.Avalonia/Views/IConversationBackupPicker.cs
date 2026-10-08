using Avalonia.Platform.Storage;

namespace Codev.Avalonia.Views;

/// <summary>Lets the backup UI use the platform picker or a deterministic test picker.</summary>
public interface IConversationBackupPicker
{
    bool CanOpen { get; }
    bool CanSave { get; }
    Task<IConversationBackupFile?> SaveFilePickerAsync(FilePickerSaveOptions options);
    Task<IReadOnlyList<IConversationBackupFile>> OpenFilePickerAsync(FilePickerOpenOptions options);
}

public interface IConversationBackupFile
{
    string Name { get; }
    Task<Stream> OpenReadAsync();
    Task<Stream> OpenWriteAsync();
}

internal sealed class AvaloniaConversationBackupFile(IStorageFile file) : IConversationBackupFile
{
    public string Name => file.Name;
    public Task<Stream> OpenReadAsync() => file.OpenReadAsync();
    public Task<Stream> OpenWriteAsync() => file.OpenWriteAsync();
}

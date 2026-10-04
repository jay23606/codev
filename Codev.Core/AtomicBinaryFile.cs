namespace Codev;

/// <summary>Writes binary data through a same-directory temporary file and replaces the destination after a complete flush.</summary>
public static class AtomicBinaryFile
{
    public static async Task WriteAsync(string path, Func<Stream, CancellationToken, Task> writeContents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeContents);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("A destination directory is required.", nameof(path));
        const UnixFileMode privateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode privateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else
        {
            Directory.CreateDirectory(directory, privateDirectoryMode);
            // Existing directories retain their old permissions when CreateDirectory is called.
            // Tighten them too, so data written before this protection is no longer exposed.
            File.SetUnixFileMode(directory, privateDirectoryMode);
        }
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                UnixCreateMode = OperatingSystem.IsWindows() ? null : privateFileMode
            }))
            {
                await writeContents(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }
}

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Codev;

/// <summary>Windows-only safe-handle reads for read-only auto-approval. Fails closed on hard links and path escapes.</summary>
internal static class ReadOnlyFileHandleReader
{
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    public static bool CanRead(string path, string projectRoot, IReadOnlyList<string>? exclusions, int maxBytes)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var rootHandle = OpenHandle(projectRoot, FileReadAttributes, ShareRead | ShareWrite | ShareDelete, directory: true);
            using var fileHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);
            return IsSafeHandle(fileHandle, rootHandle, projectRoot, exclusions, maxBytes, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static string ReadText(string path, string projectRoot, IReadOnlyList<string>? exclusions, int maxBytes)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Automatic file reads require Windows hard-link checks.");
        using var rootHandle = OpenHandle(projectRoot, FileReadAttributes, ShareRead | ShareWrite | ShareDelete, directory: true);
        using var fileHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);
        if (!IsSafeHandle(fileHandle, rootHandle, projectRoot, exclusions, maxBytes, out var length))
            throw new IOException("The file is linked, outside the project, excluded, or otherwise unsafe; inspection stopped.");

        var bytes = new byte[length];
        var offset = 0L;
        while (offset < length)
        {
            var count = RandomAccess.Read(fileHandle, bytes.AsSpan((int)offset), offset);
            if (count == 0) break;
            offset += count;
        }
        if (offset != length) Array.Resize(ref bytes, (int)offset);
        return Encoding.UTF8.GetString(bytes);
    }

    private static bool IsSafeHandle(SafeFileHandle file, SafeFileHandle root, string projectRoot,
        IReadOnlyList<string>? exclusions, int maxBytes, out int length)
    {
        length = 0;
        if (!GetFileInformationByHandle(file, out var fileInfo) ||
            !GetFileInformationByHandle(root, out _) ||
            fileInfo.NumberOfLinks != 1 ||
            (fileInfo.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            (fileInfo.FileAttributes & (uint)FileAttributes.Directory) != 0)
            return false;

        var fileSize = ((long)fileInfo.FileSizeHigh << 32) | fileInfo.FileSizeLow;
        if (fileSize < 0 || fileSize > maxBytes) return false;
        var finalRoot = GetFinalPath(root);
        var finalFile = GetFinalPath(file);
        if (finalRoot is null || finalFile is null || !IsWithinRoot(finalRoot, finalFile)) return false;

        var relative = Path.GetRelativePath(ToRegularPath(finalRoot), ToRegularPath(finalFile));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => WorkspaceFileService.IsSensitiveFileName(segment) || WorkspaceFileService.IsIgnoredDirectory(segment))) return false;
        var workspace = new WorkspaceFileService(projectRoot, exclusions);
        if (!workspace.IsSupportedContextFile(relative) || workspace.IsContextExcluded(relative)) return false;

        length = (int)fileSize;
        return true;
    }

    private static SafeFileHandle OpenHandle(string path, uint access, uint share, bool directory)
    {
        var handle = CreateFileW(path, access, share, IntPtr.Zero, OpenExisting, directory ? BackupSemantics : 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException("Could not open a path handle for read-only inspection.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }
        return handle;
    }

    private static string? GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        return length is 0 or >= 1024 ? null : buffer.ToString();
    }

    private static string ToRegularPath(string path)
    {
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) return "\\\\" + path[8..];
        return path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static bool IsWithinRoot(string root, string path)
    {
        root = root.TrimEnd('\\', '/');
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);
}

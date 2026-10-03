using System.Runtime.InteropServices;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace Codev;

/// <summary>Reads file hard-link counts through operating-system APIs where available.</summary>
public static class FileHardLinkInspector
{
    private const uint LinuxStatxNlink = 0x00000004;
    private const uint LinuxStatxMode = 0x00000002;
    private const int AtCurrentWorkingDirectory = -100;
    private const int AtSymlinkNoFollow = 0x0100;
    private const int AtEmptyPath = 0x1000;

    public static bool IsSupportedPlatform => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>Returns the file's hard-link count, or null when the platform/API cannot report it.</summary>
    public static int? TryGetLinkCount(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (OperatingSystem.IsWindows()) return TryGetWindowsLinkCount(path);
            if (OperatingSystem.IsLinux()) return TryGetLinuxLinkCount(path);
            if (OperatingSystem.IsMacOS()) return TryGetMacOsLinkCount(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
                                      DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or Win32Exception or InvalidOperationException or OverflowException)
        {
            return null;
        }
        return null;
    }

    /// <summary>Prevents atomic replacement when a hard link could not be ruled out.</summary>
    public static void EnsureSafeToReplaceExistingFile(string path, string displayPath)
    {
        if (!IsSupportedPlatform)
            throw new InvalidOperationException($"The hard-link count for '{displayPath}' cannot be checked on this platform. Nothing was overwritten.");

        var linkCount = TryGetLinkCount(path);
        if (linkCount is null)
            throw new InvalidOperationException($"The hard-link count for '{displayPath}' could not be verified. Nothing was overwritten.");
        if (linkCount > 1)
            throw new InvalidOperationException($"'{displayPath}' has {linkCount} hard links. Nothing was overwritten; restoring it could break its shared-file relationship.");
    }

    /// <summary>
    /// Opens a regular file without following a final symbolic link, checks the link count on
    /// that same open file, and returns the handle that callers must use for the read.
    /// </summary>
    public static FileStream OpenSingleLinkReadStream(string path, string displayPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayPath);
        if (!IsSupportedPlatform)
            throw new UnauthorizedAccessException($"The hard-link status for '{displayPath}' cannot be checked on this platform, so it was not read.");

        SafeFileHandle? handle = null;
        try
        {
            handle = OpenReadHandle(path);
            var linkCount = TryGetLinkCount(handle, out var isRegularFile);
            if (!isRegularFile)
                throw new UnauthorizedAccessException($"'{displayPath}' is not a regular file, so it was not read.");
            if (linkCount is null)
                throw new UnauthorizedAccessException($"The hard-link status for '{displayPath}' could not be verified, so it was not read.");
            if (linkCount > 1)
                throw new UnauthorizedAccessException($"'{displayPath}' is shared through a hard link, so it was not read.");

            var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            handle = null; // FileStream now owns the verified handle.
            return stream;
        }
        finally { handle?.Dispose(); }
    }

    private static SafeFileHandle OpenReadHandle(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFileWindows(path, GenericRead, ShareRead | ShareWrite | ShareDelete,
                IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new IOException("The project file could not be opened without following a reparse point.", new Win32Exception(error));
            }
            return handle;
        }

        var flags = OperatingSystem.IsLinux()
            ? LinuxOpenReadOnly | LinuxOpenNonBlock | LinuxOpenCloseOnExec | LinuxOpenNoFollow
            : MacOpenReadOnly | MacOpenNonBlock | MacOpenCloseOnExec | MacOpenNoFollow;
        var descriptor = OpenUnix(path, flags);
        if (descriptor < 0)
            throw new IOException("The project file could not be opened without following a symbolic link.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static int? TryGetLinkCount(SafeFileHandle handle, out bool isRegularFile)
    {
        isRegularFile = false;
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var info)) return null;
            isRegularFile = (info.Attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) == 0;
            return checked((int)info.NumberOfLinks);
        }
        if (OperatingSystem.IsLinux())
        {
            var descriptor = handle.DangerousGetHandle().ToInt32();
            if (Statx(descriptor, string.Empty, AtEmptyPath, LinuxStatxNlink | LinuxStatxMode, out var info) != 0 ||
                (info.Mask & (LinuxStatxNlink | LinuxStatxMode)) != (LinuxStatxNlink | LinuxStatxMode)) return null;
            isRegularFile = (info.Mode & UnixFileTypeMask) == UnixRegularFile;
            return checked((int)info.LinkCount);
        }
        if (OperatingSystem.IsMacOS())
        {
            var descriptor = handle.DangerousGetHandle().ToInt32();
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? FStatMacOsInode64(descriptor, out var info)
                : FStatMacOs(descriptor, out info);
            if (result != 0) return null;
            isRegularFile = (info.Mode & UnixFileTypeMask) == UnixRegularFile;
            return info.LinkCount >= 1 ? info.LinkCount : null;
        }
        return null;
    }

    private static int? TryGetWindowsLinkCount(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(handle, out var info) ? checked((int)info.NumberOfLinks) : null;
    }

    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int LinuxOpenReadOnly = 0;
    private const int LinuxOpenNonBlock = 0x00000800;
    private const int LinuxOpenCloseOnExec = 0x00080000;
    private const int LinuxOpenNoFollow = 0x00020000;
    private const int MacOpenReadOnly = 0;
    private const int MacOpenNonBlock = 0x00000004;
    private const int MacOpenNoFollow = 0x00000100;
    private const int MacOpenCloseOnExec = 0x01000000;
    private const ushort UnixFileTypeMask = 0xF000;
    private const ushort UnixRegularFile = 0x8000;

    private static int? TryGetLinuxLinkCount(string path)
    {
        if (Statx(AtCurrentWorkingDirectory, path, AtSymlinkNoFollow, LinuxStatxNlink, out var info) != 0 ||
            (info.Mask & LinuxStatxNlink) == 0) return null;
        return checked((int)info.LinkCount);
    }

    private static int? TryGetMacOsLinkCount(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? LStatMacOsInode64(fullPath, out var info)
            : LStatMacOs(fullPath, out info);
        if (result != 0) return null;
        return info.LinkCount >= 1 ? info.LinkCount : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileWindows(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatx information);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStatMacOs(int fileDescriptor, out MacStat information);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int FStatMacOsInode64(int fileDescriptor, out MacStat information);

    // Darwin's 64-bit-inode struct stat declares dev_t (32 bits), mode_t (16 bits),
    // then nlink_t (16 bits). On Intel macOS the symbol is versioned; Apple Silicon
    // exposes the 64-bit-inode ABI as the default lstat entry point.
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStatMacOs([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out MacStat information);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int LStatMacOsInode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out MacStat information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint LinkCount;
        [FieldOffset(28)] public ushort Mode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct MacStat
    {
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(6)] public ushort LinkCount;
    }
}

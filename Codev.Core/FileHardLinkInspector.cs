using System.Runtime.InteropServices;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace Codev;

/// <summary>Reads file hard-link counts through operating-system APIs where available.</summary>
public static class FileHardLinkInspector
{
    private const uint LinuxStatxNlink = 0x00000004;
    private const int AtCurrentWorkingDirectory = -100;
    private const int AtSymlinkNoFollow = 0x0100;

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

    /// <summary>Fails closed when a file could be shared with a path outside its trusted directory.</summary>
    public static void EnsureSingleLinkFile(string path, string displayPath)
    {
        if (!IsSupportedPlatform)
            throw new UnauthorizedAccessException($"The hard-link status for '{displayPath}' cannot be checked on this platform, so it was not read.");

        var linkCount = TryGetLinkCount(path);
        if (linkCount is null)
            throw new UnauthorizedAccessException($"The hard-link status for '{displayPath}' could not be verified, so it was not read.");
        if (linkCount > 1)
            throw new UnauthorizedAccessException($"'{displayPath}' is shared through a hard link, so it was not read.");
    }

    private static int? TryGetWindowsLinkCount(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(handle, out var info) ? checked((int)info.NumberOfLinks) : null;
    }

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

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatx information);

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
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct MacStat
    {
        [FieldOffset(6)] public ushort LinkCount;
    }
}

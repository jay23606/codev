using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Globalization;
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
        // macOS stat(1) exposes st_nlink as %l. Use ArgumentList rather than a shell,
        // and keep the process bounded so a platform/tooling failure fails closed.
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/stat",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add("%l");
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(path));
        if (!process.Start()) return null;

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(milliseconds: 2000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { process.WaitForExit(milliseconds: 1000); } catch (InvalidOperationException) { }
            return null;
        }

        // Drain both redirected streams before disposing the process. stat's successful
        // output is one short integer, while errors are ignored and never exposed.
        _ = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0) return null;
        var output = outputTask.GetAwaiter().GetResult().Trim();
        return int.TryParse(output, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= 1
            ? count
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatx information);

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
}

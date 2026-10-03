using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace Codev;

/// <summary>Performs workspace mutations relative to a verified parent-directory handle.</summary>
internal static class WorkspaceFileSystemOperations
{
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadAttributes = 0x00000080;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint Synchronize = 0x00100000;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const int FileRenameInformation = 10;
    private const int FileDispositionInformation = 13;
    private const int FileDirectoryInformation = 1;
    private const int StatusNoMoreFiles = unchecked((int)0x80000006);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);
    private const int LinuxOpenWriteOnly = 1;
    private const int LinuxOpenNonBlock = 0x00000800;
    private const int LinuxOpenCreate = 0x40;
    private const int LinuxOpenExclusive = 0x80;
    private const int LinuxOpenCloseOnExec = 0x00080000;
    private const int LinuxOpenNoFollow = 0x00020000;
    private const int MacOpenWriteOnly = 1;
    private const int MacOpenNonBlock = 0x00000004;
    private const int MacOpenCreate = 0x00000200;
    private const int MacOpenExclusive = 0x00000800;
    private const int MacOpenNoFollow = 0x00000100;
    private const int MacOpenCloseOnExec = 0x01000000;
    private const uint DefaultMacFileMode = 0x180; // 0600: keep newly-created source private and writable by its owner.

    internal static async Task WriteAtomicallyAsync(string root, string destinationPath, byte[] content, bool overwrite, CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? validateBeforeCommit = null)
    {
        var (directory, fileName) = GetDestination(root, destinationPath);
        using var parent = FileHardLinkInspector.OpenDirectoryBeneathRoot(root, directory, forMutation: true);
        var temporaryName = $".codev-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}.tmp";
        if (OperatingSystem.IsWindows())
            await WriteWindowsAsync(root, parent, temporaryName, fileName, content, overwrite, cancellationToken, validateBeforeCommit);
        else
            await WriteUnixAsync(parent, temporaryName, fileName, content, overwrite, cancellationToken, validateBeforeCommit);
    }

    internal static void DeleteFile(string root, string destinationPath)
    {
        var (directory, fileName) = GetDestination(root, destinationPath);
        using var parent = FileHardLinkInspector.OpenDirectoryBeneathRoot(root, directory, forMutation: true);
        if (OperatingSystem.IsWindows())
        {
            using var handle = OpenWindowsRelativeFile(parent, fileName, DeleteAccess | ReadAttributes | Synchronize,
                FileOpen, FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint);
            EnsureHandleIsChildOfParent(handle, parent, fileName, root);
            SetDisposition(handle);
            return;
        }

        if (UnlinkAt(parent.DangerousGetHandle().ToInt32(), fileName, 0) != 0)
            throw new IOException("The project file could not be removed relative to its verified folder.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    internal static IReadOnlyList<WorkspaceDirectoryEntry> EnumerateDirectory(string root, string relativeDirectory, int maxEntries = 10_000)
    {
        var maximum = Math.Max(1, maxEntries);
        using var directory = FileHardLinkInspector.OpenDirectoryBeneathRoot(root, relativeDirectory, forEnumeration: true);
        if (OperatingSystem.IsWindows()) return EnumerateWindows(directory, maximum);
        if (OperatingSystem.IsMacOS()) return EnumerateMacOs(directory, maximum);

        var handlePath = $"/proc/self/fd/{directory.DangerousGetHandle().ToInt32()}";
        var entries = new List<WorkspaceDirectoryEntry>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(handlePath))
        {
            if (entries.Count >= maximum) break;
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name) || name is "." or "..") continue;
            entries.Add(new WorkspaceDirectoryEntry(name, File.GetAttributes(entry)));
        }
        return entries;
    }

    private static IReadOnlyList<WorkspaceDirectoryEntry> EnumerateMacOs(SafeFileHandle directory, int maximum)
    {
        const int recordLengthOffset = 16;
        const int nameLengthOffset = 18;
        const int typeOffset = 20;
        const int nameOffset = 21;
        const int maxNameLength = 1023; // 64-bit Darwin dirent uses MAXPATHLEN, not MAXNAMLEN.
        const byte directoryType = 4;
        const byte symbolicLinkType = 10;
        const byte whiteoutType = 14;
        var duplicate = Dup(directory.DangerousGetHandle().ToInt32());
        if (duplicate < 0) throw new IOException("The project folder could not be safely enumerated.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var stream = OpenDirectoryStream(duplicate);
        if (stream == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            _ = Close(duplicate);
            throw new IOException("The project folder could not be safely enumerated.", new Win32Exception(error));
        }

        var results = new List<WorkspaceDirectoryEntry>();
        try
        {
            while (results.Count < maximum)
            {
                // readdir returns null for both EOF and errors; clear errno first so an
                // enumeration error cannot silently look like a successfully complete list.
                Marshal.SetLastPInvokeError(0);
                var entry = ReadDirectoryForCurrentMacAbi(stream);
                if (entry == IntPtr.Zero)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 0)
                        throw new IOException("The project folder could not be safely enumerated.", new Win32Exception(error));
                    break;
                }
                var recordLength = unchecked((ushort)Marshal.ReadInt16(entry, recordLengthOffset));
                var nameLength = unchecked((ushort)Marshal.ReadInt16(entry, nameLengthOffset));
                if (recordLength < nameOffset + 1 || nameLength == 0 || nameLength > maxNameLength || nameOffset + nameLength >= recordLength)
                    throw new IOException("The project folder returned an invalid directory entry.");
                var nameBytes = new byte[nameLength];
                Marshal.Copy(IntPtr.Add(entry, nameOffset), nameBytes, 0, nameBytes.Length);
                var name = Encoding.UTF8.GetString(nameBytes);
                if (name is "." or "..") continue;
                var type = Marshal.ReadByte(entry, typeOffset);
                var attributes = type switch
                {
                    directoryType => FileAttributes.Directory,
                    symbolicLinkType or whiteoutType => FileAttributes.ReparsePoint,
                    0 => FileAttributes.ReparsePoint, // Unknown entries fail closed and are not traversed or exposed.
                    _ => FileAttributes.Normal
                };
                results.Add(new WorkspaceDirectoryEntry(name, attributes));
            }
        }
        finally { _ = CloseDirectoryStream(stream); }
        return results;
    }

    private static IReadOnlyList<WorkspaceDirectoryEntry> EnumerateWindows(SafeFileHandle directory, int maximum)
    {
        const int bufferSize = 64 * 1024;
        const int fileAttributesOffset = 56;
        const int fileNameLengthOffset = 60;
        const int fileNameOffset = 64;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        var results = new List<WorkspaceDirectoryEntry>();
        var restart = true;
        try
        {
            while (true)
            {
                var status = NtQueryDirectoryFile(directory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    out var ioStatus, buffer, bufferSize, FileDirectoryInformation, false, IntPtr.Zero, restart);
                restart = false;
                if (status == StatusNoMoreFiles) break;
                if (status < 0 && status != StatusBufferOverflow)
                    throw NtStatusIOException("The project folder could not be safely enumerated.", status);

                var available = ioStatus.Information.ToInt64();
                if (available < 0 || available > bufferSize)
                    throw new IOException("The project folder returned an invalid directory buffer length.");
                if (available == 0) break;
                var offset = 0;
                while (offset + fileNameOffset <= available)
                {
                    var current = IntPtr.Add(buffer, offset);
                    var next = Marshal.ReadInt32(current, 0);
                    var nameLength = Marshal.ReadInt32(current, fileNameLengthOffset);
                    if (nameLength < 0 || (nameLength & 1) != 0 || offset + fileNameOffset + nameLength > available)
                        throw new IOException("The project folder returned an invalid directory entry.");
                    var name = Marshal.PtrToStringUni(IntPtr.Add(current, fileNameOffset), nameLength / sizeof(char)) ?? "";
                    var attributes = (FileAttributes)Marshal.ReadInt32(current, fileAttributesOffset);
                    if (!string.IsNullOrEmpty(name) && name is not "." and not "..")
                    {
                        results.Add(new WorkspaceDirectoryEntry(name, attributes));
                        if (results.Count >= maximum) return results;
                    }
                    if (next == 0) break;
                    if (next < fileNameOffset || offset + next > available)
                        throw new IOException("The project folder returned an invalid directory record.");
                    offset += next;
                }
                if (status == StatusBufferOverflow) continue;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return results;
    }

    private static IntPtr ReadDirectoryForCurrentMacAbi(IntPtr stream) =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 ? ReadDirectoryInode64(stream) : ReadDirectory(stream);

    private static async Task WriteUnixAsync(SafeFileHandle parent, string temporaryName, string fileName, byte[] content, bool overwrite, CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? validateBeforeCommit)
    {
        var parentDescriptor = parent.DangerousGetHandle().ToInt32();
        var targetMode = GetUnixTargetMode(parentDescriptor, fileName, overwrite);
        var flags = OperatingSystem.IsLinux()
            ? LinuxOpenWriteOnly | LinuxOpenCreate | LinuxOpenExclusive | LinuxOpenCloseOnExec | LinuxOpenNoFollow
            : MacOpenWriteOnly | MacOpenCreate | MacOpenExclusive | MacOpenCloseOnExec | MacOpenNoFollow;
        var descriptor = OpenAtCreate(parentDescriptor, temporaryName, flags, OperatingSystem.IsLinux() ? 0x1B6u : 0u);
        if (descriptor < 0) throw new IOException("A temporary project file could not be created safely.", new Win32Exception(Marshal.GetLastPInvokeError()));
        using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        var temporaryExists = true;
        try
        {
            if (targetMode is { } mode && Fchmod(descriptor, mode) != 0)
                throw new IOException("The temporary project file permissions could not be set safely.", new Win32Exception(Marshal.GetLastPInvokeError()));
            using (var stream = new FileStream(handle, FileAccess.Write, 4096, isAsync: false))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
                if (validateBeforeCommit is not null && !await validateBeforeCommit(cancellationToken))
                    throw new IOException("The project file changed during review. Nothing was overwritten; inspect it again.");
                var directoryFd = parent.DangerousGetHandle().ToInt32();
                if (overwrite)
                {
                    if (RenameAt(directoryFd, temporaryName, directoryFd, fileName) != 0)
                        throw new IOException("The project file could not be atomically replaced.", new Win32Exception(Marshal.GetLastPInvokeError()));
                }
                else
                {
                    if (LinkAt(directoryFd, temporaryName, directoryFd, fileName, 0) != 0)
                        throw new IOException("A project file already exists at this path or could not be created.", new Win32Exception(Marshal.GetLastPInvokeError()));
                    if (UnlinkAt(directoryFd, temporaryName, 0) != 0)
                        throw new IOException("The new project file was created, but its temporary name could not be removed.", new Win32Exception(Marshal.GetLastPInvokeError()));
                }
                temporaryExists = false;
            }
        }
        finally
        {
            if (temporaryExists) _ = UnlinkAt(parent.DangerousGetHandle().ToInt32(), temporaryName, 0);
        }
    }

    private static uint? GetUnixTargetMode(int parentDescriptor, string fileName, bool overwrite)
    {
        var flags = OperatingSystem.IsLinux()
            ? LinuxOpenCloseOnExec | LinuxOpenNoFollow | LinuxOpenNonBlock
            : MacOpenCloseOnExec | MacOpenNoFollow | MacOpenNonBlock;
        var existing = OpenAtReadOnly(parentDescriptor, fileName, flags);
        if (existing < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 2) return OperatingSystem.IsMacOS() ? DefaultMacFileMode : null; // Linux create mode is 0666, subject to umask.
            throw new IOException("The existing project file could not be opened safely to preserve its permissions.", new Win32Exception(error));
        }

        using var handle = new SafeFileHandle((IntPtr)existing, ownsHandle: true);
        if (!overwrite)
            throw new IOException("A project file already exists at this path or could not be created.");
        var mode = FileHardLinkInspector.GetUnixPermissions(handle);
        return mode;
    }

    private static async Task WriteWindowsAsync(string root, SafeFileHandle parent, string temporaryName, string fileName, byte[] content, bool overwrite, CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? validateBeforeCommit)
    {
        using var handle = OpenWindowsRelativeFile(parent, temporaryName,
            0x40000000 | DeleteAccess | ReadAttributes | Synchronize,
            FileCreate, FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint);
        var temporaryExists = true;
        FileStream? stream = null;
        try
        {
            EnsureHandleIsChildOfParent(handle, parent, temporaryName, root);
            stream = new FileStream(handle, FileAccess.Write, 4096, isAsync: false);
            await stream.WriteAsync(content, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
            if (validateBeforeCommit is not null && !await validateBeforeCommit(cancellationToken))
                throw new IOException("The project file changed during review. Nothing was overwritten; inspect it again.");
            RenameByHandle(stream.SafeFileHandle, parent, fileName, overwrite);
            temporaryExists = false;
        }
        finally
        {
            if (temporaryExists) TrySetDisposition(handle);
            stream?.Dispose();
        }
    }

    private static (string Directory, string FileName) GetDestination(string root, string destinationPath)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullDestination = Path.GetFullPath(destinationPath);
        var relative = Path.GetRelativePath(fullRoot, fullDestination);
        var components = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(relative) || components.Length == 0 || components.Any(component => component is "." or ".."))
            throw new UnauthorizedAccessException("The project file is outside the trusted project folder.");
        return (Path.GetDirectoryName(relative) ?? "", components[^1]);
    }

    private static void EnsureHandleIsChildOfParent(SafeFileHandle file, SafeFileHandle parent, string name, string root)
    {
        var actual = FileHardLinkInspector.NormalizeHandlePath(FileHardLinkInspector.GetFinalPath(file));
        var parentPath = FileHardLinkInspector.NormalizeHandlePath(FileHardLinkInspector.GetFinalPath(parent));
        var expected = FileHardLinkInspector.NormalizeHandlePath(Path.Combine(parentPath, name));
        using var rootHandle = FileHardLinkInspector.OpenDirectoryHandle(root);
        var rootPath = FileHardLinkInspector.NormalizeHandlePath(FileHardLinkInspector.GetFinalPath(rootHandle));
        var prefix = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!FileHardLinkInspector.PathsEqual(actual, expected) ||
            (!FileHardLinkInspector.PathsEqual(parentPath, rootPath) && !parentPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("The project file resolved outside the trusted project folder.");
    }

    private static void RenameByHandle(SafeFileHandle file, SafeFileHandle parent, string fileName, bool replace)
    {
        var nameBytes = Encoding.Unicode.GetBytes(fileName);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var bufferSize = nameOffset + nameBytes.Length;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            Marshal.WriteByte(buffer, 0, replace ? (byte)1 : (byte)0);
            Marshal.WriteIntPtr(buffer, rootOffset, parent.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, nameOffset), nameBytes.Length);
            var status = NtSetInformationFile(file, out _, buffer, (uint)bufferSize, FileRenameInformation);
            if (status < 0) throw NtStatusIOException("The project file could not be atomically renamed within its verified folder.", status);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static SafeFileHandle OpenWindowsRelativeFile(SafeFileHandle parent, string name, uint desiredAccess, uint disposition, uint createOptions)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicode = new UnicodeString
        {
            Length = checked((ushort)(name.Length * sizeof(char))),
            MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
            Buffer = nameBuffer
        };
        var unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        Marshal.StructureToPtr(unicode, unicodeBuffer, false);
        var attributes = new ObjectAttributes
        {
            Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
            RootDirectory = parent.DangerousGetHandle(),
            ObjectName = unicodeBuffer,
            Attributes = ObjectCaseInsensitive
        };
        var attributesBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<ObjectAttributes>());
        Marshal.StructureToPtr(attributes, attributesBuffer, false);
        try
        {
            var status = NtCreateFile(out var rawHandle, desiredAccess, attributesBuffer, out _, IntPtr.Zero,
                FileAttributeNormal, 0x7, disposition, createOptions, IntPtr.Zero, 0);
            if (status < 0) throw NtStatusIOException("A project file could not be opened relative to its verified folder.", status);
            return new SafeFileHandle(rawHandle, ownsHandle: true);
        }
        finally
        {
            Marshal.FreeHGlobal(attributesBuffer);
            Marshal.FreeHGlobal(unicodeBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static IOException NtStatusIOException(string message, int status) =>
        new(message, new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))));

    private static void SetDisposition(SafeFileHandle handle)
    {
        var disposition = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(disposition, 1);
            var status = NtSetInformationFile(handle, out _, disposition, 1, FileDispositionInformation);
            if (status < 0) throw NtStatusIOException("The project file could not be removed safely.", status);
        }
        finally { Marshal.FreeHGlobal(disposition); }
    }

    private static void TrySetDisposition(SafeFileHandle handle)
    {
        try { SetDisposition(handle); } catch { }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out IntPtr fileHandle, uint desiredAccess, IntPtr objectAttributes,
        out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess,
        uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation, uint length, int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(SafeFileHandle fileHandle, IntPtr eventHandle,
        IntPtr apcRoutine, IntPtr apcContext, out IoStatusBlock ioStatusBlock, IntPtr fileInformation,
        int length, int fileInformationClass, [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
        IntPtr fileName, [MarshalAs(UnmanagedType.U1)] bool restartScan);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtCreate(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtReadOnly(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int Fchmod(int fileDescriptor, uint mode);

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int RenameAt(int oldDirectoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    [DllImport("libc", EntryPoint = "linkat", SetLastError = true)]
    private static extern int LinkAt(int oldDirectoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, int flags);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dup", SetLastError = true)]
    private static extern int Dup(int fileDescriptor);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr OpenDirectoryStream(int fileDescriptor);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "readdir", SetLastError = true)]
    private static extern IntPtr ReadDirectory(IntPtr directoryStream);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "readdir$INODE64", SetLastError = true)]
    private static extern IntPtr ReadDirectoryInode64(IntPtr directoryStream);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "closedir", SetLastError = true)]
    private static extern int CloseDirectoryStream(IntPtr directoryStream);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fileDescriptor);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }
}

internal sealed record WorkspaceDirectoryEntry(string Name, FileAttributes Attributes);

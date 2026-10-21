using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PCHealth.Ledger;

namespace PCHealth.Execution.Windows;

/// <summary>
/// Moves a file or folder by handle (spec §6.3). The source is opened without following a reparse point, so a target
/// swapped for a junction or symlink is opened as the link itself and refused. The handle's final path must still be
/// the expected path, which catches a parent folder swapped for a junction. The rename then happens on that same
/// handle, so nothing can be substituted between the check and the move. It never overwrites and never crosses volumes.
/// </summary>
public sealed class HandleMove : IFileMover
{
    public void Move(string source, string destination, bool isDirectory) => MoveByHandle(source, destination, isDirectory);

    /// <exception cref="IOException">The source is a link, is no longer at its expected path, is not the expected kind, or the rename failed. Nothing moved.</exception>
    public static void MoveByHandle(string source, string destination, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(destination))
        {
            throw new ArgumentException("A move by handle needs full paths.");
        }

        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        using var handle = CreateFileW(
            @"\\?\" + expected,
            Delete | FileReadAttributes | Synchronize,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw Failure($"'{expected}' could not be opened", Marshal.GetLastPInvokeError());
        }

        if (!GetFileInformationByHandleEx(handle, FileBasicInfoClass, out var basic, (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            throw Failure($"The attributes of '{expected}' could not be read", Marshal.GetLastPInvokeError());
        }

        var attributes = (FileAttributes)basic.FileAttributes;
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"'{expected}' is now a link; links are never moved.");
        }

        if (attributes.HasFlag(FileAttributes.Directory) != isDirectory)
        {
            throw new IOException($"'{expected}' is no longer a {(isDirectory ? "folder" : "file")}.");
        }

        var final = FinalPath(handle);
        if (!string.Equals(final, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"'{expected}' now leads to '{final}'; it was not moved.");
        }

        Rename(handle, @"\\?\" + Path.GetFullPath(destination));
    }

    private static void Rename(SafeFileHandle handle, string destination)
    {
        // FILE_RENAME_INFO: a 4-byte union (ReplaceIfExists / Flags), a pointer-aligned RootDirectory handle,
        // a DWORD FileNameLength in bytes, then the name. Flags = 0 and ReplaceIfExists = false: never overwrite.
        var rootOffset = IntPtr.Size;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var nameBytes = Encoding.Unicode.GetBytes(destination);
        var size = nameOffset + nameBytes.Length + sizeof(char);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            foreach (var infoClass in new[] { FileRenameInfoExClass, FileRenameInfoClass })
            {
                for (var i = 0; i < size; i++)
                {
                    Marshal.WriteByte(buffer, i, 0);
                }

                Marshal.WriteInt32(buffer, lengthOffset, nameBytes.Length);
                Marshal.Copy(nameBytes, 0, buffer + nameOffset, nameBytes.Length);
                if (SetFileInformationByHandle(handle, infoClass, buffer, (uint)size))
                {
                    return;
                }

                var error = Marshal.GetLastPInvokeError();
                // Older systems lack the Ex class; fall back to the classic one only in that case.
                if (infoClass == FileRenameInfoExClass && error is ErrorInvalidParameter or ErrorNotSupported)
                {
                    continue;
                }

                throw Failure($"It could not be renamed to '{destination[4..]}'", error);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                throw Failure("Its final path could not be read", Marshal.GetLastPInvokeError());
            }

            if (length < buffer.Capacity)
            {
                var path = buffer.ToString();
                if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"'{path}' is on a network share; network paths are never moved.");
                }

                return Path.TrimEndingDirectorySeparator(path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path);
            }

            buffer.Capacity = (int)length + 1;
        }
    }

    private static IOException Failure(string what, int error) => new($"{what}: {new Win32Exception(error).Message} ({error}).");

    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x80;
    private const uint Synchronize = 0x00100000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileBasicInfoClass = 0;
    private const int FileRenameInfoClass = 3;
    private const int FileRenameInfoExClass = 22;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotSupported = 50;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass, out FileBasicInfo lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass, IntPtr lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);
}

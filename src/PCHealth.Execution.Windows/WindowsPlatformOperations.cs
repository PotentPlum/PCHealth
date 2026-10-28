using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PCHealth.Execution.Windows;

/// <summary>
/// The Windows platform layer behind the execution gateway: handle-based quarantine moves (T-24), the Recycle Bin
/// (T-25) and in-use checks (T-26). Every query that cannot be answered throws, so the gateway refuses the item.
/// </summary>
public sealed class WindowsPlatformOperations : IPlatformOperations
{
    public ItemState? Inspect(string finalPath)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(finalPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            var directory = new DirectoryInfo(finalPath);
            return new ItemState(true, 0, directory.LastWriteTimeUtc, directory.CreationTimeUtc, attributes);
        }

        var file = new FileInfo(finalPath);
        return new ItemState(false, file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc, attributes);
    }

    public IReadOnlyList<string> ProcessesUsing(string finalPath) =>
        InUseCheck.ProcessesUsing(finalPath, File.GetAttributes(finalPath).HasFlag(FileAttributes.Directory));

    public IReadOnlyList<string> RunningProcesses(IReadOnlyCollection<string> processNames)
    {
        try
        {
            return processNames.Where(IsRunning).ToArray();
        }
        catch (Win32Exception ex)
        {
            throw new IOException($"The running apps could not be listed: {ex.Message}", ex);
        }

        static bool IsRunning(string name)
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(name);
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return processes.Length > 0;
        }
    }

    /// <summary>True when this account cannot open the item for deletion, which a move needs (D-019).</summary>
    public bool NeedsElevation(string finalPath)
    {
        using var handle = Open(finalPath, Delete | FileReadAttributes);
        if (!handle.IsInvalid)
        {
            return false;
        }

        var error = Marshal.GetLastPInvokeError();
        return error == ErrorAccessDenied
            ? true
            : throw new IOException($"'{finalPath}' could not be opened to check its permissions: {new Win32Exception(error).Message} ({error}).");
    }

    /// <summary>Compares volume serial numbers by handle; a path that does not exist yet is judged by its nearest existing parent.</summary>
    public bool SameVolume(string path, string otherPath) => VolumeSerial(path) == VolumeSerial(otherPath);

    public string? CannotRecycle(string finalPath, long sizeBytes) => WindowsRecycleBin.Unsuitable(finalPath, sizeBytes);

    public string MoveToRecycleBin(string finalPath, bool isDirectory) => WindowsRecycleBin.Recycle(finalPath, isDirectory);

    public void MoveToQuarantine(string finalPath, string destination, bool isDirectory) =>
        HandleMove.MoveByHandle(finalPath, destination, isDirectory);

    public void MoveToFolder(string finalPath, string destination, bool isDirectory) =>
        HandleMove.MoveByHandle(finalPath, destination, isDirectory);

    private static ulong VolumeSerial(string path)
    {
        var existing = Path.GetFullPath(path);
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing)
                ?? throw new IOException($"No part of '{path}' exists, so its volume is unknown.");
        }

        using var handle = Open(existing, FileReadAttributes);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileIdInfoClass, out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"The volume of '{existing}' could not be read: {new Win32Exception(error).Message} ({error}).");
        }

        return info.VolumeSerialNumber;
    }

    private static SafeFileHandle Open(string path, uint access) => CreateFileW(
        @"\\?\" + Path.GetFullPath(path),
        access,
        FileShare.Read | FileShare.Write | FileShare.Delete,
        IntPtr.Zero,
        FileMode.Open,
        FileFlagBackupSemantics | FileFlagOpenReparsePoint,
        IntPtr.Zero);

    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x80;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileIdInfoClass = 18;
    private const int ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
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
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass, out FileIdInfo lpFileInformation, uint dwBufferSize);
}

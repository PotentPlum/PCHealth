using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PCHealth.Core;

namespace PCHealth.Inventory.Windows;

/// <summary>
/// Reads the volume serial number and 128-bit file ID of an open file (<c>FILE_ID_INFO</c>).
/// Hard links to one file report the same identity. Works on NTFS and ReFS; returns null where the
/// file system has no stable file IDs (e.g. FAT), so those paths count as separate files.
/// </summary>
public sealed class WindowsFileIdentityProvider : IFileIdentityProvider
{
    private const int FileIdInfoClass = 18;

    public FileIdentity? TryGetIdentity(SafeFileHandle handle, string path)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            return null;
        }

        var id = new UInt128(info.FileIdHigh, info.FileIdLow);
        return id == UInt128.Zero ? null : new FileIdentity(info.VolumeSerialNumber, id);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;

        // FILE_ID_128 is 16 opaque bytes; only equality matters, so the halves' order is irrelevant.
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FileIdInfo lpFileInformation,
        uint dwBufferSize);
}

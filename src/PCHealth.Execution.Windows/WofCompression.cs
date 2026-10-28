using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PCHealth.Ledger;

namespace PCHealth.Execution.Windows;

/// <summary>
/// Compresses single files where they are with the Windows Overlay Filter, XPRESS in 8 KB chunks (T-55, D-030), and
/// decompresses them again. The path and contents never change; Windows decompresses a file by itself when it is
/// written. Only ever called by the gateway or the restorer, on final paths they just checked.
/// </summary>
public sealed class WofCompression : IFileCompression
{
    private const uint WofProviderFile = 2;
    private const uint FileProviderCompressionXpress8K = 2;
    private const int ErrorCompressionNotBeneficial = 344;
    private const int AccessDenied = unchecked((int)0x80070005);
    private const uint FsctlDeleteExternalBacking = 0x00090314;
    private const int ErrorObjectNotExternallyBacked = 342;

    public string? CannotCompress(string path)
    {
        var root = Path.GetPathRoot(path) ?? throw new IOException($"'{path}' has no drive.");
        var fileSystem = FileSystemOf(root);
        if (!string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            return $"Its drive uses {fileSystem}; compression needs NTFS.";
        }

        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Encrypted))
        {
            return "It is encrypted, and Windows can't compress encrypted files.";
        }

        if (attributes.HasFlag(FileAttributes.Compressed) || IsCompressed(path))
        {
            return "It is already compressed.";
        }

        if (attributes.HasFlag(FileAttributes.Offline) || ((int)attributes & 0x400000) != 0)
        {
            return "Its contents are kept online by a sync service, not on this PC.";
        }

        return attributes.HasFlag(FileAttributes.ReparsePoint) ? "It is a link or a special file, which is never compressed." : null;
    }

    public bool IsCompressed(string path)
    {
        var result = WofIsExternalFile(path, out var external, out var provider, IntPtr.Zero, IntPtr.Zero);
        if (result < 0)
        {
            throw new IOException($"Whether '{path}' is compressed could not be checked.", Marshal.GetExceptionForHR(result));
        }

        return external != 0 && provider == WofProviderFile;
    }

    public long SizeOnDisk(string path)
    {
        var low = GetCompressedFileSizeW(path, out var high);
        if (low == uint.MaxValue && Marshal.GetLastPInvokeError() is var error and not 0)
        {
            throw new IOException($"The size on disk of '{path}' could not be read.", new Win32Exception(error));
        }

        return ((long)high << 32) | low;
    }

    public long FreeSpace(string path)
    {
        var root = Path.GetPathRoot(path) ?? throw new IOException($"'{path}' has no drive.");
        return GetDiskFreeSpaceExW(root, out var available, out _, out _)
            ? (long)available
            : throw new IOException($"The free space on {root} could not be read.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    public void Compress(string path)
    {
        var info = new FileProviderExternalInfoV1 { Version = 1, Algorithm = FileProviderCompressionXpress8K, Flags = 0 };
        var result = WithHandle(path, handle => WofSetFileDataLocation(handle, WofProviderFile, ref info, (uint)Marshal.SizeOf<FileProviderExternalInfoV1>()));
        if (result == HResultFromWin32(ErrorCompressionNotBeneficial))
        {
            throw new CompressionNotBeneficialException($"Compressing '{path}' would not save space.");
        }

        if (result < 0)
        {
            throw new IOException($"'{path}' could not be compressed.", Marshal.GetExceptionForHR(result));
        }
    }

    public void Decompress(string path)
    {
        var result = WithHandle(path, handle => DeviceIoControl(handle, FsctlDeleteExternalBacking, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)
            ? 0
            : HResultFromWin32(Marshal.GetLastPInvokeError()));
        if (result < 0 && result != HResultFromWin32(ErrorObjectNotExternallyBacked))
        {
            throw new IOException($"'{path}' could not be decompressed.", Marshal.GetExceptionForHR(result));
        }
    }

    /// <summary>Runs <paramref name="action"/> on a read handle, and on a read-write handle if Windows asks for write access.</summary>
    private static int WithHandle(string path, Func<SafeFileHandle, int> action)
    {
        using (var read = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = action(read);
            if (result != AccessDenied && result != HResultFromWin32(5))
            {
                return result;
            }
        }

        using var write = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        return action(write);
    }

    private static int HResultFromWin32(int error) => error <= 0 ? error : unchecked((int)(((uint)error & 0x0000FFFF) | 0x80070000));

    private static string FileSystemOf(string root)
    {
        var name = new char[64];
        return GetVolumeInformationW(root, null, 0, out _, out _, out _, name, (uint)name.Length)
            ? new string(name).TrimEnd('\0')
            : throw new IOException($"The file system of {root} could not be read.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileProviderExternalInfoV1
    {
        public uint Version;
        public uint Algorithm;
        public uint Flags;
    }

    [DllImport("wofutil.dll")]
    private static extern int WofSetFileDataLocation(SafeFileHandle fileHandle, uint provider, ref FileProviderExternalInfoV1 externalFileInfo, uint length);

    [DllImport("wofutil.dll", CharSet = CharSet.Unicode)]
    private static extern int WofIsExternalFile(string filePath, out int isExternalFile, out uint provider, IntPtr externalFileInfo, IntPtr bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(
        string rootPathName, char[]? volumeNameBuffer, uint volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength,
        out uint fileSystemFlags, char[] fileSystemNameBuffer, uint fileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}

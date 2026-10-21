using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace PCHealth.Execution.Windows;

/// <summary>
/// Sends items to the Windows Recycle Bin (spec §5.1, §6.4). Before acting it checks the Recycle Bin is suitable: a
/// local fixed drive whose Recycle Bin exists, is not turned off, and has room for the item. After acting it finds the
/// item in this user's <c>$Recycle.Bin</c> folder by its original path, so success means "verifiably recycled". If the
/// item is gone but not in the Recycle Bin, that is reported as a permanent deletion, never as recoverable.
/// </summary>
public static class WindowsRecycleBin
{
    private const string BitBucketKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";

    public static string? Unsuitable(string finalPath, long sizeBytes)
    {
        var root = Path.GetPathRoot(finalPath);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "it is not on a local drive, so it would be deleted permanently.";
        }

        if (new DriveInfo(root).DriveType != DriveType.Fixed)
        {
            return "it is not on a fixed local drive, so it would be deleted permanently.";
        }

        var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
        if (SHQueryRecycleBinW(root, ref info) != 0)
        {
            return "this drive has no Recycle Bin.";
        }

        using var bitBucket = Registry.CurrentUser.OpenSubKey(BitBucketKey);
        var volume = VolumeGuid(root);
        using var settings = volume is null ? null : bitBucket?.OpenSubKey($@"Volume\{volume}");
        if (Value(settings, "NukeOnDelete") == 1 || (settings is null && Value(bitBucket, "NukeOnDelete") == 1))
        {
            return "the Recycle Bin is turned off for this drive, so it would be deleted permanently.";
        }

        if (Value(settings, "MaxCapacity") is { } megabytes && sizeBytes > megabytes * 1024L * 1024L)
        {
            return $"it is larger than this drive's Recycle Bin ({megabytes} MB), so it would be deleted permanently.";
        }

        return null;
    }

    /// <returns>The item's path inside the Recycle Bin.</returns>
    /// <exception cref="PermanentlyDeletedException">The item is gone but is not in the Recycle Bin.</exception>
    /// <exception cref="IOException">The item could not be recycled and is still in place.</exception>
    public static string Recycle(string finalPath, bool isDirectory)
    {
        var started = DateTime.UtcNow.AddSeconds(-2);
        var operation = new ShFileOpStruct
        {
            Func = FoDelete,
            From = finalPath + '\0',
            // Allow undo: recycle. Warn before a permanent deletion: the shell asks instead of silently destroying.
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning,
        };
        var result = SHFileOperationW(ref operation);
        var stillThere = isDirectory ? Directory.Exists(finalPath) : File.Exists(finalPath);
        if (result != 0 || operation.AnyOperationsAborted || stillThere)
        {
            if (stillThere)
            {
                throw new IOException($"'{finalPath}' could not be moved to the Recycle Bin (shell error 0x{result:X}).");
            }
        }

        return Find(finalPath, started)
            ?? throw new PermanentlyDeletedException($"'{finalPath}' is gone but could not be found in the Recycle Bin.");
    }

    /// <summary>The newest Recycle Bin entry for <paramref name="originalPath"/> deleted after <paramref name="since"/>, by its $I record.</summary>
    public static string? Find(string originalPath, DateTime since)
    {
        var folder = RecycleFolder(originalPath);
        if (folder is null || !Directory.Exists(folder))
        {
            return null;
        }

        string? best = null;
        var bestTime = DateTime.MinValue;
        foreach (var record in Directory.EnumerateFiles(folder, "$I*"))
        {
            if (ReadRecord(record) is not { } entry
                || !string.Equals(entry.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase)
                || entry.DeletedUtc < since || entry.DeletedUtc < bestTime)
            {
                continue;
            }

            var content = Path.Combine(folder, "$R" + Path.GetFileName(record)[2..]);
            if (File.Exists(content) || Directory.Exists(content))
            {
                best = content;
                bestTime = entry.DeletedUtc;
            }
        }

        return best;
    }

    /// <summary>This user's Recycle Bin folder on the drive of <paramref name="path"/>.</summary>
    public static string? RecycleFolder(string path) =>
        Path.GetPathRoot(path) is { Length: > 0 } root && WindowsIdentity.GetCurrent().User?.Value is { } sid
            ? Path.Combine(root, "$Recycle.Bin", sid)
            : null;

    /// <summary>Reads a $I record: version (8 bytes), size (8), deletion time (FILETIME, 8), then the original path.</summary>
    internal static (string OriginalPath, DateTime DeletedUtc)? ReadRecord(string recordPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(recordPath);
            if (bytes.Length < 24)
            {
                return null;
            }

            var version = BitConverter.ToInt64(bytes, 0);
            var deleted = DateTime.FromFileTimeUtc(BitConverter.ToInt64(bytes, 16));
            string path;
            if (version == 2 && bytes.Length >= 28)
            {
                var characters = BitConverter.ToInt32(bytes, 24);
                path = Encoding.Unicode.GetString(bytes, 28, Math.Min(characters * 2, bytes.Length - 28));
            }
            else if (version == 1)
            {
                path = Encoding.Unicode.GetString(bytes, 24, bytes.Length - 24);
            }
            else
            {
                return null;
            }

            return (path.TrimEnd('\0'), deleted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static int? Value(RegistryKey? key, string name) => key?.GetValue(name) is int value ? value : null;

    private static string? VolumeGuid(string root)
    {
        var buffer = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(root, buffer, (uint)buffer.Capacity))
        {
            return null;
        }

        // "\\?\Volume{GUID}\" → "{GUID}"
        var name = buffer.ToString();
        var start = name.IndexOf('{');
        var end = name.IndexOf('}');
        return start >= 0 && end > start ? name[start..(end + 1)] : null;
    }

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int Size;
        public long SizeBytes;
        public long NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct lpFileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string lpszVolumeMountPoint, StringBuilder lpszVolumeName, uint cchBufferLength);
}

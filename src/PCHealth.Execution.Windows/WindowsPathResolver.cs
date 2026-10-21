using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PCHealth.Execution.Windows;

/// <summary>
/// Resolves paths by handle (spec §6.3): rejects ambiguous syntax, opens the object (by default without following
/// a link in the last component), and reads its canonical path back with <c>GetFinalPathNameByHandleW</c>.
/// Links in parent folders are always resolved, so the final path shows where the object really is.
/// A link to a volume with no drive letter cannot be expressed as a drive path and is rejected.
/// </summary>
public sealed class WindowsPathResolver : IPathResolver
{
    private const string LongPrefix = @"\\?\";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    public PathResolution Resolve(string path, LinkHandling links = LinkHandling.DoNotFollow)
    {
        if (CheckSyntax(path) is { } rejection)
        {
            return rejection;
        }

        var full = Path.GetFullPath(StripLongPrefix(path));
        using var handle = CreateFileW(
            LongPrefix + full,
            FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            links == LinkHandling.Follow ? FileFlagBackupSemantics : FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return FromWin32Error(Marshal.GetLastPInvokeError(), path);
        }

        if (!GetFileInformationByHandleEx(handle, FileBasicInfoClass, out var basic, (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            return FromWin32Error(Marshal.GetLastPInvokeError(), path);
        }

        var final = FinalPath(handle, out var error);
        if (final is null)
        {
            return FromWin32Error(error, path);
        }

        if (final.StartsWith(LongPrefix + @"UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return PathResolution.Rejected(PathRejectionReason.NetworkPath, $"'{path}' is on a network share, which is out of scope.");
        }

        if (!IsDriveLongPath(final))
        {
            return PathResolution.Rejected(PathRejectionReason.Unresolvable, $"'{path}' resolved to '{final}', which has no drive-letter path.");
        }

        return PathResolution.Resolved(final[LongPrefix.Length..], (FileAttributes)basic.FileAttributes);
    }

    /// <summary>Rejects paths whose meaning Windows would silently change or that point outside local drives.</summary>
    private static PathResolution? CheckSyntax(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return PathResolution.Rejected(PathRejectionReason.InvalidSyntax, "The path is empty or contains invalid characters.");
        }

        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"//", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return PathResolution.Rejected(PathRejectionReason.UnsupportedPrefix, $"'{path}' uses a device namespace.");
        }

        if (path.StartsWith(LongPrefix, StringComparison.Ordinal))
        {
            if (path.StartsWith(LongPrefix + @"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return PathResolution.Rejected(PathRejectionReason.NetworkPath, $"'{path}' is a network share, which is out of scope.");
            }

            if (!IsDriveLongPath(path))
            {
                return PathResolution.Rejected(PathRejectionReason.UnsupportedPrefix, $"'{path}' uses a volume or device namespace.");
            }
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return PathResolution.Rejected(PathRejectionReason.NetworkPath, $"'{path}' is a network share, which is out of scope.");
        }

        var local = StripLongPrefix(path);
        if (!Path.IsPathFullyQualified(local) || local.Length < 3 || !char.IsAsciiLetter(local[0]) || local[1] != ':')
        {
            return PathResolution.Rejected(PathRejectionReason.NotFullyQualified, $"'{path}' is not a full path starting with a drive letter.");
        }

        if (local.IndexOf(':', 2) >= 0)
        {
            return PathResolution.Rejected(PathRejectionReason.AlternateDataStream, $"'{path}' names an alternate data stream.");
        }

        foreach (var segment in local[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                continue;
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return PathResolution.Rejected(PathRejectionReason.TrailingDotOrSpace, $"'{segment}' ends with a dot or space, which Windows silently removes.");
            }

            var baseName = segment.Split('.')[0].TrimEnd(' ');
            if (ReservedNames.Contains(baseName))
            {
                return PathResolution.Rejected(PathRejectionReason.ReservedDeviceName, $"'{segment}' is a reserved device name.");
            }
        }

        return null;
    }

    private static bool IsDriveLongPath(string path) =>
        path.Length >= LongPrefix.Length + 3
        && path.StartsWith(LongPrefix, StringComparison.Ordinal)
        && char.IsAsciiLetter(path[LongPrefix.Length])
        && path[LongPrefix.Length + 1] == ':'
        && path[LongPrefix.Length + 2] == '\\';

    private static string StripLongPrefix(string path) =>
        path.StartsWith(LongPrefix, StringComparison.Ordinal) ? path[LongPrefix.Length..] : path;

    private static string? FinalPath(SafeFileHandle handle, out int error)
    {
        var buffer = new StringBuilder(512);
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, FileNameNormalized | VolumeNameDos);
            if (length == 0)
            {
                error = Marshal.GetLastPInvokeError();
                return null;
            }

            if (length < buffer.Capacity)
            {
                error = 0;
                return buffer.ToString();
            }

            buffer.Capacity = (int)length + 1;
        }
    }

    private static PathResolution FromWin32Error(int error, string path)
    {
        var detail = new Win32Exception(error).Message;
        return error switch
        {
            2 or 3 => PathResolution.Rejected(PathRejectionReason.NotFound, $"'{path}' does not exist."),
            5 => PathResolution.Rejected(PathRejectionReason.AccessDenied, $"Access to '{path}' was denied."),
            123 or 161 => PathResolution.Rejected(PathRejectionReason.InvalidSyntax, $"'{path}' is not a valid path: {detail}"),
            _ => PathResolution.Rejected(PathRejectionReason.Unresolvable, $"'{path}' could not be resolved: {detail} ({error})."),
        };
    }

    private const uint FileReadAttributes = 0x80;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileBasicInfoClass = 0;
    private const uint FileNameNormalized = 0x0;
    private const uint VolumeNameDos = 0x0;

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
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FileBasicInfo lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);
}

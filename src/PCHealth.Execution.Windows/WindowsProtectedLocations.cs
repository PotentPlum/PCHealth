using System.Runtime.InteropServices;
using PCHealth.Policy;

namespace PCHealth.Execution.Windows;

/// <summary>
/// Finds the default protected locations on this PC (D-014) through the known-folder API, so redirected and
/// localized folders are found where they really are. Paths are canonicalized with the path resolver when possible.
/// </summary>
public static class WindowsProtectedLocations
{
    private static readonly (Guid Id, ProtectionKind Kind, string Reason)[] KnownFolders =
    [
        (new("F38BF404-1D43-42F2-9305-67DE0B28FC23"), ProtectionKind.OperatingSystem, "it holds Windows system files."),
        (new("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7"), ProtectionKind.OperatingSystem, "it holds Windows system files."),
        (new("D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27"), ProtectionKind.OperatingSystem, "it holds Windows system files."),
        (new("905E63B6-C1BF-494E-B29C-65B732D3D21A"), ProtectionKind.ProgramFiles, "installed applications live here."),
        (new("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E"), ProtectionKind.ProgramFiles, "installed applications live here."),
        (new("6D809377-6AF0-444B-8957-A3773F02200E"), ProtectionKind.ProgramFiles, "installed applications live here."),
        (new("5CD7AEE2-2219-4A67-B85D-6C9CE15660CB"), ProtectionKind.ProgramFiles, "applications installed for your account live here."),
        (new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"), ProtectionKind.PersonalFolder, "it is your Documents folder."),
        (new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"), ProtectionKind.PersonalFolder, "it is your Desktop."),
        (new("33E28130-4E1E-4676-835A-98395C3BC3BB"), ProtectionKind.PersonalFolder, "it is your Pictures folder."),
        (new("4BD8D571-6D19-48D3-BE97-422220080E43"), ProtectionKind.PersonalFolder, "it is your Music folder."),
        (new("18989B1D-99B5-455B-841C-AB7C74E4DDFC"), ProtectionKind.PersonalFolder, "it is your Videos folder."),
    ];

    // Boot and recovery areas at the root of the system drive.
    private static readonly string[] BootEntries =
    [
        "Boot", "EFI", "Recovery", "System Volume Information", "$WinREAgent",
        "bootmgr", "BOOTNXT", "pagefile.sys", "hiberfil.sys", "swapfile.sys",
    ];

    public static IReadOnlyList<ProtectedLocation> Discover(IPathResolver? resolver = null)
    {
        resolver ??= new WindowsPathResolver();
        var locations = new List<ProtectedLocation>();
        foreach (var (id, kind, reason) in KnownFolders)
        {
            if (KnownFolderPath(id) is { } path)
            {
                locations.Add(new ProtectedLocation(Canonical(path, resolver), kind, reason));
            }
        }

        var systemRoot = KnownFolderPath(KnownFolders[0].Id) is { } windows ? Path.GetPathRoot(windows) : null;
        if (systemRoot is not null)
        {
            foreach (var entry in BootEntries)
            {
                locations.Add(new ProtectedLocation(
                    Canonical(Path.Combine(systemRoot, entry), resolver),
                    ProtectionKind.Boot,
                    "Windows needs it to start, recover or manage memory."));
            }
        }

        return locations
            .DistinctBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static ProtectionPolicy CreateDefaultPolicy(IPathResolver? resolver = null) => new(Discover(resolver));

    /// <summary>The final path when it resolves; otherwise the path as given, so a missing folder is still protected.</summary>
    private static string Canonical(string path, IPathResolver resolver) =>
        resolver.Resolve(path, LinkHandling.Follow) is { IsResolved: true, FinalPath: { } final } ? final : path;

    private static string? KnownFolderPath(Guid id)
    {
        var result = SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pointer);
        try
        {
            return result == 0 ? Marshal.PtrToStringUni(pointer) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}

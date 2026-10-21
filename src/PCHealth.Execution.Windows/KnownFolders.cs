using System.Runtime.InteropServices;

namespace PCHealth.Execution.Windows;

/// <summary>The Windows known-folder API, which follows folder redirection and localization.</summary>
internal static class KnownFolders
{
    public static readonly Guid LocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    public static readonly Guid RoamingAppData = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D");
    public static readonly Guid ProgramData = new("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97");

    /// <summary>The folder's path, or null if this PC has none.</summary>
    public static string? Path(Guid id)
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

    /// <summary>The path's final location with links and short names resolved, or null if it cannot be resolved.</summary>
    public static string? Canonical(string path, IPathResolver resolver) =>
        resolver.Resolve(path, LinkHandling.Follow) is { IsResolved: true, FinalPath: { } final } ? final : null;

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}

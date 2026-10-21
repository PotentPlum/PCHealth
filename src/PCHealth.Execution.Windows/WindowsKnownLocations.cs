using PCHealth.Policy.Catalog;

namespace PCHealth.Execution.Windows;

/// <summary>Resolves cache-catalog tokens to this user's folders, canonicalized so protection checks see final paths.</summary>
public sealed class WindowsKnownLocations(IPathResolver? resolver = null) : IKnownLocations
{
    private readonly IPathResolver resolver = resolver ?? new WindowsPathResolver();

    public string? Resolve(string token)
    {
        var path = token switch
        {
            "Temp" => System.IO.Path.GetTempPath(),
            "LocalAppData" => KnownFolders.Path(KnownFolders.LocalAppData),
            "RoamingAppData" => KnownFolders.Path(KnownFolders.RoamingAppData),
            "ProgramData" => KnownFolders.Path(KnownFolders.ProgramData),
            _ => null,
        };
        return path is null ? null : KnownFolders.Canonical(path, resolver);
    }
}

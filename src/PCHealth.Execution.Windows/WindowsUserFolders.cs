namespace PCHealth.Execution.Windows;

/// <summary>This user's Desktop, Downloads and Documents (T-52), found through the known-folder API so redirected folders are found too.</summary>
public static class WindowsUserFolders
{
    /// <summary>The final path of each folder this PC has, by its English name.</summary>
    public static IReadOnlyList<(string Name, string Path)> Find(IPathResolver? resolver = null)
    {
        resolver ??= new WindowsPathResolver();
        var folders = new List<(string, string)>();
        foreach (var (name, id) in new[] { ("Desktop", KnownFolders.Desktop), ("Downloads", KnownFolders.Downloads), ("Documents", KnownFolders.Documents) })
        {
            if (KnownFolders.Path(id) is { } path && KnownFolders.Canonical(path, resolver) is { } final)
            {
                folders.Add((name, final));
            }
        }

        return folders;
    }
}

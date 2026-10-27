using PCHealth.Ledger;

namespace PCHealth.Execution.Windows;

/// <summary>
/// Puts items this app recycled back where they were (T-40), the way Explorer's Restore does: the <c>$R</c> item is
/// moved back by handle, never overwriting and never following a link, and its <c>$I</c> record is removed so the
/// Recycle Bin no longer lists it. Only items in this user's own Recycle Bin folder whose record names the original
/// path are touched.
/// </summary>
public sealed class WindowsRecycleBinRestore : IRecycleBin
{
    public bool Holds(string location, string originalPath) =>
        Record(location, originalPath) is not null && (File.Exists(location) || Directory.Exists(location));

    public void Restore(string location, string originalPath, bool isDirectory)
    {
        var record = Record(location, originalPath)
            ?? throw new IOException($"'{originalPath}' is no longer in the Recycle Bin.");
        HandleMove.MoveByHandle(location, originalPath, isDirectory);
        try
        {
            File.Delete(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The item is back; a leftover record only makes the Recycle Bin list an entry it can no longer restore.
        }
    }

    /// <summary>The <c>$I</c> record of <paramref name="location"/>, if it is in this user's Recycle Bin as <paramref name="originalPath"/>.</summary>
    private static string? Record(string location, string originalPath)
    {
        var folder = WindowsRecycleBin.RecycleFolder(originalPath);
        var name = Path.GetFileName(location);
        if (folder is null
            || !string.Equals(Path.GetDirectoryName(location), folder, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var record = Path.Combine(folder, "$I" + name[2..]);
        return WindowsRecycleBin.ReadRecord(record) is { } entry && string.Equals(entry.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase)
            ? record
            : null;
    }
}

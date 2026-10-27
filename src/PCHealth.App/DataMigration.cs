using System.IO;
using PCHealth.App.ViewModels;
using PCHealth.Ledger;

namespace PCHealth.App;

/// <summary>
/// Moves the app's data out of the folder version 0.1.0 kept it in, which is also the per-user install folder, so an
/// uninstall can't delete the history or what is in quarantine (D-021). It runs at every start, before anything opens
/// the data. Every step is a rename on the same volume and is skipped once done, so an interrupted move finishes at the
/// next start. Nothing that is already in the new folder is overwritten.
/// </summary>
public static class DataMigration
{
    /// <returns>How many files and quarantine items were moved.</returns>
    public static int Run(string legacyRoot, AppPaths paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRoot);
        ArgumentNullException.ThrowIfNull(paths);
        if (!Directory.Exists(legacyRoot)
            || string.Equals(Path.TrimEndingDirectorySeparator(legacyRoot), Path.TrimEndingDirectorySeparator(paths.DataRoot), StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var legacy = new AppPaths(legacyRoot);
        var moved = 0;
        foreach (var (from, to) in Files(legacy).Zip(Files(paths)))
        {
            if (File.Exists(from) && !File.Exists(to))
            {
                Directory.CreateDirectory(paths.DataRoot);
                File.Move(from, to);
                moved++;
            }
        }

        if (!Directory.Exists(legacy.Quarantine))
        {
            return moved;
        }

        Directory.CreateDirectory(paths.Quarantine);
        foreach (var slot in Directory.EnumerateDirectories(legacy.Quarantine))
        {
            var target = Path.Combine(paths.Quarantine, Path.GetFileName(slot));
            if (!Directory.Exists(target))
            {
                Directory.Move(slot, target);
                moved++;
            }
        }

        // The ledger records where each quarantined item is; point those records at the new folder before the old one goes.
        if (File.Exists(paths.Ledger))
        {
            using var ledger = TransactionLedger.Open(paths.Ledger);
            ledger.MoveRecoveryLocations(legacy.Quarantine, paths.Quarantine);
        }

        if (!Directory.EnumerateFileSystemEntries(legacy.Quarantine).Any())
        {
            Directory.Delete(legacy.Quarantine);
        }

        return moved;
    }

    // The ledger's write-ahead log and shared memory move with it.
    private static string[] Files(AppPaths paths) =>
    [
        paths.Ledger, paths.Ledger + "-wal", paths.Ledger + "-shm",
        paths.Exclusions, paths.PauseState, paths.Declines, paths.Settings, FirstRunViewModel.MarkerPath(paths),
    ];
}

using PCHealth.Core;

namespace PCHealth.Inventory;

/// <summary>
/// Builds a read-only inventory of a directory tree. Implementations never modify the file system
/// and never follow symlinks, junctions or other reparse points (spec §3.4, §5.1).
/// </summary>
public interface IInventoryScanner
{
    /// <exception cref="OperationCanceledException">The scan was cancelled.</exception>
    ScanResult Scan(string rootPath, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <param name="Entries">Every entry found, the root included, in visit order.</param>
/// <param name="Inaccessible">Paths that could not be read; the scan continued past each of them.</param>
public sealed record ScanResult(IReadOnlyList<FileSystemEntry> Entries, IReadOnlyList<InaccessibleEvidence> Inaccessible)
{
    public bool IsComplete => Inaccessible.Count == 0;
}

/// <param name="CurrentDirectory">The directory that was just read.</param>
public sealed record ScanProgress(long DirectoriesScanned, long FilesScanned, MeasuredSize BytesScanned, string CurrentDirectory);

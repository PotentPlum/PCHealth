using PCHealth.Core;

namespace PCHealth.Inventory;

/// <summary>
/// Builds a read-only inventory of a directory tree. Implementations never modify the file system
/// and never follow symlinks, junctions or other reparse points (spec §3.4, §5.1).
/// </summary>
public interface IInventoryScanner
{
    /// <exception cref="OperationCanceledException">The scan was cancelled.</exception>
    ScanResult Scan(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        PauseToken pause = default,
        CancellationToken cancellationToken = default);
}

/// <param name="Entries">Every entry found, the root included, in visit order.</param>
/// <param name="Inaccessible">Paths that could not be read; the scan continued past each of them.</param>
public sealed record ScanResult(
    IReadOnlyList<FileSystemEntry> Entries,
    IReadOnlyList<InaccessibleEvidence> Inaccessible,
    ScanStatistics Statistics)
{
    public bool IsComplete => Inaccessible.Count == 0;
}

/// <summary>
/// Counts and timings for benchmarks (spec §6.1). These are observations of one run, not performance claims.
/// </summary>
/// <param name="Elapsed">Wall-clock time from start to finish, pauses included.</param>
/// <param name="Paused">Time spent paused.</param>
public sealed record ScanStatistics(long DirectoriesScanned, long FilesScanned, MeasuredSize BytesScanned, TimeSpan Elapsed, TimeSpan Paused)
{
    public TimeSpan Active => Elapsed - Paused;
}

/// <param name="CurrentDirectory">The directory that was just read.</param>
/// <param name="Elapsed">Wall-clock time since the scan started, pauses included.</param>
public sealed record ScanProgress(long DirectoriesScanned, long FilesScanned, MeasuredSize BytesScanned, string CurrentDirectory, TimeSpan Elapsed);

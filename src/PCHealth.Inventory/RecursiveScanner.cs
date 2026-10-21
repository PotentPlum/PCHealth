using System.Diagnostics;
using PCHealth.Core;

namespace PCHealth.Inventory;

/// <summary>
/// The fallback scanner (spec §5.1): walks a tree with ordinary directory enumeration, for volumes or
/// conditions where MFT/USN inventory is unavailable. It only lists metadata and never opens file contents.
/// </summary>
public sealed class RecursiveScanner : IInventoryScanner
{
    private static readonly EnumerationOptions ListOneLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,

        // The default skips hidden and system entries; an inventory must see them.
        AttributesToSkip = 0,
    };

    public ScanResult Scan(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        PauseToken pause = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var paused = TimeSpan.Zero;

        var root = new DirectoryInfo(Path.GetFullPath(rootPath));
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException($"Scan root does not exist: '{root.FullName}'.");
        }

        var entries = new List<FileSystemEntry>();
        var inaccessible = new List<InaccessibleEvidence>();
        long directories = 0, files = 0;
        var bytes = MeasuredSize.Zero;

        var rootEntry = ToDirectoryEntry(root);
        entries.Add(rootEntry);
        var pending = new Stack<DirectoryInfo>();
        if (!rootEntry.IsReparsePoint)
        {
            pending.Push(root);
        }

        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            paused += pause.WaitWhilePaused(cancellationToken);

            var children = ListChildren(directory, inaccessible);
            directories++;
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (child is DirectoryInfo childDirectory)
                {
                    var entry = ToDirectoryEntry(childDirectory);
                    entries.Add(entry);
                    if (!entry.IsReparsePoint)
                    {
                        pending.Push(childDirectory);
                    }
                }
                else if (child is FileInfo file)
                {
                    var entry = ToFileEntry(file);
                    entries.Add(entry);
                    files++;
                    bytes += entry.Size;
                }
            }

            progress?.Report(new ScanProgress(directories, files, bytes, directory.FullName, Stopwatch.GetElapsedTime(started)));
        }

        var statistics = new ScanStatistics(directories, files, bytes, Stopwatch.GetElapsedTime(started), paused);
        return new ScanResult(entries, inaccessible, statistics);
    }

    private static List<FileSystemInfo> ListChildren(DirectoryInfo directory, List<InaccessibleEvidence> inaccessible)
    {
        try
        {
            return directory.EnumerateFileSystemInfos("*", ListOneLevel).ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            inaccessible.Add(new InaccessibleEvidence(directory.FullName, InaccessibleReason.AccessDenied, Describe(ex)));
        }
        catch (DirectoryNotFoundException ex)
        {
            inaccessible.Add(new InaccessibleEvidence(directory.FullName, InaccessibleReason.Vanished, Describe(ex)));
        }
        catch (IOException ex)
        {
            inaccessible.Add(new InaccessibleEvidence(directory.FullName, InaccessibleReason.IoError, Describe(ex)));
        }

        return [];
    }

    private static string Describe(Exception ex) => $"Could not list directory: {ex.GetType().Name}: {ex.Message}";

    private static DirectoryEntry ToDirectoryEntry(DirectoryInfo directory) =>
        new(directory.FullName, directory.Attributes, directory.LastWriteTimeUtc);

    private static FileEntry ToFileEntry(FileInfo file)
    {
        // A file symlink's own size is not the bytes it points at; reading the target's size would follow the link.
        var size = file.Attributes.HasFlag(FileAttributes.ReparsePoint) ? MeasuredSize.Zero : new MeasuredSize(file.Length);
        return new FileEntry(file.FullName, size, file.Attributes, file.LastWriteTimeUtc);
    }
}

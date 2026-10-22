using PCHealth.Core;

namespace PCHealth.Inventory;

/// <param name="TotalSize">Logical size of every file below the folder; links are not followed, so their targets add nothing.</param>
/// <param name="FileCount">Number of files below the folder, at any depth.</param>
public sealed record FolderSize(string FullPath, MeasuredSize TotalSize, long FileCount);

public sealed record CategorySize(FileCategory Category, MeasuredSize TotalSize, long FileCount);

/// <summary>
/// Rolls file sizes up into every ancestor folder of a scan and ranks folders and file categories (spec §5.1).
/// All figures are measured logical sizes from the scan; an incomplete scan gives lower bounds, see <see cref="IsComplete"/>.
/// </summary>
public sealed class StorageSummary
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Dictionary<string, FolderSize> folders;
    private readonly Dictionary<FileCategory, CategorySize> categories;
    private readonly Dictionary<string, FolderSize[]> children;

    private StorageSummary(FolderSize root, Dictionary<string, FolderSize> folders, Dictionary<FileCategory, CategorySize> categories, bool isComplete)
    {
        Root = root;
        this.folders = folders;
        this.categories = categories;
        IsComplete = isComplete;
        children = folders.Values
            .Where(f => !PathComparer.Equals(f.FullPath, root.FullPath) && Path.GetDirectoryName(f.FullPath) is not null)
            .GroupBy(f => Key(Path.GetDirectoryName(f.FullPath)!), PathComparer)
            .ToDictionary(g => g.Key, g => Ranked(g).ToArray(), PathComparer);
    }

    public FolderSize Root { get; }

    /// <summary>False when some folders could not be read, so every total is a lower bound.</summary>
    public bool IsComplete { get; }

    public static StorageSummary From(ScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (scan.Entries.Count == 0 || scan.Entries[0] is not DirectoryEntry rootEntry)
        {
            throw new ArgumentException("A scan result starts with its root directory.", nameof(scan));
        }

        var sizes = new Dictionary<string, (long Bytes, long Files)>(PathComparer);
        foreach (var directory in scan.Entries.OfType<DirectoryEntry>())
        {
            sizes[Key(directory.FullPath)] = (0, 0);
        }

        var categoryTotals = new Dictionary<FileCategory, (long Bytes, long Files)>();
        foreach (var file in scan.Entries.OfType<FileEntry>())
        {
            var category = FileCategories.Classify(file.Name);
            var (categoryBytes, categoryFiles) = categoryTotals.GetValueOrDefault(category);
            categoryTotals[category] = (checked(categoryBytes + file.Size.Bytes), categoryFiles + 1);

            // Every directory in a scan result is an ancestor chain up to the root, so walk up until we leave it.
            for (var parent = Path.GetDirectoryName(file.FullPath); parent is not null; parent = Path.GetDirectoryName(parent))
            {
                var key = Key(parent);
                if (!sizes.TryGetValue(key, out var total))
                {
                    break;
                }

                sizes[key] = (checked(total.Bytes + file.Size.Bytes), total.Files + 1);
            }
        }

        var folders = sizes.ToDictionary(
            pair => pair.Key,
            pair => new FolderSize(pair.Key, new MeasuredSize(pair.Value.Bytes), pair.Value.Files),
            PathComparer);
        var categories = categoryTotals.ToDictionary(
            pair => pair.Key,
            pair => new CategorySize(pair.Key, new MeasuredSize(pair.Value.Bytes), pair.Value.Files));

        return new StorageSummary(folders[Key(rootEntry.FullPath)], folders, categories, scan.IsComplete);
    }

    /// <summary>The rolled-up size of a scanned folder, or null if the folder was not part of the scan.</summary>
    public FolderSize? Folder(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        return folders.GetValueOrDefault(Key(fullPath));
    }

    /// <summary>The folders directly inside a scanned folder, biggest first; empty if it has none or was not scanned.</summary>
    public IReadOnlyList<FolderSize> Children(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        return children.GetValueOrDefault(Key(fullPath)) ?? [];
    }

    /// <summary>The largest folders below the root, biggest first. Nested folders are ranked independently.</summary>
    public IReadOnlyList<FolderSize> LargestFolders(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return Ranked(folders.Values.Where(f => !PathComparer.Equals(f.FullPath, Root.FullPath))).Take(count).ToArray();
    }

    /// <summary>The file categories using the most space, biggest first.</summary>
    public IReadOnlyList<CategorySize> LargestCategories(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return categories.Values
            .OrderByDescending(c => c.TotalSize.Bytes)
            .ThenBy(c => c.Category)
            .Take(count)
            .ToArray();
    }

    private static IEnumerable<FolderSize> Ranked(IEnumerable<FolderSize> folders) =>
        folders.OrderByDescending(f => f.TotalSize.Bytes).ThenBy(f => f.FullPath, StringComparer.Ordinal);

    private static string Key(string path) => Path.TrimEndingDirectorySeparator(path);
}

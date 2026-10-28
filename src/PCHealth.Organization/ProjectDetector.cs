using System.Globalization;
using PCHealth.Core;
using PCHealth.Inventory;

namespace PCHealth.Organization;

/// <summary>A file or folder that marks a project root, e.g. a <c>.csproj</c> or a <c>.git</c> folder.</summary>
public sealed record ProjectMarker(string Path, string Description);

/// <summary>
/// A folder that holds a project (spec §5.2). Its files, measured size and last activity are reported as facts. Age is
/// never a reason to call a project expendable, so this type has no removal suggestion at all.
/// </summary>
public sealed record DetectedProject
{
    internal DetectedProject(
        string root,
        IReadOnlyList<ProjectMarker> markers,
        IReadOnlyList<FileEntry> files,
        string? parentRoot,
        string? cloudSync)
    {
        Root = root;
        Markers = markers;
        Files = files;
        ParentRoot = parentRoot;
        CloudSync = cloudSync;
        TotalSize = files.Aggregate(new MeasuredSize(0), (sum, f) => sum + f.Size);
        LastActivityUtc = files.Count == 0 ? null : files.Max(f => f.LastWriteTimeUtc);
        OnlineOnlyFiles = files.Count(f => IsOnlineOnly(f.Attributes));
    }

    public string Root { get; }

    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(Root));

    public IReadOnlyList<ProjectMarker> Markers { get; }

    /// <summary>Every file below the root, nested projects' files included.</summary>
    public IReadOnlyList<FileEntry> Files { get; }

    /// <summary>The sum of the files' measured sizes.</summary>
    public MeasuredSize TotalSize { get; }

    /// <summary>The newest modified time of any file in the project.</summary>
    public DateTimeOffset? LastActivityUtc { get; }

    /// <summary>The root of the project this one sits inside, if any.</summary>
    public string? ParentRoot { get; }

    /// <summary>The sync service whose folder holds this project, e.g. "OneDrive", or null.</summary>
    public string? CloudSync { get; }

    /// <summary>
    /// Files the sync service keeps only online. Their size is the size they will have once downloaded, not space they
    /// use on this PC now.
    /// </summary>
    public int OnlineOnlyFiles { get; }

    /// <summary>A factual description of activity; never a judgement of whether the project is still needed.</summary>
    public string Activity => LastActivityUtc is { } last
        ? $"Not changed since {last.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}."
        : "Has no files to date it by.";

    /// <summary>Whether nothing in the project changed within <paramref name="threshold"/> before <paramref name="nowUtc"/>.</summary>
    public bool IsInactive(DateTimeOffset nowUtc, TimeSpan threshold) =>
        threshold > TimeSpan.Zero && LastActivityUtc is { } last && nowUtc - last >= threshold;

    // FILE_ATTRIBUTE_OFFLINE, FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS mark cloud placeholders.
    internal static bool IsOnlineOnly(FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.Offline) || ((int)attributes & (0x40000 | 0x400000)) != 0;
}

/// <summary>
/// Finds project roots in a read-only scan from their manifests (T-49): .NET solutions and projects, <c>package.json</c>,
/// <c>pyproject.toml</c>, <c>Cargo.toml</c>, a <c>.git</c> folder, and creative project files.
/// </summary>
public static class ProjectDetector
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly Dictionary<string, string> ManifestNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["package.json"] = "Node.js package (package.json)",
        ["pyproject.toml"] = "Python project (pyproject.toml)",
        ["Cargo.toml"] = "Rust crate (Cargo.toml)",
    };

    private static readonly Dictionary<string, string> ManifestExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".sln"] = "Visual Studio solution",
        [".slnx"] = "Visual Studio solution",
        [".csproj"] = "C# project",
        [".prproj"] = "Premiere Pro project",
        [".aep"] = "After Effects project",
        [".blend"] = "Blender project",
        [".als"] = "Ableton Live set",
        [".flp"] = "FL Studio project",
    };

    // Folders full of other people's manifests (every package in node_modules has a package.json) or of build output.
    private static readonly HashSet<string> NotSearched = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", ".vs", ".venv", "venv", "__pycache__", "target", "packages", ".tox",
    };

    private static readonly string[] SyncFolders = ["OneDrive", "Dropbox", "Google Drive", "My Drive", "iCloudDrive", "iCloud Drive", "Box"];

    public static IReadOnlyList<DetectedProject> Find(ScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var files = scan.Entries.OfType<FileEntry>().Where(f => !f.IsReparsePoint).ToArray();
        var scanRoot = scan.Entries.Count > 0 ? scan.Entries[0].FullPath : "";
        var filesPerFolder = files.GroupBy(f => Path.GetDirectoryName(f.FullPath) ?? "", PathComparer).ToDictionary(g => g.Key, g => g.Count(), PathComparer);
        var markers = new Dictionary<string, List<ProjectMarker>>(PathComparer);
        void Mark(string root, string path, string description)
        {
            if (!markers.TryGetValue(root, out var list))
            {
                markers[root] = list = [];
            }

            list.Add(new ProjectMarker(path, description));
        }

        foreach (var entry in scan.Entries.Where(e => !e.IsReparsePoint))
        {
            var folder = Path.GetDirectoryName(entry.FullPath);
            if (folder is null || InsideNotSearched(scanRoot, folder))
            {
                continue;
            }

            if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                Mark(folder, entry.FullPath, entry is DirectoryEntry ? "Git repository (.git folder)" : "Git working tree (.git file)");
            }
            else if (entry is FileEntry)
            {
                if (ManifestNames.TryGetValue(entry.Name, out var named))
                {
                    Mark(folder, entry.FullPath, named);
                }
                else if (ManifestExtensions.TryGetValue(Path.GetExtension(entry.Name), out var typed))
                {
                    Mark(folder, entry.FullPath, $"{typed} ({entry.Name})");
                }
                else if (Path.GetExtension(entry.Name).Equals(".psd", StringComparison.OrdinalIgnoreCase)
                    && filesPerFolder.GetValueOrDefault(folder) > 1)
                {
                    // A lone .psd is just an image; one with other files around it is likely a design project.
                    Mark(folder, entry.FullPath, $"Photoshop file with other files next to it ({entry.Name})");
                }
            }
        }

        var roots = markers.Keys.OrderBy(r => r, PathComparer).ToArray();
        return roots.Select(root => new DetectedProject(
                root,
                markers[root].OrderBy(m => m.Path, PathComparer).ToArray(),
                files.Where(f => IsBelow(f.FullPath, root)).ToArray(),
                roots.Where(r => !PathComparer.Equals(r, root) && IsBelow(root, r)).OrderByDescending(r => r.Length).FirstOrDefault(),
                SyncService(root)))
            .ToArray();
    }

    /// <summary>The sync service whose folder holds <paramref name="path"/>: a path segment such as "OneDrive" or "OneDrive - Contoso".</summary>
    public static string? SyncService(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        foreach (var segment in path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var service = SyncFolders.FirstOrDefault(s =>
                segment.Equals(s, StringComparison.OrdinalIgnoreCase) || segment.StartsWith(s + " - ", StringComparison.OrdinalIgnoreCase));
            if (service is not null)
            {
                return service == "My Drive" ? "Google Drive" : service.Replace("iCloudDrive", "iCloud Drive", StringComparison.Ordinal);
            }
        }

        return null;
    }

    /// <summary>Whether a folder below the scan root is, or is inside, one whose manifests belong to someone else.</summary>
    private static bool InsideNotSearched(string scanRoot, string folder) =>
        IsBelow(folder, scanRoot)
        && Path.GetRelativePath(scanRoot, folder)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(NotSearched.Contains);

    private static bool IsBelow(string path, string folder) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, PathComparison);
}

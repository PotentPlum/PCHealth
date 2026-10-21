using System.Text.RegularExpressions;
using PCHealth.Core;

namespace PCHealth.Duplicates;

/// <summary>
/// Files that look like versions of one document ("Report v2.docx", "Report - Copy (1).docx"). This is a review
/// signal only. Similar names are never evidence that a file can be removed (spec §5.1), so these groups are a
/// separate type from <see cref="VerifiedDuplicateGroup"/>, and their confidence never goes above Medium.
/// </summary>
public sealed record VersionCandidateGroup
{
    internal VersionCandidateGroup(string stem, string extension, IReadOnlyList<FileEntry> files)
    {
        Stem = stem;
        Extension = extension;
        Files = files;
        SameFolder = files.Select(f => Path.GetDirectoryName(f.FullPath)).Distinct(StringComparer.Ordinal).Count() == 1;
    }

    /// <summary>The name shared by every file once version and copy markers are removed.</summary>
    public string Stem { get; }

    public string Extension { get; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<FileEntry> Files { get; }

    public bool SameFolder { get; }

    /// <summary>Always true: a version group is shown for the user to compare, never offered for removal.</summary>
    public bool ReviewOnly => true;

    public Confidence Confidence => SameFolder
        ? new(ConfidenceLevel.Medium, $"{Files.Count} files in one folder share the name \"{Stem}\" once version markers are removed.")
        : new(ConfidenceLevel.Low, $"{Files.Count} files in different folders share the name \"{Stem}\" once version markers are removed.");

    public IReadOnlyList<Evidence> Evidence =>
        Files.Select(f => (Evidence)new PathEvidence(f.FullPath, $"{f.Name} · {f.Size.Bytes} bytes · modified {f.LastWriteTimeUtc:u}")).ToArray();
}

/// <summary>Groups files whose names differ only by version, copy or date markers.</summary>
public static partial class VersionCandidates
{
    public static IReadOnlyList<VersionCandidateGroup> Find(IEnumerable<FileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files
            .Where(f => !f.IsReparsePoint)
            .Select(f => (File: f, Stem: Stem(f.Name), Extension: Path.GetExtension(f.Name).ToLowerInvariant()))
            .Where(x => x.Stem.Length > 0)
            .GroupBy(x => (x.Stem, x.Extension))

            // Identical names in different folders carry no version signal on their own.
            .Where(g => g.Select(x => x.File.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .OrderBy(g => g.Key.Stem, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Extension, StringComparer.Ordinal)
            .Select(g => new VersionCandidateGroup(
                g.Key.Stem,
                g.Key.Extension,
                g.Select(x => x.File).OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.FullPath, StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    /// <summary>The file name without extension, lower-cased, with copy, version and date markers removed.</summary>
    public static string Stem(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        stem = CopyOfPrefix().Replace(stem, "");
        string previous;
        do
        {
            previous = stem;
            stem = Markers().Replace(stem, " ");
            stem = TrailingNumber().Replace(stem, "");
        }
        while (stem != previous);

        return Separators().Replace(stem, " ").Trim();
    }

    [GeneratedRegex(@"^copy( \(\d+\))? of ")]
    private static partial Regex CopyOfPrefix();

    // Whole-token markers: "- copy", "(2)", "v2", "v1.3", "rev4", "final", "draft", dates like 2026-01-31 or 20260131.
    [GeneratedRegex(@"(?<=^|[\s_.\-()])(?:-\s*copy|copy|\(\d+\)|v(?:er(?:sion)?)?[\s_.\-]?\d+(?:[._]\d+)*|rev[\s_.\-]?\d+|final|draft|old|new|backup|bak|latest|edited|\d{4}[\-_.]?\d{2}[\-_.]?\d{2})(?=$|[\s_.\-()])")]
    private static partial Regex Markers();

    // "notes_2", "notes-03", "notes 1"; at most three digits so years and numbered titles like "Chapter 1000" stay.
    [GeneratedRegex(@"[\s_\-]+\d{1,3}\s*$")]
    private static partial Regex TrailingNumber();

    [GeneratedRegex(@"[\s_.\-()]+")]
    private static partial Regex Separators();
}

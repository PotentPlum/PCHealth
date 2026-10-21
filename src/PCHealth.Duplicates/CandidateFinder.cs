using PCHealth.Core;

namespace PCHealth.Duplicates;

/// <summary>Files that may be duplicates: same size and same head+tail digest. Not verified (spec §5.1).</summary>
public sealed record CandidateGroup(MeasuredSize Size, string PartialDigest, IReadOnlyList<FileEntry> Files);

/// <param name="Unreadable">Files dropped because their contents could not be read.</param>
/// <param name="ChangedSinceScan">Files dropped because their size or last-write time no longer matches the scan.</param>
public sealed record CandidateResult(
    IReadOnlyList<CandidateGroup> Groups,
    IReadOnlyList<InaccessibleEvidence> Unreadable,
    IReadOnlyList<FileEntry> ChangedSinceScan);

/// <summary>
/// The cheap stages of duplicate detection: group by size, then by a partial head+tail digest.
/// Zero-byte files and links are skipped. Files are only read, never modified.
/// </summary>
public sealed class CandidateFinder
{
    public const int DefaultSampleBytes = 64 * 1024;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly int sampleBytes;

    /// <param name="sampleBytes">Bytes hashed from each end of a file.</param>
    public CandidateFinder(int sampleBytes = DefaultSampleBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleBytes);
        this.sampleBytes = sampleBytes;
    }

    /// <summary>Stage 1: files of equal, non-zero size, in groups of two or more. Links and repeated paths are dropped.</summary>
    public static IReadOnlyList<IReadOnlyList<FileEntry>> GroupBySize(IEnumerable<FileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files
            .Where(f => f.Size.Bytes > 0 && !f.IsReparsePoint)
            .DistinctBy(f => f.FullPath, PathComparer)
            .GroupBy(f => f.Size.Bytes)
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Key)
            .Select(g => (IReadOnlyList<FileEntry>)g.OrderBy(f => f.FullPath, StringComparer.Ordinal).ToArray())
            .ToArray();
    }

    /// <summary>Runs both stages.</summary>
    public CandidateResult Find(IEnumerable<FileEntry> files, CancellationToken cancellationToken = default)
    {
        var groups = new List<CandidateGroup>();
        var unreadable = new List<InaccessibleEvidence>();
        var changed = new List<FileEntry>();

        foreach (var sizeGroup in GroupBySize(files))
        {
            var digests = new List<(FileEntry File, string Digest)>();
            foreach (var file in sizeGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var handle = ContentReader.OpenRead(file.FullPath);
                    if (!Unchanged(file, handle))
                    {
                        changed.Add(file);
                        continue;
                    }

                    var digest = ContentReader.HeadTailDigest(handle, file.Size.Bytes, sampleBytes);
                    digests.Add((file, $"{ContentReader.Algorithm}/head+tail({sampleBytes}):{Convert.ToHexStringLower(digest)}"));
                }
                catch (FileChangedException)
                {
                    changed.Add(file);
                }
                catch (Exception ex) when (ReadFailures.ToEvidence(file, ex) is { } evidence)
                {
                    unreadable.Add(evidence);
                }
            }

            groups.AddRange(digests
                .GroupBy(d => d.Digest, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new CandidateGroup(sizeGroup[0].Size, g.Key, g.Select(d => d.File).ToArray())));
        }

        return new CandidateResult(groups, unreadable, changed);
    }

    /// <summary>The open file still has the size and last-write time recorded by the scan (D-006).</summary>
    internal static bool Unchanged(FileEntry file, Microsoft.Win32.SafeHandles.SafeFileHandle handle) =>
        RandomAccess.GetLength(handle) == file.Size.Bytes
        && new DateTimeOffset(File.GetLastWriteTimeUtc(handle)) == file.LastWriteTimeUtc;
}

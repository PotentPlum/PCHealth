using PCHealth.Core;

namespace PCHealth.Duplicates;

/// <summary>A file and the full-content hash read from it while it still matched its scanned size and last-write time.</summary>
/// <param name="Identity">The physical file, when the file system reports one; hard links share it.</param>
public sealed record HashedFile(FileEntry File, string ContentHash, FileIdentity? Identity = null);

/// <summary>
/// Files whose entire contents hashed the same (spec §5.1, D-006). Only <see cref="DuplicateVerifier"/> creates these,
/// so nothing else can call a group "verified". Removal still requires the gateway's byte comparison (D-006).
/// A group always holds at least two distinct physical files; hard links to one file are never a group on their own.
/// </summary>
public sealed record VerifiedDuplicateGroup
{
    internal VerifiedDuplicateGroup(MeasuredSize fileSize, string contentHash, IReadOnlyList<HashedFile> files)
    {
        if (files.Any(f => f.ContentHash != contentHash || f.File.Size != fileSize))
        {
            throw new ArgumentException("Every file in a verified group has the same size and content hash.", nameof(files));
        }

        var copies = PhysicalFiles(files);
        if (copies.Count < 2)
        {
            throw new ArgumentException("A verified group needs two or more distinct physical files.", nameof(files));
        }

        FileSize = fileSize;
        ContentHash = contentHash;
        Files = files;
        PhysicalCopies = copies;
    }

    public MeasuredSize FileSize { get; }

    public string ContentHash { get; }

    /// <summary>Every path with this content, hard links included.</summary>
    public IReadOnlyList<HashedFile> Files { get; }

    /// <summary>
    /// The paths grouped by physical file. Hard links to one file form one entry; removing one of its paths frees nothing.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<HashedFile>> PhysicalCopies { get; }

    public Confidence Confidence => new(
        ConfidenceLevel.Verified,
        $"All {PhysicalCopies.Count} files are {FileSize.Bytes} bytes with the same full-content {ContentReader.Algorithm} hash.");

    /// <summary>One inspectable record per file: path, size, timestamp and hash.</summary>
    public IReadOnlyList<Evidence> Evidence =>
        Files.Select(f => (Evidence)new FileContentEvidence(f.File.FullPath, f.File.Size, f.File.LastWriteTimeUtc, f.ContentHash)).ToArray();

    /// <summary>Groups paths by identity; a path without a known identity counts as its own physical file.</summary>
    internal static IReadOnlyList<IReadOnlyList<HashedFile>> PhysicalFiles(IEnumerable<HashedFile> files) =>
        files
            .GroupBy(f => f.Identity is { } id ? (object)id : f)
            .Select(g => (IReadOnlyList<HashedFile>)g.ToArray())
            .ToArray();
}

public sealed record VerificationResult(
    IReadOnlyList<VerifiedDuplicateGroup> Groups,
    IReadOnlyList<InaccessibleEvidence> Unreadable,
    IReadOnlyList<FileEntry> ChangedSinceScan);

/// <summary>The final stage: full-content SHA-256 of every candidate (D-006).</summary>
/// <param name="identities">Reports physical file identities so hard links are not mistaken for copies; null treats every path as distinct.</param>
public sealed class DuplicateVerifier(IFileIdentityProvider? identities = null)
{
    public VerificationResult Verify(IEnumerable<CandidateGroup> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var groups = new List<VerifiedDuplicateGroup>();
        var unreadable = new List<InaccessibleEvidence>();
        var changed = new List<FileEntry>();

        foreach (var candidate in candidates)
        {
            var hashed = new List<HashedFile>();
            foreach (var file in candidate.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var handle = ContentReader.OpenRead(file.FullPath);
                    if (!CandidateFinder.Unchanged(file, handle))
                    {
                        changed.Add(file);
                        continue;
                    }

                    var digest = ContentReader.FullDigest(handle, file.Size.Bytes, cancellationToken);

                    // A write during hashing would make the hash describe neither version.
                    if (!CandidateFinder.Unchanged(file, handle))
                    {
                        changed.Add(file);
                        continue;
                    }

                    var identity = identities?.TryGetIdentity(handle, file.FullPath);
                    hashed.Add(new HashedFile(file, $"{ContentReader.Algorithm}:{Convert.ToHexStringLower(digest)}", identity));
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

            groups.AddRange(hashed
                .GroupBy(h => h.ContentHash, StringComparer.Ordinal)
                .Where(g => VerifiedDuplicateGroup.PhysicalFiles(g).Count > 1)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new VerifiedDuplicateGroup(candidate.Size, g.Key, g.ToArray())));
        }

        return new VerificationResult(groups, unreadable, changed);
    }
}

/// <summary>The whole pipeline: size, then partial digest, then full hash (spec §5.1).</summary>
public sealed class DuplicateFinder(CandidateFinder candidates, DuplicateVerifier verifier)
{
    public DuplicateFinder(IFileIdentityProvider? identities = null)
        : this(new CandidateFinder(), new DuplicateVerifier(identities))
    {
    }

    public VerificationResult Find(IEnumerable<FileEntry> files, CancellationToken cancellationToken = default)
    {
        var stageOne = candidates.Find(files, cancellationToken);
        var stageTwo = verifier.Verify(stageOne.Groups, cancellationToken);
        return new VerificationResult(
            stageTwo.Groups,
            [.. stageOne.Unreadable, .. stageTwo.Unreadable],
            [.. stageOne.ChangedSinceScan, .. stageTwo.ChangedSinceScan]);
    }
}

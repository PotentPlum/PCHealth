using PCHealth.Core;
using PCHealth.Duplicates;
using PCHealth.Inventory;

namespace PCHealth.Policy.Recommendations;

/// <summary>Turns discovery results that are not findings yet (duplicates, version groups, large folders) into findings.</summary>
public static class FindingSources
{
    /// <summary>
    /// Keeps one physical copy and proposes moving the others to the Recycle Bin. The kept copy is a protected one if
    /// any copy is protected (it could not be removed anyway), otherwise the oldest, then the first by path. Every path
    /// of a removed copy is included, because removing only some hard links to a file frees nothing.
    /// </summary>
    public static Finding FromDuplicates(VerifiedDuplicateGroup group, ProtectionPolicy protection)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(protection);

        var keeper = group.PhysicalCopies
            .OrderBy(c => c.Any(f => protection.Check(f.File.FullPath).IsBlocked) ? 0 : 1)
            .ThenBy(c => c.Min(f => f.File.LastWriteTimeUtc))
            .ThenBy(c => c.Select(f => f.File.FullPath).Min(StringComparer.Ordinal), StringComparer.Ordinal)
            .First();
        var removed = group.PhysicalCopies.Where(c => !ReferenceEquals(c, keeper)).ToArray();
        return Duplicates(group, keeper, removed, "protected copies are kept first, then the oldest");
    }

    /// <summary>
    /// The copies the user chose to remove from a group (T-40). Every path of a removed copy is included, and each
    /// operation names the kept copy the gateway compares it with right before acting. Callers make sure at least one
    /// copy stays; <see cref="RecommendationBuilder.ForDuplicates"/> does.
    /// </summary>
    internal static Finding Duplicates(VerifiedDuplicateGroup group, IReadOnlyList<HashedFile> keeper, IReadOnlyList<IReadOnlyList<HashedFile>> removed, string whyKept)
    {
        var keptPath = keeper.Select(f => f.File.FullPath).Min(StringComparer.Ordinal)!;
        var operations = removed
            .SelectMany(c => c)
            .Select(f => new ProposedOperation(OperationKind.MoveToRecycleBin, f.File.FullPath, isDirectory: false, f.File.Size, f.File.LastWriteTimeUtc, keptPath))
            .ToArray();
        var reclaimable = checked(group.FileSize.Bytes * removed.Count);

        var evidence = group.Evidence.ToList();
        evidence.Add(new CheckEvidence("kept copy", $"'{keptPath}' is kept: {whyKept}."));

        return new Finding(
            FindingCategory.VerifiedDuplicate,
            $"{group.PhysicalCopies.Count} identical copies of {Path.GetFileName(keptPath)}",
            "Identical copies take up space without adding anything.",
            evidence,
            group.Confidence,
            new Impact(
                $"Keeps '{keptPath}' and moves {removed.Count} other {(removed.Count == 1 ? "copy" : "copies")} ({operations.Length} paths) to the Recycle Bin.",
                new EstimatedSize(reclaimable, "one file size for each removed copy; space returns when the Recycle Bin is emptied"),
                "Shortcuts, playlists or programs that open a removed copy by its path will no longer find it."),
            new Recovery(RecoveryMethod.RecycleBin, "Copies can be restored from the Recycle Bin until it is emptied."),
            operations: operations);
    }

    /// <summary>Version groups are always review only: similar names are never evidence that a file can go (spec §5.1).</summary>
    public static Finding FromVersionCandidates(VersionCandidateGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new Finding(
            FindingCategory.VersionCandidate,
            $"{group.Files.Count} versions of \"{group.Stem}{group.Extension}\"",
            "Several versions of one document may be worth tidying up, but only you can tell which ones you still need.",
            group.Evidence,
            group.Confidence,
            new Impact("Nothing changes: compare the versions and decide yourself."),
            new Recovery(RecoveryMethod.None, "Nothing is changed, so there is nothing to recover."),
            reviewOnly: true);
    }

    /// <summary>
    /// Files the user chose to remove from a version group after comparing them (T-41). The name match is not the
    /// evidence here; the user's own choice is, so the finding says so and moves exactly those files to the Recycle Bin.
    /// </summary>
    internal static Finding ChosenVersions(VersionCandidateGroup group, IReadOnlyList<FileEntry> chosen)
    {
        var bytes = chosen.Sum(f => f.Size.Bytes);
        return new Finding(
            FindingCategory.VersionCandidate,
            $"{chosen.Count} of {group.Files.Count} possible versions of {group.Files[0].Name}",
            "You chose these after comparing the versions.",
            [
                .. chosen.Select(f => (Evidence)new PathEvidence(f.FullPath, $"{f.Name} · {f.Size.Bytes} bytes · modified {f.LastWriteTimeUtc:u}")),
                new CheckEvidence("your choice", "You chose these files yourself. Similar names alone are never evidence that a file can go."),
            ],
            new Confidence(ConfidenceLevel.High, "You chose these files yourself after comparing them."),
            new Impact(
                $"Moves {chosen.Count} {(chosen.Count == 1 ? "file" : "files")} to the Recycle Bin. The other versions stay.",
                new EstimatedSize(bytes, "the sizes measured during the scan; space returns when the Recycle Bin is emptied"),
                "Shortcuts or recent-file lists that open a removed version by its path will no longer find it."),
            new Recovery(RecoveryMethod.RecycleBin, "Files can be restored from the Recycle Bin until it is emptied."),
            operations: chosen.Select(f => new ProposedOperation(OperationKind.MoveToRecycleBin, f.FullPath, isDirectory: false, f.Size, f.LastWriteTimeUtc)).ToArray());
    }

    /// <summary>A large folder is information about where space goes, not a proposal.</summary>
    public static Finding FromLargeFolder(FolderSize folder, bool scanComplete)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return new Finding(
            FindingCategory.LargeFolder,
            $"Large folder: {Path.GetFileName(folder.FullPath)}",
            $"It holds {folder.FileCount} files and is one of the largest folders scanned.",
            [
                new PathEvidence(folder.FullPath, "Folder measured during this scan."),
                new MeasurementEvidence(folder.TotalSize, $"{folder.FileCount} files, measured during this scan."),
            ],
            scanComplete
                ? new Confidence(ConfidenceLevel.High, "Every file in it was measured.")
                : new Confidence(ConfidenceLevel.Medium, "Some folders could not be read, so the total may be higher."),
            new Impact("Nothing changes: this shows where space goes."),
            new Recovery(RecoveryMethod.None, "Nothing is changed, so there is nothing to recover."));
    }
}

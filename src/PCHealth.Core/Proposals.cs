namespace PCHealth.Core;

/// <summary>The only kinds of change a finding can propose. Each one has a recovery path (spec §3.6, §6.4).</summary>
public enum OperationKind
{
    MoveToRecycleBin,
    MoveToQuarantine,

    /// <summary>Moves the item into a folder the user chose, on the same volume (T-51, D-029).</summary>
    MoveToFolder,
}

/// <summary>
/// One exact change a finding proposes (spec §7: "What will change?"). A proposal is data, not authority: the
/// execution gateway revalidates it, including the expected state recorded here, right before acting (spec §6.3).
/// </summary>
public sealed record ProposedOperation
{
    /// <param name="expectedLastWriteTimeUtc">The last-write time seen during discovery; null for folders.</param>
    /// <param name="keptCopy">
    /// For a duplicate: another file with the same contents that stays. The gateway removes this one only while the
    /// kept copy still exists with byte-for-byte the same contents, so the last copy is never removed (D-006).
    /// </param>
    /// <param name="destinationFolder">For <see cref="OperationKind.MoveToFolder"/> only: the folder the user chose to move the item into.</param>
    public ProposedOperation(
        OperationKind kind,
        string path,
        bool isDirectory,
        MeasuredSize expectedSize,
        DateTimeOffset? expectedLastWriteTimeUtc = null,
        string? keptCopy = null,
        string? destinationFolder = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"A proposed operation needs a full path: '{path}'.", nameof(path));
        }

        if (keptCopy is not null && (isDirectory || !System.IO.Path.IsPathFullyQualified(keptCopy) || keptCopy == path))
        {
            throw new ArgumentException($"A kept copy is another file's full path: '{keptCopy}'.", nameof(keptCopy));
        }

        if ((kind == OperationKind.MoveToFolder) != (destinationFolder is not null)
            || (destinationFolder is not null && !System.IO.Path.IsPathFullyQualified(destinationFolder)))
        {
            throw new ArgumentException("A move to a folder, and only a move to a folder, names the full path of its destination.", nameof(destinationFolder));
        }

        if (kind == OperationKind.MoveToFolder && keptCopy is not null)
        {
            throw new ArgumentException("A move to a folder has no kept copy.", nameof(keptCopy));
        }

        Kind = kind;
        Path = path;
        DestinationFolder = destinationFolder is null ? null : System.IO.Path.TrimEndingDirectorySeparator(destinationFolder);
        KeptCopy = keptCopy;
        IsDirectory = isDirectory;
        ExpectedSize = expectedSize;
        ExpectedLastWriteTimeUtc = expectedLastWriteTimeUtc;
    }

    public OperationKind Kind { get; }

    public string Path { get; }

    public bool IsDirectory { get; }

    /// <summary>The size measured during discovery.</summary>
    public MeasuredSize ExpectedSize { get; }

    public DateTimeOffset? ExpectedLastWriteTimeUtc { get; }

    /// <summary>The copy that must still exist, unchanged in content, for this one to be removed; null if none.</summary>
    public string? KeptCopy { get; }

    /// <summary>The folder a <see cref="OperationKind.MoveToFolder"/> moves the item into; null for other kinds.</summary>
    public string? DestinationFolder { get; }

    /// <summary>Where the item will be after a move to a folder: the destination folder plus the item's own name.</summary>
    public string? Destination => DestinationFolder is null
        ? null
        : System.IO.Path.Combine(DestinationFolder, System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)));

    /// <summary>The recovery method an operation of this kind provides.</summary>
    public static RecoveryMethod RecoveryFor(OperationKind kind) => kind switch
    {
        OperationKind.MoveToRecycleBin => RecoveryMethod.RecycleBin,
        OperationKind.MoveToQuarantine => RecoveryMethod.Quarantine,
        OperationKind.MoveToFolder => RecoveryMethod.MoveBack,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

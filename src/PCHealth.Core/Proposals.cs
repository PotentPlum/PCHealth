namespace PCHealth.Core;

/// <summary>The only kinds of change a finding can propose. Each one has a recovery path (spec §3.6, §6.4).</summary>
public enum OperationKind
{
    MoveToRecycleBin,
    MoveToQuarantine,
}

/// <summary>
/// One exact change a finding proposes (spec §7: "What will change?"). A proposal is data, not authority: the
/// execution gateway revalidates it, including the expected state recorded here, right before acting (spec §6.3).
/// </summary>
public sealed record ProposedOperation
{
    /// <param name="expectedLastWriteTimeUtc">The last-write time seen during discovery; null for folders.</param>
    public ProposedOperation(
        OperationKind kind,
        string path,
        bool isDirectory,
        MeasuredSize expectedSize,
        DateTimeOffset? expectedLastWriteTimeUtc = null)
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

        Kind = kind;
        Path = path;
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

    /// <summary>The recovery method an operation of this kind provides.</summary>
    public static RecoveryMethod RecoveryFor(OperationKind kind) => kind switch
    {
        OperationKind.MoveToRecycleBin => RecoveryMethod.RecycleBin,
        OperationKind.MoveToQuarantine => RecoveryMethod.Quarantine,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

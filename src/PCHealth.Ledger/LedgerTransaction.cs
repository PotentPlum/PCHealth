using PCHealth.Core;

namespace PCHealth.Ledger;

/// <summary>Every kind of change the ledger records: the operations findings propose, plus the product's own follow-ups.</summary>
public enum LedgerOperation
{
    MoveToRecycleBin,
    MoveToQuarantine,

    /// <summary>Items moved into a folder the user chose. Each item's recovery location is where it went.</summary>
    MoveToFolder,

    /// <summary>Quarantined items deleted permanently because their retention period ended.</summary>
    PurgeQuarantine,

    /// <summary>Items put back where they came from.</summary>
    Restore,
}

public static class LedgerOperations
{
    public static LedgerOperation From(OperationKind kind) => kind switch
    {
        OperationKind.MoveToRecycleBin => LedgerOperation.MoveToRecycleBin,
        OperationKind.MoveToQuarantine => LedgerOperation.MoveToQuarantine,
        OperationKind.MoveToFolder => LedgerOperation.MoveToFolder,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>Where a transaction is in its life (spec §6.4, §9). Only <see cref="Verified"/> means the change is confirmed.</summary>
public enum TransactionState
{
    /// <summary>Recorded before anything changed. A pending transaction found at startup was interrupted.</summary>
    Pending,

    /// <summary>The change was made but not yet checked.</summary>
    Applied,

    /// <summary>The change was made and checked.</summary>
    Verified,

    /// <summary>Some or all steps failed; see each item's status.</summary>
    Failed,
}

public enum ItemStatus
{
    Pending,
    Done,
    Failed,

    /// <summary>Left alone on purpose, e.g. because it changed since it was checked.</summary>
    Skipped,
}

/// <summary>What was known about an item at one moment.</summary>
public sealed record ItemMetadata(
    long SizeBytes,
    DateTimeOffset? LastWriteTimeUtc = null,
    DateTimeOffset? CreationTimeUtc = null,
    FileAttributes? Attributes = null);

/// <summary>One affected path in a transaction, with before/after metadata and where it can be recovered from.</summary>
/// <param name="RecoveryLocation">Where the item went, e.g. its quarantine path; null until known or when there is none.</param>
/// <param name="Error">What went wrong for a failed or skipped item.</param>
public sealed record LedgerItem(
    string OriginalPath,
    bool IsDirectory,
    ItemMetadata Before,
    ItemMetadata? After = null,
    string? RecoveryLocation = null,
    ItemStatus Status = ItemStatus.Pending,
    string? Error = null);

/// <summary>
/// One mutating operation as recorded in the ledger. It holds every field spec §6.4 asks for: identifier, time,
/// operation type and outcome; why it was proposed and which checks ran; the exact items with before/after metadata;
/// expected and observed storage effects; and recovery method, limitations, retention deadline and failed steps.
/// </summary>
public sealed record LedgerTransaction
{
    public LedgerTransaction(
        Guid id,
        DateTimeOffset createdUtc,
        LedgerOperation operation,
        TransactionState state,
        string reason,
        IReadOnlyList<string> checks,
        IReadOnlyList<LedgerItem> items,
        long? expectedBytes,
        long? observedBytes,
        RecoveryMethod recoveryMethod,
        string recoveryLimitations,
        DateTimeOffset? retentionDeadlineUtc = null,
        string? outcome = null)
    {
        Id = id;
        CreatedUtc = createdUtc;
        Operation = operation;
        State = state;
        Reason = reason;
        Checks = checks?.ToArray()!;
        Items = items?.ToArray()!;
        ExpectedBytes = expectedBytes;
        ObservedBytes = observedBytes;
        RecoveryMethod = recoveryMethod;
        RecoveryLimitations = recoveryLimitations;
        RetentionDeadlineUtc = retentionDeadlineUtc;
        Outcome = outcome;
        EnsureValid();
    }

    public Guid Id { get; }

    public DateTimeOffset CreatedUtc { get; }

    public LedgerOperation Operation { get; }

    /// <summary>The earlier transaction this one follows up, e.g. the quarantine move a purge or restore belongs to.</summary>
    public Guid? RelatesTo { get; init; }

    public TransactionState State { get; init; }

    /// <summary>Why the action was proposed.</summary>
    public string Reason { get; }

    /// <summary>The checks that were performed before acting.</summary>
    public IReadOnlyList<string> Checks { get; init; }

    public IReadOnlyList<LedgerItem> Items { get; init; }

    /// <summary>Storage the change was expected to free, when measurable.</summary>
    public long? ExpectedBytes { get; }

    /// <summary>Storage the change was observed to free, when measured.</summary>
    public long? ObservedBytes { get; init; }

    public RecoveryMethod RecoveryMethod { get; }

    public string RecoveryLimitations { get; }

    public DateTimeOffset? RetentionDeadlineUtc { get; init; }

    /// <summary>A user-readable account of what happened.</summary>
    public string? Outcome { get; init; }

    /// <summary>Checks the rules again; <c>with</c> expressions bypass the constructor, so the ledger calls this before saving.</summary>
    internal void EnsureValid()
    {
        if (Id == Guid.Empty)
        {
            throw new ArgumentException("A transaction needs an identifier.");
        }

        if (!Enum.IsDefined(Operation) || !Enum.IsDefined(State) || !Enum.IsDefined(RecoveryMethod))
        {
            throw new ArgumentOutOfRangeException(nameof(Operation), "Unknown operation, state or recovery method.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(RecoveryLimitations);
        if (Checks is null || Checks.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Checks must be listed and not blank.");
        }

        if (Items is null || Items.Count == 0)
        {
            throw new ArgumentException("A transaction affects at least one item.");
        }

        if (Items.Any(i => i is null || string.IsNullOrWhiteSpace(i.OriginalPath) || i.Before is null || !Enum.IsDefined(i.Status)))
        {
            throw new ArgumentException("Every item needs a path, its metadata before the change and a known status.");
        }

        if (RecoveryMethod == RecoveryMethod.None && RetentionDeadlineUtc is not null)
        {
            throw new ArgumentException("An irreversible change cannot have a retention deadline.");
        }
    }

    /// <summary>Value equality, lists included, so a transaction read back from the ledger equals the one saved.</summary>
    public bool Equals(LedgerTransaction? other) =>
        other is not null
        && Id == other.Id
        && CreatedUtc == other.CreatedUtc
        && Operation == other.Operation
        && RelatesTo == other.RelatesTo
        && State == other.State
        && Reason == other.Reason
        && Checks.SequenceEqual(other.Checks)
        && Items.SequenceEqual(other.Items)
        && ExpectedBytes == other.ExpectedBytes
        && ObservedBytes == other.ObservedBytes
        && RecoveryMethod == other.RecoveryMethod
        && RecoveryLimitations == other.RecoveryLimitations
        && RetentionDeadlineUtc == other.RetentionDeadlineUtc
        && Outcome == other.Outcome;

    public override int GetHashCode() => HashCode.Combine(Id, State, Items.Count);

    /// <summary>Items that failed or were skipped.</summary>
    public IReadOnlyList<LedgerItem> FailedSteps => Items.Where(i => i.Status is ItemStatus.Failed or ItemStatus.Skipped).ToArray();
}

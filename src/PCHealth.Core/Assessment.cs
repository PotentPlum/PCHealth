namespace PCHealth.Core;

// Confidence, impact and recovery are three independent types (spec §3.3). Nothing in this
// assembly combines them into one "safe" score, and none of them is derived from another.

public enum ConfidenceLevel
{
    Low,
    Medium,
    High,

    /// <summary>Confirmed by full verification, e.g. a byte-for-byte content match (spec §5.1).</summary>
    Verified,
}

/// <summary>How strong the evidence is that the finding is correctly identified and relevant.</summary>
public sealed record Confidence
{
    public Confidence(ConfidenceLevel level, string rationale)
    {
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(rationale);
        Level = level;
        Rationale = rationale;
    }

    public ConfidenceLevel Level { get; }

    public string Rationale { get; }
}

/// <summary>What will change, the expected benefit, and what may happen afterward.</summary>
public sealed record Impact
{
    public Impact(string whatChanges, EstimatedSize? potentiallyReclaimable = null, string? aftereffects = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(whatChanges);
        WhatChanges = whatChanges;
        PotentiallyReclaimable = potentiallyReclaimable;
        Aftereffects = aftereffects;
    }

    public string WhatChanges { get; }

    /// <summary>Always an estimate: reclaimable space is not reclaimed space until an action is measured.</summary>
    public EstimatedSize? PotentiallyReclaimable { get; }

    /// <summary>Rebuilds, downloads, compatibility or performance effects, if any.</summary>
    public string? Aftereffects { get; }
}

public enum RecoveryMethod
{
    /// <summary>The change cannot be undone by this product.</summary>
    None,
    RecycleBin,
    Quarantine,

    /// <summary>Undo moves the items back from the folder they were moved to (T-51).</summary>
    MoveBack,

    /// <summary>Undo decompresses the files where they are (T-55).</summary>
    Decompress,
}

/// <summary>Whether the change can be undone, how, until when, and with what limits.</summary>
public sealed record Recovery
{
    public Recovery(RecoveryMethod method, string limitations, DateTimeOffset? retentionDeadlineUtc = null)
    {
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(limitations);
        if (method == RecoveryMethod.None && retentionDeadlineUtc is not null)
        {
            throw new ArgumentException("An irreversible change cannot have a retention deadline.", nameof(retentionDeadlineUtc));
        }

        Method = method;
        Limitations = limitations;
        RetentionDeadlineUtc = retentionDeadlineUtc;
    }

    public RecoveryMethod Method { get; }

    public bool CanUndo => Method != RecoveryMethod.None;

    public string Limitations { get; }

    public DateTimeOffset? RetentionDeadlineUtc { get; }
}

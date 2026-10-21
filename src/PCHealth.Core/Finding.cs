namespace PCHealth.Core;

/// <summary>The kind of finding; users can exclude whole categories (spec §3.2, §6.5).</summary>
public enum FindingCategory
{
    LargeFolder,
    VerifiedDuplicate,
    VersionCandidate,
    Cache,
    AppResidue,
}

/// <summary>
/// One evidence-backed finding (spec §7). Confidence, impact and recovery are kept as separate
/// properties and are reported side by side, never combined (spec §3.3).
/// </summary>
public sealed record Finding
{
    public Finding(
        FindingCategory category,
        string title,
        string whyItMatters,
        IReadOnlyList<Evidence> evidence,
        Confidence confidence,
        Impact impact,
        Recovery recovery,
        bool reviewOnly = false,
        IReadOnlyList<ProposedOperation>? operations = null)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(whyItMatters);
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Count == 0)
        {
            throw new ArgumentException("A finding needs at least one piece of evidence.", nameof(evidence));
        }

        if (evidence.Any(e => e is null))
        {
            throw new ArgumentException("Evidence entries cannot be null.", nameof(evidence));
        }

        ArgumentNullException.ThrowIfNull(confidence);
        ArgumentNullException.ThrowIfNull(impact);
        ArgumentNullException.ThrowIfNull(recovery);
        operations ??= [];
        if (operations.Any(o => o is null || ProposedOperation.RecoveryFor(o.Kind) != recovery.Method))
        {
            throw new ArgumentException("Every proposed operation must provide the recovery the finding promises.", nameof(operations));
        }

        Category = category;
        Title = title;
        WhyItMatters = whyItMatters;
        Evidence = evidence.ToArray();
        Confidence = confidence;
        Impact = impact;
        Recovery = recovery;
        ReviewOnly = reviewOnly;
        Operations = operations.ToArray();
    }

    public FindingCategory Category { get; }

    /// <summary>What did we find?</summary>
    public string Title { get; }

    /// <summary>Why should I care?</summary>
    public string WhyItMatters { get; }

    public IReadOnlyList<Evidence> Evidence { get; }

    public Confidence Confidence { get; }

    public Impact Impact { get; }

    public Recovery Recovery { get; }

    /// <summary>
    /// The evidence is incomplete or contradictory (spec §5.1): the finding is shown for review and never
    /// included in bulk cleanup.
    /// </summary>
    public bool ReviewOnly { get; }

    /// <summary>The exact changes this finding proposes, if acted on; empty for findings that are only informative.</summary>
    public IReadOnlyList<ProposedOperation> Operations { get; }
}

namespace PCHealth.Core;

/// <summary>
/// One evidence-backed finding (spec §7). Confidence, impact and recovery are kept as separate
/// properties and are reported side by side, never combined (spec §3.3).
/// </summary>
public sealed record Finding
{
    public Finding(
        string title,
        string whyItMatters,
        IReadOnlyList<Evidence> evidence,
        Confidence confidence,
        Impact impact,
        Recovery recovery)
    {
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

        Title = title;
        WhyItMatters = whyItMatters;
        Evidence = evidence.ToArray();
        Confidence = confidence;
        Impact = impact;
        Recovery = recovery;
    }

    /// <summary>What did we find?</summary>
    public string Title { get; }

    /// <summary>Why should I care?</summary>
    public string WhyItMatters { get; }

    public IReadOnlyList<Evidence> Evidence { get; }

    public Confidence Confidence { get; }

    public Impact Impact { get; }

    public Recovery Recovery { get; }
}

namespace PCHealth.Core;

/// <summary>The kind of finding; users can exclude whole categories (spec §3.2, §6.5).</summary>
public enum FindingCategory
{
    LargeFolder,
    VerifiedDuplicate,
    VersionCandidate,
    Cache,
    AppResidue,

    /// <summary>Files or folders the user chose to move into another folder (spec §5.2).</summary>
    Organization,
}

/// <summary>An app that must not be running while a finding is acted on, e.g. a browser whose cache it changes (T-46).</summary>
/// <param name="Process">The process name without <c>.exe</c>, e.g. <c>chrome</c>.</param>
/// <param name="Name">The app's name as people know it, e.g. <c>Google Chrome</c>.</param>
public sealed record AppToClose
{
    public AppToClose(string process, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (process.IndexOfAny(['/', '\\', ':', '*', '?']) >= 0 || process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"A process name has no path, wildcard or .exe: '{process}'.", nameof(process));
        }

        Process = process;
        Name = name;
    }

    public string Process { get; }

    public string Name { get; }
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
        IReadOnlyList<ProposedOperation>? operations = null,
        IReadOnlyList<AppToClose>? closeFirst = null)
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
        CloseFirst = closeFirst?.ToArray() ?? [];
        if (CloseFirst.Any(a => a is null))
        {
            throw new ArgumentException("Apps to close cannot be null.", nameof(closeFirst));
        }
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

    /// <summary>Apps that must be closed before acting; the gateway refuses while any of them runs.</summary>
    public IReadOnlyList<AppToClose> CloseFirst { get; }
}

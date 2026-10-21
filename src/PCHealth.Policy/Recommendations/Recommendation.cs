using System.Security.Cryptography;
using System.Text;
using PCHealth.Core;

namespace PCHealth.Policy.Recommendations;

public enum Disposition
{
    /// <summary>Offered as an action the user can preview and approve.</summary>
    Act,

    /// <summary>Shown for the user to review; never preselected and never part of bulk cleanup.</summary>
    Review,
}

/// <summary>A proposed change that was taken out of a recommendation, and why.</summary>
public sealed record BlockedOperation(ProposedOperation Operation, string Reason);

/// <summary>
/// A finding prepared for the user: it answers the six questions of spec §7, keeps confidence, impact and recovery
/// as separate properties (spec §3.3), and lists only the operations that passed protection and exclusion checks.
/// </summary>
public sealed record Recommendation
{
    internal Recommendation(
        Finding finding,
        IReadOnlyList<ProposedOperation> operations,
        IReadOnlyList<BlockedOperation> blocked,
        TimeSpan quarantineRetention)
    {
        Finding = finding;
        Operations = operations;
        Blocked = blocked;
        (Disposition, ReviewReason) = Decide(finding, operations);
        Key = KeyFor(finding);
        QuarantineRetention = quarantineRetention;
    }

    /// <summary>Stable across scans while the same category and paths are involved.</summary>
    public string Key { get; }

    public Finding Finding { get; }

    public FindingCategory Category => Finding.Category;

    public Disposition Disposition { get; }

    /// <summary>Why this goes to review instead of being offered as an action; null for <see cref="Disposition.Act"/>.</summary>
    public string? ReviewReason { get; }

    /// <summary>The exact changes that would be made, after protected and excluded items were removed.</summary>
    public IReadOnlyList<ProposedOperation> Operations { get; }

    public IReadOnlyList<BlockedOperation> Blocked { get; }

    public TimeSpan QuarantineRetention { get; }

    public Confidence Confidence => Finding.Confidence;

    public Impact Impact => Finding.Impact;

    public Recovery Recovery => Finding.Recovery;

    /// <summary>1. What did we find?</summary>
    public string WhatWeFound => $"{Finding.Title} ({AffectedArea()})";

    /// <summary>2. Why should I care?</summary>
    public string WhyItMatters => Finding.WhyItMatters;

    /// <summary>3. What is the evidence?</summary>
    public IReadOnlyList<Evidence> Evidence => Finding.Evidence;

    /// <summary>4. What will change?</summary>
    public string WhatWillChange
    {
        get
        {
            var blocked = Blocked.Count == 0 ? "" : $" {Blocked.Count} items are left alone because they are protected or excluded.";
            if (Operations.Count == 0)
            {
                return "Nothing changes: this is for you to review." + blocked;
            }

            return Disposition == Disposition.Review
                ? $"Nothing is selected: review it first. If you then choose to act: {Impact.WhatChanges}{blocked}"
                : Impact.WhatChanges + blocked;
        }
    }

    /// <summary>5. What could happen afterward?</summary>
    public string WhatCouldHappenAfterward =>
        Operations.Count == 0
            ? "Nothing changes, so nothing follows."
            : Impact.Aftereffects ?? "No rebuilds, downloads or other effects are known.";

    /// <summary>6. Can I recover?</summary>
    public string CanIRecover
    {
        get
        {
            if (Operations.Count == 0)
            {
                return "Nothing changes, so there is nothing to recover.";
            }

            var until = Recovery.RetentionDeadlineUtc is { } deadline ? $" until {deadline:u}" : "";
            return Recovery.Method switch
            {
                RecoveryMethod.RecycleBin => $"Yes, from the Recycle Bin{until}. {Recovery.Limitations}",
                RecoveryMethod.Quarantine => $"Yes, from quarantine{(until.Length > 0 ? until : $" for {QuarantineRetention.TotalDays:0} days")}; after that it is deleted permanently. {Recovery.Limitations}",
                _ => $"No. {Recovery.Limitations}",
            };
        }
    }

    private static (Disposition, string?) Decide(Finding finding, IReadOnlyList<ProposedOperation> operations)
    {
        if (finding.Operations.Count == 0)
        {
            return (Disposition.Review, "There is nothing to change automatically.");
        }

        if (finding.ReviewOnly)
        {
            return (Disposition.Review, "The evidence is incomplete or contradictory.");
        }

        if (finding.Confidence.Level == ConfidenceLevel.Low)
        {
            return (Disposition.Review, "The evidence is weak.");
        }

        if (operations.Count == 0)
        {
            return (Disposition.Review, "Every item it would change is protected or excluded.");
        }

        return (Disposition.Act, null);
    }

    private string AffectedArea()
    {
        var paths = Paths(Finding).Distinct(StringComparer.Ordinal).ToArray();
        return paths.Length switch
        {
            0 => "no specific path",
            1 => paths[0],
            _ => $"{paths.Length} locations",
        };
    }

    private static IEnumerable<string> Paths(Finding finding) =>
        finding.Evidence.Select(e => e switch
        {
            PathEvidence p => p.Path,
            FileContentEvidence f => f.Path,
            _ => null,
        }).OfType<string>();

    private static string KeyFor(Finding finding)
    {
        var paths = Paths(finding).Concat(finding.Operations.Select(o => o.Path)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var text = $"{finding.Category}\n{finding.Title}\n{string.Join('\n', paths)}";
        return $"{finding.Category}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16]}";
    }
}

/// <param name="Excluded">Findings withheld by the user's exclusions, with the exclusion that matched.</param>
public sealed record RecommendationSet(IReadOnlyList<Recommendation> Recommendations, IReadOnlyList<ExcludedFinding> Excluded)
{
    public IReadOnlyList<Recommendation> ToAct => Recommendations.Where(r => r.Disposition == Disposition.Act).ToArray();

    public IReadOnlyList<Recommendation> ForReview => Recommendations.Where(r => r.Disposition == Disposition.Review).ToArray();
}

/// <summary>
/// Builds recommendations from findings (spec §3.3, §7). Excluded findings are withheld; operations on protected or
/// excluded paths are removed; weak, uncertain or informative findings go to review. Recommendations to act come
/// first, each group ordered by potentially reclaimable space. Confidence, impact and recovery are never combined.
/// </summary>
public sealed class RecommendationBuilder(ProtectionPolicy protection, ExclusionList exclusions, TimeSpan? quarantineRetention = null)
{
    public static readonly TimeSpan DefaultQuarantineRetention = TimeSpan.FromDays(14);

    private readonly TimeSpan retention = quarantineRetention ?? DefaultQuarantineRetention;

    public RecommendationSet Build(IEnumerable<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var applied = exclusions.Apply(findings);
        var recommendations = applied.Kept
            .Select(Build)
            .OrderBy(r => r.Disposition)
            .ThenByDescending(r => r.Impact.PotentiallyReclaimable?.Bytes ?? 0)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToArray();
        return new RecommendationSet(recommendations, applied.Excluded);
    }

    private Recommendation Build(Finding finding)
    {
        var allowed = new List<ProposedOperation>();
        var blocked = new List<BlockedOperation>();
        foreach (var operation in finding.Operations)
        {
            if (protection.Check(operation.Path) is { IsBlocked: true } decision)
            {
                blocked.Add(new BlockedOperation(operation, decision.Reason));
            }
            else if (exclusions.MatchPath(operation.Path) is { } exclusion)
            {
                blocked.Add(new BlockedOperation(operation, $"You excluded '{exclusion.Value}'."));
            }
            else
            {
                allowed.Add(operation);
            }
        }

        return new Recommendation(finding, allowed, blocked, retention);
    }
}

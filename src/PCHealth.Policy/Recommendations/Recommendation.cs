using System.Security.Cryptography;
using System.Text;
using PCHealth.Core;
using PCHealth.Duplicates;

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

    /// <summary>Stable across scans while the same category, title and evidence paths are involved.</summary>
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

    /// <summary>"Close X first." when the finding names apps that must not be running; null otherwise (T-46).</summary>
    public string? CloseFirst => Finding.CloseFirst.Count == 0
        ? null
        : $"Close {string.Join(" and ", Finding.CloseFirst.Select(a => a.Name).Distinct(StringComparer.Ordinal))} first.";

    /// <summary>4. What will change?</summary>
    public string WhatWillChange
    {
        get
        {
            var change = Change();
            return Operations.Count > 0 && CloseFirst is { } close ? $"{close} {change}" : change;
        }
    }

    private string Change()
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
                RecoveryMethod.MoveBack => $"Yes: Undo moves the items back where they were, unless something now exists there. {Recovery.Limitations}",
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

    // Evidence paths only: a cache's individual files change between scans, its folder does not.
    private static string KeyFor(Finding finding)
    {
        var paths = Paths(finding).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var text = $"{finding.Category}\n{finding.Title}\n{string.Join('\n', paths)}";
        return $"{finding.Category}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16]}";
    }
}

/// <summary>An item the user chose to move, as measured during the scan (T-51).</summary>
/// <param name="LastWriteTimeUtc">For files: the last-write time seen; the move is refused if it changed.</param>
public sealed record MoveCandidate(string Path, bool IsDirectory, MeasuredSize Size, DateTimeOffset? LastWriteTimeUtc = null);

/// <summary>The outcome of checking the files a user chose to remove: an action to preview, or why it is refused.</summary>
public sealed record UserChoice(Recommendation? Recommendation, string? Refusal)
{
    public bool IsAllowed => Recommendation is not null;
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

    /// <summary>
    /// Checks the copies the user chose to remove from a verified group (T-40, spec §5.1). Refused when nothing is
    /// chosen, when every copy is chosen (at least one always stays), or when a chosen copy is protected or excluded:
    /// the user's choice is then not what would happen, so nothing is offered. Otherwise the result is an action that
    /// moves exactly the chosen copies to the Recycle Bin, still to be previewed, approved and run by the gateway.
    /// </summary>
    /// <param name="remove">Entries of <see cref="VerifiedDuplicateGroup.PhysicalCopies"/>; hard links to one file go together.</param>
    public UserChoice ForDuplicates(VerifiedDuplicateGroup group, IReadOnlyCollection<IReadOnlyList<HashedFile>> remove)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(remove);
        if (remove.Any(c => !group.PhysicalCopies.Contains(c)))
        {
            throw new ArgumentException("Only copies from this group can be chosen.", nameof(remove));
        }

        if (exclusions.IsExcluded(FindingCategory.VerifiedDuplicate))
        {
            return new UserChoice(null, "You excluded duplicates in Settings, so nothing is offered for them.");
        }

        var chosen = group.PhysicalCopies.Where(remove.Contains).ToArray();
        var kept = group.PhysicalCopies.Where(c => !remove.Contains(c)).ToArray();
        if (chosen.Length == 0)
        {
            return new UserChoice(null, "Choose at least one copy to remove.");
        }

        if (kept.Length == 0)
        {
            return new UserChoice(null, "At least one copy has to stay. Leave out the copy you want to keep.");
        }

        if (Blocked(chosen.SelectMany(c => c).Select(f => f.File.FullPath)) is { } refusal)
        {
            return new UserChoice(null, refusal);
        }

        var keeper = kept.OrderBy(c => c.Min(f => f.File.LastWriteTimeUtc)).ThenBy(c => c.Select(f => f.File.FullPath).Min(StringComparer.Ordinal), StringComparer.Ordinal).First();

        // Only the chosen copies and the category count here: an excluded copy that stays is exactly what the user wants.
        return new UserChoice(Build(FindingSources.Duplicates(group, keeper, chosen, "you chose to keep it")), null);
    }

    /// <summary>
    /// Checks the files the user chose to remove from a version group (T-41, spec §5.1). Version groups never offer
    /// anything on their own; only the user's explicit choice does. Refused when nothing is chosen, when versions are
    /// excluded, or when a chosen file is protected or excluded. Otherwise exactly the chosen files go to the Recycle
    /// Bin, still to be previewed, approved and run by the gateway.
    /// </summary>
    public UserChoice ForVersions(VersionCandidateGroup group, IReadOnlyCollection<FileEntry> remove)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(remove);
        if (remove.Any(f => !group.Files.Contains(f)))
        {
            throw new ArgumentException("Only files from this group can be chosen.", nameof(remove));
        }

        if (exclusions.IsExcluded(FindingCategory.VersionCandidate))
        {
            return new UserChoice(null, "You excluded versions in Settings, so nothing is offered for them.");
        }

        var chosen = group.Files.Where(remove.Contains).ToArray();
        if (chosen.Length == 0)
        {
            return new UserChoice(null, "Choose at least one file to remove.");
        }

        if (Blocked(chosen.Select(f => f.FullPath)) is { } refusal)
        {
            return new UserChoice(null, refusal);
        }

        return new UserChoice(Build(FindingSources.ChosenVersions(group, chosen)), null);
    }

    /// <summary>
    /// Checks items the user chose to move into a folder they chose (T-51, spec §5.2). Refused when nothing is chosen,
    /// when the destination or a chosen item is protected or excluded, when two items share a name, or when one item
    /// is inside another. Otherwise the result moves exactly those items; the gateway checks everything again,
    /// including the destination, right before acting.
    /// </summary>
    /// <param name="why">Why the user is moving them, e.g. the suggestion they came from; shown in Activity.</param>
    public UserChoice ForMove(IReadOnlyCollection<MoveCandidate> items, string destinationFolder, string why)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        if (exclusions.IsExcluded(FindingCategory.Organization))
        {
            return new UserChoice(null, "You excluded organizing in Settings, so nothing is moved.");
        }

        if (items.Count == 0)
        {
            return new UserChoice(null, "Choose at least one item to move.");
        }

        if (string.IsNullOrWhiteSpace(destinationFolder) || !Path.IsPathFullyQualified(destinationFolder))
        {
            return new UserChoice(null, "Choose the folder to move them to.");
        }

        var destination = Path.TrimEndingDirectorySeparator(destinationFolder);
        if (protection.CheckMove(destination, asDestination: true) is { IsBlocked: true } protectedDestination)
        {
            return new UserChoice(null, $"The destination can't be used: {protectedDestination.Reason}");
        }

        if (exclusions.MatchPath(destination) is { } excludedDestination)
        {
            return new UserChoice(null, $"You excluded '{excludedDestination.Value}', so nothing is moved into it.");
        }

        if (items.Select(i => protection.CheckMove(i.Path)).FirstOrDefault(d => d.IsBlocked) is { } protectedItem)
        {
            return new UserChoice(null, protectedItem.Reason);
        }

        if (items.Select(i => exclusions.MatchPath(i.Path) is { } e ? $"You excluded '{e.Value}', so '{i.Path}' cannot be moved." : null).FirstOrDefault(r => r is not null) is { } refusal)
        {
            return new UserChoice(null, refusal);
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var clash = items.GroupBy(i => Path.GetFileName(Path.TrimEndingDirectorySeparator(i.Path)), comparer).FirstOrDefault(g => g.Count() > 1);
        if (clash is not null)
        {
            return new UserChoice(null, $"Two chosen items are both named '{clash.Key}', so they can't go into one folder.");
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var nested = items.FirstOrDefault(i => items.Any(o => o.IsDirectory && o != i
            && i.Path.StartsWith(Path.TrimEndingDirectorySeparator(o.Path) + Path.DirectorySeparatorChar, comparison)));
        if (nested is not null)
        {
            return new UserChoice(null, $"'{nested.Path}' is inside another chosen folder; choose one or the other.");
        }

        return new UserChoice(Build(FindingSources.Move(items, destination, why)), null);
    }

    private string? Blocked(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (protection.Check(path) is { IsBlocked: true } decision)
            {
                return decision.Reason;
            }

            if (exclusions.MatchPath(path) is { } exclusion)
            {
                return $"You excluded '{exclusion.Value}', so '{path}' cannot be removed.";
            }
        }

        return null;
    }

    private Recommendation Build(Finding finding)
    {
        var allowed = new List<ProposedOperation>();
        var blocked = new List<BlockedOperation>();
        foreach (var operation in finding.Operations)
        {
            var check = operation.Kind == OperationKind.MoveToFolder ? protection.CheckMove(operation.Path) : protection.Check(operation.Path);
            if (check is { IsBlocked: true } decision)
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

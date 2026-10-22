using System.IO;
using PCHealth.Core;
using PCHealth.Duplicates;
using PCHealth.Inventory;
using PCHealth.Policy;
using PCHealth.Policy.Catalog;
using PCHealth.Policy.Recommendations;
using PCHealth.Policy.Residue;

namespace PCHealth.App;

/// <summary>Read-only discovery (spec §3.4, §6.5). Nothing here changes a file.</summary>
public interface IDiscovery
{
    /// <summary>Known cache locations only: a few small folders, so a first finding arrives quickly.</summary>
    Task<RecommendationSet> QuickAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Caches, leftover app data, and a scan of the user folder for large folders, duplicates and versions, with the
    /// measured sizes of every folder in it.
    /// </summary>
    Task<FullDiscovery> FullAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>The result of a full discovery: the recommendations and the measured storage they were drawn from.</summary>
public sealed record FullDiscovery(RecommendationSet Recommendations, StorageSummary Storage);

/// <summary>
/// Discovery limited to the user's own folder (D-018). Duplicates are only looked for among files of at least
/// <see cref="DuplicateMinimumBytes"/>, which bounds the hashing work on a large profile.
/// </summary>
public sealed class Discovery(
    IKnownLocations locations,
    IAppRegistrations registrations,
    IInventoryScanner scanner,
    IFileIdentityProvider? identities,
    ProtectionPolicy protection,
    ExclusionList exclusions,
    string profileRoot,
    TimeProvider clock,
    Func<TimeSpan>? quarantineRetention = null) : IDiscovery
{
    public const long DuplicateMinimumBytes = 1024 * 1024;
    public const int LargeFolderCount = 5;

    public Task<RecommendationSet> QuickAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Builder().Build(Caches(cancellationToken)), cancellationToken);

    public async Task<FullDiscovery> FullAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var scan = await BackgroundScan.Start(scanner, profileRoot, progress, cancellationToken).Completion.ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                var findings = new List<Finding>(Caches(cancellationToken));
                findings.AddRange(ResidueDetector.Evaluate(locations, registrations, scanner, protection, exclusions, clock.GetUtcNow(), cancellationToken).Findings);

                var summary = StorageSummary.From(scan);
                findings.AddRange(summary.LargestFolders(LargeFolderCount).Select(f => FindingSources.FromLargeFolder(f, summary.IsComplete)));

                var files = scan.Entries.OfType<FileEntry>().Where(f => !f.IsReparsePoint).ToArray();
                var duplicates = new DuplicateFinder(identities).Find(files.Where(f => f.Size.Bytes >= DuplicateMinimumBytes), cancellationToken);
                findings.AddRange(duplicates.Groups.Select(g => FindingSources.FromDuplicates(g, protection)));
                findings.AddRange(VersionCandidates.Find(files).Select(FindingSources.FromVersionCandidates));
                return new FullDiscovery(Builder().Build(findings), summary);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<Finding> Caches(CancellationToken cancellationToken) =>
        CacheCatalog.LoadDefault().Evaluate(locations, scanner, protection, exclusions, clock.GetUtcNow(), cancellationToken).Findings;

    private RecommendationBuilder Builder() => new(protection, exclusions, quarantineRetention?.Invoke());
}

/// <summary>The latest discovery results, shared by the pages that show them.</summary>
public sealed class FindingsStore
{
    public event EventHandler? Updated;

    public RecommendationSet? Latest { get; private set; }

    /// <summary>The measured sizes from the latest full discovery; kept in memory only, so null after a restart.</summary>
    public StorageSummary? Storage { get; private set; }

    public void Publish(RecommendationSet set)
    {
        Latest = set ?? throw new ArgumentNullException(nameof(set));
        Updated?.Invoke(this, EventArgs.Empty);
    }

    public void Publish(FullDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        Storage = discovery.Storage;
        Publish(discovery.Recommendations);
    }

    /// <summary>
    /// Builds the latest recommendations again from their findings, e.g. after the exclusions or the quarantine period
    /// changed, so what is shown always matches the current settings. The measured storage is kept.
    /// </summary>
    public void Rebuild(RecommendationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (Latest is { } latest)
        {
            Publish(builder.Build(latest.Recommendations.Select(r => r.Finding).Concat(latest.Excluded.Select(e => e.Finding))));
        }
    }

    /// <summary>Drops a recommendation that was acted on; the next discovery measures the new state.</summary>
    public void Remove(Recommendation recommendation)
    {
        if (Latest is { } latest)
        {
            Publish(latest with { Recommendations = latest.Recommendations.Where(r => r != recommendation).ToArray() });
        }
    }
}

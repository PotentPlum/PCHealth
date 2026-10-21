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

    /// <summary>Caches, leftover app data, and a scan of the user folder for large folders, duplicates and versions.</summary>
    Task<RecommendationSet> FullAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
}

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
    TimeProvider clock) : IDiscovery
{
    public const long DuplicateMinimumBytes = 1024 * 1024;
    public const int LargeFolderCount = 5;

    public Task<RecommendationSet> QuickAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Builder().Build(Caches(cancellationToken)), cancellationToken);

    public async Task<RecommendationSet> FullAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
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
                return Builder().Build(findings);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<Finding> Caches(CancellationToken cancellationToken) =>
        CacheCatalog.LoadDefault().Evaluate(locations, scanner, protection, exclusions, clock.GetUtcNow(), cancellationToken).Findings;

    private RecommendationBuilder Builder() => new(protection, exclusions);
}

/// <summary>The latest discovery results, shared by the pages that show them.</summary>
public sealed class FindingsStore
{
    public event EventHandler? Updated;

    public RecommendationSet? Latest { get; private set; }

    public void Publish(RecommendationSet set)
    {
        Latest = set ?? throw new ArgumentNullException(nameof(set));
        Updated?.Invoke(this, EventArgs.Empty);
    }
}

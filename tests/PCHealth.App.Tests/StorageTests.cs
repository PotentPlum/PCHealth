using System.IO;
using PCHealth.App.ViewModels;
using PCHealth.Core;
using PCHealth.Inventory;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;

namespace PCHealth.App.Tests;

/// <summary>The storage detail view over synthetic scan results; no file is read or written.</summary>
public sealed class StorageTests
{
    private static readonly DateTimeOffset Written = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string Profile = Path.Combine(Path.GetTempPath(), "PCHealth.Tests", "storage", "profile");

    [Fact]
    public void Before_anything_is_measured_the_view_says_so()
    {
        var storage = new StorageViewModel(new FindingsStore(), new FakeDiscovery());

        Assert.False(storage.HasMeasurement);
        Assert.Empty(storage.Folders);
        Assert.StartsWith("Nothing has been measured", storage.Status);
    }

    [Fact]
    public void Folders_are_listed_biggest_first_with_measured_sizes_and_drill_down()
    {
        var findings = new FindingsStore();
        var storage = new StorageViewModel(findings, new FakeDiscovery());

        findings.Publish(new FullDiscovery(new([], []), Summary(Profile)));

        Assert.Equal(Profile, storage.Location);
        Assert.Equal("1.1 KB (measured) in 4 files", storage.Total);
        Assert.Equal("Files directly in this folder: 10 bytes in 1 file (measured)", storage.FilesHere);
        Assert.Equal(new[] { "Videos", "Documents" }, storage.Folders.Select(f => f.Name));
        Assert.Equal("1000 bytes (measured)", storage.Folders[0].Size);
        Assert.Equal(1000.0 / 1110, storage.Folders[0].Share, 6);
        Assert.False(storage.UpCommand.CanExecute(null));

        storage.OpenCommand.Execute(storage.Folders[1]);

        Assert.Equal(Path.Combine(Profile, "Documents"), storage.Location);
        Assert.Equal(new[] { "Old" }, storage.Folders.Select(f => f.Name));
        Assert.Equal("Files directly in this folder: 40 bytes in 1 file (measured)", storage.FilesHere);

        storage.UpCommand.Execute(null);
        Assert.Equal(Profile, storage.Location);
    }

    [Fact]
    public void What_could_be_freed_is_an_estimate_and_labeled_apart_from_measured_sizes()
    {
        var findings = new FindingsStore();
        var storage = new StorageViewModel(findings, new FakeDiscovery());
        var old = Path.Combine(Profile, "Documents", "Old", "copy.pdf");

        findings.Publish(new FullDiscovery(new([Recommend(old, 60)], []), Summary(Profile)));

        Assert.Equal("About 60 bytes could be freed by the suggested actions (estimated, not guaranteed)", storage.Reclaimable);
        Assert.Null(storage.Folders.Single(f => f.Name == "Videos").Reclaimable);
        Assert.Contains("(estimated, not guaranteed)", storage.Folders.Single(f => f.Name == "Documents").Reclaimable);
        Assert.All(storage.Folders, f => Assert.EndsWith("(measured)", f.Size));
        Assert.StartsWith("Sizes are measured from the last scan", storage.Completeness);
    }

    [Fact]
    public void Declined_recommendations_do_not_count_toward_what_could_be_freed()
    {
        var findings = new FindingsStore();
        var declines = new DeclinedRecommendations();
        var recommendation = Recommend(Path.Combine(Profile, "Videos", "clip.mp4"), 1000);
        declines.Decline(recommendation, Written);
        var storage = new StorageViewModel(findings, new FakeDiscovery(), declines);

        findings.Publish(new FullDiscovery(new([recommendation], []), Summary(Profile)));

        Assert.Equal("No suggested actions here.", storage.Reclaimable);
    }

    [Fact]
    public async Task Measuring_again_runs_read_only_discovery_and_starts_over_at_the_top()
    {
        var findings = new FindingsStore();
        var discovery = new FakeDiscovery { Result = new FullDiscovery(new([], []), Summary(Profile, complete: false)) };
        var storage = new StorageViewModel(findings, discovery);
        findings.Publish(new FullDiscovery(new([], []), Summary(Profile)));
        storage.OpenCommand.Execute(storage.Folders[0]);

        findings.Publish(new RecommendationSet([], []));
        Assert.Equal(Path.Combine(Profile, "Videos"), storage.Location);

        await storage.MeasureCommand.ExecuteAsync(null);

        Assert.Equal(1, discovery.Calls);
        Assert.Equal(Profile, storage.Location);
        Assert.Equal("Measured.", storage.Status);
        Assert.Equal("Some folders could not be read, so measured sizes are at least what is shown.", storage.Completeness);
        Assert.False(storage.IsRunning);
    }

    /// <summary>A profile with Videos (1000 bytes), Documents (40 + Old: 60) and one 10-byte file at the top.</summary>
    internal static StorageSummary Summary(string profile, bool complete = true)
    {
        string P(string relative) => Path.Combine(profile, relative.Replace('/', Path.DirectorySeparatorChar));
        FileEntry F(string relative, long bytes) => new(P(relative), new MeasuredSize(bytes), FileAttributes.Normal, Written);
        DirectoryEntry D(string path) => new(path, FileAttributes.Directory, Written);

        return StorageSummary.From(new ScanResult(
            [
                D(profile), D(P("Videos")), D(P("Documents")), D(P("Documents/Old")),
                F("top.txt", 10), F("Videos/clip.mp4", 1000), F("Documents/a.txt", 40), F("Documents/Old/copy.pdf", 60),
            ],
            complete ? [] : [new InaccessibleEvidence(P("Locked"), InaccessibleReason.AccessDenied, "denied")],
            new ScanStatistics(4, 4, new MeasuredSize(1110), TimeSpan.Zero, TimeSpan.Zero)));
    }

    private static Recommendation Recommend(string file, long bytes) =>
        new RecommendationBuilder(new ProtectionPolicy([]), new ExclusionList()).Build(
        [
            new Finding(
                FindingCategory.VerifiedDuplicate, "Identical copies", "Wasted space.",
                [new PathEvidence(file, "A copy.")],
                new Confidence(ConfidenceLevel.Verified, "Same hash."),
                new Impact("Moves the extra copy to the Recycle Bin.", new EstimatedSize(bytes, "one copy")),
                new Recovery(RecoveryMethod.RecycleBin, "Restorable."),
                operations: [new ProposedOperation(OperationKind.MoveToRecycleBin, file, isDirectory: false, new MeasuredSize(bytes))]),
        ]).Recommendations.Single();

    private sealed class FakeDiscovery : IDiscovery
    {
        public FullDiscovery? Result { get; init; }

        public int Calls { get; private set; }

        public Task<RecommendationSet> QuickAsync(CancellationToken cancellationToken) => Task.FromResult(new RecommendationSet([], []));

        public Task<FullDiscovery> FullAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result ?? throw new InvalidOperationException("No result set."));
        }
    }
}

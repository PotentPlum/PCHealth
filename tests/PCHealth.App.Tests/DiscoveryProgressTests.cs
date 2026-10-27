using System.IO;
using PCHealth.App.ViewModels;
using PCHealth.Core;
using PCHealth.Duplicates;
using PCHealth.Inventory;
using PCHealth.Testing;

namespace PCHealth.App.Tests;

/// <summary>Scan progress (T-66): measured where the total is known, estimated and labelled as such where it isn't.</summary>
public sealed class DiscoveryProgressTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly TempRoot root = new();
    private readonly ManualClock clock = new(Start);

    [Fact]
    public void A_first_scan_shows_counts_and_steps_but_no_percentage()
    {
        var progress = new DiscoveryProgressViewModel(new AppSettings(), Paths(), clock);
        progress.Start();

        progress.Update(Scan(files: 100, folders: 10, elapsed: TimeSpan.FromSeconds(10)));

        Assert.True(progress.IsActive);
        Assert.True(progress.IsIndeterminate);
        Assert.StartsWith("This is the first scan", progress.Estimate);
        Assert.Equal("100 files · 10 folders · 1 MB measured · 10 s elapsed", progress.Counts);
        Assert.Equal(@"Now: C:\Users\someone\Documents", progress.CurrentFolder);
        Assert.Equal("▶ Measuring your user folder…", progress.Steps[0]);
        Assert.StartsWith("   ", progress.Steps[1]);
    }

    [Fact]
    public void Later_scans_estimate_from_the_last_scan_and_say_so()
    {
        var settings = new AppSettings();
        settings.RecordScan(400, 40);
        var progress = new DiscoveryProgressViewModel(settings, Paths(), clock);
        progress.Start();

        progress.Update(Scan(files: 100, folders: 10, elapsed: TimeSpan.FromSeconds(10)));
        Assert.False(progress.IsIndeterminate);
        Assert.Equal(25, progress.Percent);
        Assert.Equal("About 25%, roughly 30 s left: an estimate, based on the 400 files the last scan counted.", progress.Estimate);

        progress.Update(Scan(files: 500, folders: 50, elapsed: TimeSpan.FromSeconds(50)));
        Assert.True(progress.IsIndeterminate);
        Assert.StartsWith("More files than the last scan counted (400)", progress.Estimate);
    }

    [Fact]
    public void Comparing_duplicates_shows_the_measured_share_and_the_time_left_at_the_speed_so_far()
    {
        var progress = new DiscoveryProgressViewModel(new AppSettings(), Paths(), clock);
        progress.Start();
        progress.Update(new DiscoveryProgress(DiscoveryStep.Duplicates));

        clock.Advance(TimeSpan.FromSeconds(10));
        progress.Update(new DiscoveryProgress(DiscoveryStep.Duplicates, Hashing: new HashProgress(250, 1000, 1, 4)));

        Assert.Equal(25, progress.Percent);
        Assert.Equal("25% of 1000 bytes compared (1 of 4 files), about 30 s left at the speed so far.", progress.Estimate);
        Assert.Equal(["✓ Measuring your user folder", "✓ Checking known cache locations", "✓ Looking for data left behind by removed apps", "▶ Comparing possible duplicates…", "   Preparing suggestions"], progress.Steps);
        Assert.Equal("10 s elapsed", progress.Counts);
    }

    [Fact]
    public void A_completed_scan_is_remembered_for_the_next_estimate()
    {
        var paths = Paths();
        var progress = new DiscoveryProgressViewModel(new AppSettings(), paths, clock);
        progress.Start();

        progress.Complete(new FullDiscovery(new([], []), StorageTests.Summary(root.Resolve("profile")), Statistics(1234, 56)));

        Assert.False(progress.IsActive);
        Assert.Equal(1234, AppSettings.Load(paths.Settings).LastScanFiles);
        Assert.Equal(56, AppSettings.Load(paths.Settings).LastScanFolders);
    }

    public void Dispose() => root.Dispose();

    private AppPaths Paths() => new(root.Resolve("PCHealth"));

    private static DiscoveryProgress Scan(long files, long folders, TimeSpan elapsed) =>
        new(DiscoveryStep.Measuring, Scan: new ScanProgress(folders, files, new MeasuredSize(1024 * 1024), @"C:\Users\someone\Documents", elapsed));

    private static ScanStatistics Statistics(long files, long folders) =>
        new(folders, files, new MeasuredSize(0), TimeSpan.FromSeconds(1), TimeSpan.Zero);
}

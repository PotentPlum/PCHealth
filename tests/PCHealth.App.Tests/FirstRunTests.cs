using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Core;
using PCHealth.Duplicates;
using PCHealth.Inventory;
using PCHealth.Policy;
using PCHealth.Policy.Catalog;
using PCHealth.Policy.Recommendations;
using PCHealth.Policy.Residue;
using PCHealth.Testing;

namespace PCHealth.App.Tests;

public sealed class FirstRunTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly TempRoot root = new();

    [Fact]
    public void First_run_says_what_is_inspected_and_stored_and_that_nothing_leaves_the_pc()
    {
        using var services = Build(new FakeDiscovery());
        var firstRun = services.GetRequiredService<FirstRunViewModel>();

        Assert.Equal("Nothing leaves this PC. Checking for updates contacts GitHub only when you ask.", firstRun.Network);
        Assert.Contains("user folder", firstRun.WhatWeInspect);
        Assert.Contains("Nothing is changed", firstRun.WhatWeInspect);
        Assert.Contains(services.GetRequiredService<AppPaths>().DataRoot, firstRun.WhatWeStore);
    }

    [Fact]
    public async Task Discovery_shows_the_quick_finding_first_then_publishes_everything_and_moves_on()
    {
        var discovery = new FakeDiscovery();
        using var services = Build(discovery);
        var main = services.GetRequiredService<MainViewModel>();
        var firstRun = main.FirstRun;
        Recommendation? shownBeforeFullScan = null;
        discovery.BeforeFull = () => shownBeforeFullScan = firstRun.FirstFinding;

        await firstRun.StartCommand.ExecuteAsync(null);

        Assert.Same(discovery.Quick.Recommendations[0], shownBeforeFullScan);
        Assert.Same(discovery.Full.Recommendations, services.GetRequiredService<FindingsStore>().Latest);
        Assert.Same(discovery.Full.Storage, services.GetRequiredService<FindingsStore>().Storage);
        Assert.IsType<OverviewViewModel>(main.Current);
        Assert.False(FirstRunViewModel.IsNeeded(services.GetRequiredService<AppPaths>()));
        Assert.False(firstRun.IsRunning);

        using var relaunched = Build(new FakeDiscovery());
        Assert.IsType<OverviewViewModel>(relaunched.GetRequiredService<MainViewModel>().Current);
    }

    [Fact]
    public async Task Stopping_discovery_changes_nothing_and_first_run_stays()
    {
        var discovery = new FakeDiscovery { WaitInFull = true };
        using var services = Build(discovery);
        var firstRun = services.GetRequiredService<FirstRunViewModel>();
        discovery.BeforeFull = () => firstRun.CancelCommand.Execute(null);

        await firstRun.StartCommand.ExecuteAsync(null);

        Assert.Equal("Stopped. Nothing was changed.", firstRun.Status);
        Assert.True(FirstRunViewModel.IsNeeded(services.GetRequiredService<AppPaths>()));
    }

    [Fact]
    public async Task Real_discovery_finds_caches_quickly_and_the_rest_without_changing_anything()
    {
        var profile = root.CreateDirectory("profile");
        var temp = root.CreateDirectory("profile/AppData/Local/Temp");
        foreach (var name in new[] { "a.tmp", "b.tmp" })
        {
            var path = root.CreateFile($"profile/AppData/Local/Temp/{name}", new string('t', 100));
            File.SetLastWriteTimeUtc(path, Now.AddDays(-30).UtcDateTime);
        }

        var big = new string('d', (int)Discovery.DuplicateMinimumBytes);
        root.CreateFile("profile/Videos/clip.mp4", big);
        root.CreateFile("profile/Downloads/clip.mp4", big);
        var before = Snapshot(profile);
        var discovery = new Discovery(
            new Locations(temp, root.Resolve("profile/AppData/Local")), new NoRegistrations(), new RecursiveScanner(), null,
            new ProtectionPolicy([]), new ExclusionList(), profile, new ManualClock(Now));

        var quick = await discovery.QuickAsync(TestContext.Current.CancellationToken);
        var reports = new List<DiscoveryProgress>();
        var full = await discovery.FullAsync(new Recorder(reports), TestContext.Current.CancellationToken);

        Assert.Equal(Enum.GetValues<DiscoveryStep>(), reports.Select(r => r.Step).Distinct());
        Assert.Equal(new HashProgress(2 * big.Length, 2 * big.Length, 2, 2), reports.Last(r => r.Hashing is not null).Hashing);
        Assert.Equal(4, full.Statistics!.FilesScanned);

        Assert.Equal(FindingCategory.Cache, Assert.Single(quick.ToAct).Category);
        Assert.Contains(full.Recommendations.ToAct, r => r.Category == FindingCategory.VerifiedDuplicate);
        Assert.Contains(full.Recommendations.ForReview, r => r.Category == FindingCategory.LargeFolder);
        Assert.Equal(profile, full.Storage.Root.FullPath);
        Assert.Equal(before, Snapshot(profile));
    }

    public void Dispose() => root.Dispose();

    private sealed class Recorder(List<DiscoveryProgress> reports) : IProgress<DiscoveryProgress>
    {
        public void Report(DiscoveryProgress value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }

    private ServiceProvider Build(IDiscovery discovery) =>
        AppServices.Build(new AppPaths(root.Resolve("PCHealth")), s => s.AddSingleton(discovery));

    private static string[] Snapshot(string folder) =>
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => $"{p}|{File.GetLastWriteTimeUtc(p):O}|{(File.Exists(p) ? new FileInfo(p).Length : -1)}")
            .ToArray();

    private static Recommendation Recommendation(string title)
    {
        var path = Path.Combine(Path.GetTempPath(), "PCHealth.Tests", "first-run", title);
        return new RecommendationBuilder(new ProtectionPolicy([]), new ExclusionList()).Build(
        [
            new Finding(
                FindingCategory.Cache, title, "It is regenerable.", [new PathEvidence(path, "Known location.")],
                new Confidence(ConfidenceLevel.High, "Known location."), new Impact("Moves 1 file to the Recycle Bin."),
                new Recovery(RecoveryMethod.RecycleBin, "Restorable."),
                operations: [new ProposedOperation(OperationKind.MoveToRecycleBin, Path.Combine(path, "a.tmp"), isDirectory: false, new MeasuredSize(1))]),
        ]).Recommendations.Single();
    }

    private sealed class FakeDiscovery : IDiscovery
    {
        public RecommendationSet Quick { get; } = new([Recommendation("Quick")], []);

        public FullDiscovery Full { get; } = new(new([Recommendation("Full")], []), StorageTests.Summary(Path.GetTempPath()));

        public bool WaitInFull { get; init; }

        public Action? BeforeFull { get; set; }

        public Task<RecommendationSet> QuickAsync(CancellationToken cancellationToken) => Task.FromResult(Quick);

        public async Task<FullDiscovery> FullAsync(IProgress<DiscoveryProgress>? progress, CancellationToken cancellationToken)
        {
            BeforeFull?.Invoke();
            if (WaitInFull)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return Full;
        }
    }

    internal sealed class Locations(string temp, string localAppData) : IKnownLocations
    {
        public string? Resolve(string token) => token switch
        {
            "Temp" => temp,
            "LocalAppData" => localAppData,
            _ => null,
        };
    }

    internal sealed class NoRegistrations : IAppRegistrations
    {
        public RegistrationSnapshot Read() => new([], []);
    }
}

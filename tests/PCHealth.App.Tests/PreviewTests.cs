using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Core;
using PCHealth.Execution;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;
using PCHealth.Testing;

namespace PCHealth.App.Tests;

/// <summary>Preview, approve and decline with fixture files, a pass-through resolver and a plain-move platform.</summary>
public sealed class PreviewTests : IDisposable
{
    private readonly TempRoot root = new();

    [Fact]
    public void The_preview_shows_exact_items_what_is_left_alone_the_effect_and_the_recovery_terms()
    {
        using var services = Build();
        var (a, b, kept) = (File("a.tmp", "aa"), File("b.tmp", "bbb"), File("kept.tmp", "k"));
        var exclusions = new ExclusionList();
        exclusions.ExcludeFile(kept, DateTimeOffset.UtcNow);
        var preview = Open(services, Recommend(exclusions, a, b, kept));

        Assert.Equal(new[] { a, b }, preview.Items.Select(i => i.Path));
        Assert.All(preview.Items, i => Assert.True(i.IsSelected));
        Assert.Equal($"{kept}: You excluded '{kept}'.", Assert.Single(preview.LeftAlone));
        Assert.StartsWith("2 of 2 items selected: about 5 bytes (estimated", preview.ExpectedEffect);
        Assert.Contains("for 14 days; after that it is deleted permanently", preview.RecoveryTerms);
    }

    [Fact]
    public void Approving_runs_only_the_selected_items_through_the_gateway()
    {
        using var services = Build();
        var (a, b) = (File("a.tmp", "aa"), File("b.tmp", "bbb"));
        var main = services.GetRequiredService<MainViewModel>();
        var store = services.GetRequiredService<FindingsStore>();
        var recommendation = Recommend(new ExclusionList(), a, b);
        store.Publish(new RecommendationSet([recommendation], []));
        main.Current = main.Pages[0];
        var overview = (OverviewViewModel)main.Current;

        overview.PreviewCommand.Execute(overview.Items[0]);
        var preview = Assert.IsType<PreviewViewModel>(main.Current);
        preview.Items[1].IsSelected = false;
        Assert.StartsWith("1 of 2 items selected: about 2 bytes", preview.ExpectedEffect);
        preview.ApproveCommand.Execute(null);

        Assert.Equal(ExecutionStatus.Completed, preview.Execution!.Status);
        Assert.False(System.IO.File.Exists(a));
        Assert.True(System.IO.File.Exists(b));
        Assert.Equal(LedgerOperation.MoveToQuarantine, services.GetRequiredService<TransactionLedger>().All().Single().Operation);
        Assert.False(preview.ApproveCommand.CanExecute(null));

        preview.BackCommand.Execute(null);
        Assert.Same(overview, main.Current);
        Assert.Empty(store.Latest!.Recommendations);
    }

    [Fact]
    public void Nothing_can_be_approved_with_every_item_left_out()
    {
        using var services = Build();
        var preview = Open(services, Recommend(new ExclusionList(), File("a.tmp", "a")));

        preview.Items[0].IsSelected = false;

        Assert.False(preview.ApproveCommand.CanExecute(null));
    }

    [Fact]
    public void A_declined_recommendation_does_not_come_back_without_new_evidence()
    {
        var a = File("a.tmp", "aa");
        using (var services = Build())
        {
            var preview = Open(services, Recommend(new ExclusionList(), a));
            preview.DeclineCommand.Execute(null);

            Assert.False(preview.ApproveCommand.CanExecute(null));
            Assert.True(System.IO.File.Exists(a));
            Assert.Empty(services.GetRequiredService<TransactionLedger>().All());
        }

        using var relaunched = Build();
        var store = relaunched.GetRequiredService<FindingsStore>();
        var overview = relaunched.GetRequiredService<OverviewViewModel>();
        store.Publish(new RecommendationSet([Recommend(new ExclusionList(), a)], []));

        Assert.Empty(overview.Items);
        Assert.Equal("Nothing needs your attention right now.", overview.Summary);
    }

    public void Dispose() => root.Dispose();

    private ServiceProvider Build() =>
        AppServices.Build(new AppPaths(root.Resolve("PCHealth")), s =>
        {
            s.AddSingleton<IPathResolver>(new PassThroughResolver());
            s.AddSingleton<IPlatformOperations>(new PlainPlatform());
            s.AddSingleton(new ProtectionPolicy([]));
        });

    private static PreviewViewModel Open(ServiceProvider services, Recommendation recommendation) =>
        services.GetRequiredService<Func<Recommendation, PreviewViewModel>>()(recommendation);

    private string File(string name, string contents) => root.CreateFile($"data/{name}", contents);

    private static Recommendation Recommend(ExclusionList exclusions, params string[] files) =>
        new RecommendationBuilder(new ProtectionPolicy([]), exclusions).Build(
        [
            new Finding(
                FindingCategory.AppResidue, "Possible leftover data", "Nothing claims it.",
                [new PathEvidence(Path.GetDirectoryName(files[0])!, "Application data folder.")],
                new Confidence(ConfidenceLevel.Medium, "No registration claims it."),
                new Impact("Moves the files to quarantine.", new EstimatedSize(files.Sum(f => new FileInfo(f).Length), "measured")),
                new Recovery(RecoveryMethod.Quarantine, "Restorable to the original path while in quarantine."),
                operations: files.Select(f =>
                {
                    var info = new FileInfo(f);
                    return new ProposedOperation(OperationKind.MoveToQuarantine, f, isDirectory: false, new MeasuredSize(info.Length), info.LastWriteTimeUtc);
                }).ToArray()),
        ]).Recommendations.Single();

    private sealed class PassThroughResolver : IPathResolver
    {
        public PathResolution Resolve(string path, LinkHandling links = LinkHandling.DoNotFollow) =>
            System.IO.File.Exists(path) || Directory.Exists(path)
                ? PathResolution.Resolved(path, System.IO.File.GetAttributes(path))
                : PathResolution.Rejected(PathRejectionReason.NotFound, "Not found.");
    }

    private sealed class PlainPlatform : IPlatformOperations
    {
        public ItemState? Inspect(string finalPath)
        {
            var f = new FileInfo(finalPath);
            return f.Exists ? new ItemState(false, f.Length, f.LastWriteTimeUtc, f.CreationTimeUtc, f.Attributes) : null;
        }

        public IReadOnlyList<string> ProcessesUsing(string finalPath) => [];

        public bool NeedsElevation(string finalPath) => false;

        public bool SameVolume(string path, string otherPath) => true;

        public string? CannotRecycle(string finalPath, long sizeBytes) => "not used in these tests.";

        public string MoveToRecycleBin(string finalPath, bool isDirectory) => throw new IOException("Not used in these tests.");

        public void MoveToQuarantine(string finalPath, string destination, bool isDirectory) => System.IO.File.Move(finalPath, destination);
    }
}

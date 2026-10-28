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

/// <summary>The composition root and shell view models, with the app's data folder in a temp root.</summary>
public sealed class ShellTests : IDisposable
{
    private readonly TempRoot root = new();
    private readonly ServiceProvider services;

    public ShellTests()
    {
        services = AppServices.Build(new AppPaths(root.Resolve("PCHealth")));
    }

    [Fact]
    public void Every_service_resolves_and_the_shell_starts_with_the_first_run()
    {
        var main = services.GetRequiredService<MainViewModel>();

        Assert.IsType<FirstRunViewModel>(main.Current);
        Assert.Equal(new[] { "Overview", "Storage", "Duplicates", "Organize", "Maintenance", "Activity", "Benchmark", "Settings", "Updates" }, main.Pages.Select(p => p.Title));
        Assert.NotNull(services.GetRequiredService<ExecutionGateway>());
        Assert.NotNull(services.GetRequiredService<Restorer>());
    }

    [Fact]
    public void Navigation_moves_between_the_shells_own_pages_only()
    {
        var main = services.GetRequiredService<MainViewModel>();
        var changed = new List<string?>();
        main.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var activity = main.Pages.OfType<ActivityViewModel>().Single();

        main.NavigateCommand.Execute(activity);

        Assert.Same(activity, main.Current);
        Assert.Contains(nameof(MainViewModel.Current), changed);
        Assert.False(main.NavigateCommand.CanExecute(new OverviewViewModel(new FindingsStore())));
        Assert.Throws<ArgumentException>(() => main.Current = new OverviewViewModel(new FindingsStore()));
    }

    [Fact]
    public void The_pause_control_is_bound_to_the_gateways_pause_state()
    {
        var pause = services.GetRequiredService<MainViewModel>().Pause;
        var changed = new List<string?>();
        pause.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var file = root.CreateFile("Temp/a.tmp", "a");

        pause.ToggleCommand.Execute(null);
        var recommendation = Recommend(file);
        var result = services.GetRequiredService<ExecutionGateway>().Execute(recommendation, recommendation.Operations);

        Assert.True(pause.IsPaused);
        Assert.Same(services.GetRequiredService<GlobalPause>(), services.GetRequiredService<GlobalPause>());
        Assert.True(services.GetRequiredService<GlobalPause>().IsPaused);
        Assert.Equal(ExecutionStatus.Refused, result.Status);
        Assert.Contains("paused", result.Summary);
        Assert.True(File.Exists(file));
        Assert.Contains(nameof(PauseViewModel.IsPaused), changed);
        Assert.Equal("Resume changes", pause.Label);
        Assert.True(File.Exists(services.GetRequiredService<AppPaths>().PauseState));

        pause.ToggleCommand.Execute(null);
        Assert.False(services.GetRequiredService<GlobalPause>().IsPaused);
    }

    [Fact]
    public void A_pause_set_elsewhere_shows_in_the_control()
    {
        var pause = services.GetRequiredService<PauseViewModel>();
        var changed = new List<string?>();
        pause.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        services.GetRequiredService<GlobalPause>().Pause();

        Assert.True(pause.IsPaused);
        Assert.Contains(nameof(PauseViewModel.Status), changed);
    }

    [Fact]
    public void Startup_settles_interrupted_operations_before_anything_new()
    {
        var ledger = services.GetRequiredService<TransactionLedger>();
        var left = root.CreateFile("Temp/left.tmp", "l");
        var interrupted = new LedgerTransaction(
            Guid.NewGuid(), DateTimeOffset.UtcNow, LedgerOperation.MoveToQuarantine, TransactionState.Pending, "Interrupted.", [],
            [new LedgerItem(left, IsDirectory: false, new ItemMetadata(1))], 1, null, RecoveryMethod.Quarantine, "Restorable.");
        ledger.Save(interrupted);

        var result = services.GetRequiredService<StartupTasks>().Run();

        Assert.Equal(interrupted.Id, Assert.Single(result.Interrupted.Resolved).Id);
        Assert.Empty(ledger.InState(TransactionState.Pending));
        Assert.True(File.Exists(left));
    }

    public void Dispose()
    {
        services.Dispose();
        root.Dispose();
    }

    private static Recommendation Recommend(string file)
    {
        var info = new FileInfo(file);
        return new RecommendationBuilder(new ProtectionPolicy([]), new ExclusionList()).Build(
        [
            new Finding(
                FindingCategory.Cache, "Temporary files", "They are regenerable.",
                [new PathEvidence(Path.GetDirectoryName(file)!, "Known location.")],
                new Confidence(ConfidenceLevel.High, "Known location."),
                new Impact("Moves 1 file to the Recycle Bin."),
                new Recovery(RecoveryMethod.RecycleBin, "Restorable until the Recycle Bin is emptied."),
                operations: [new ProposedOperation(OperationKind.MoveToRecycleBin, file, isDirectory: false, new MeasuredSize(info.Length), info.LastWriteTimeUtc)]),
        ]).Recommendations.Single();
    }
}

using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Execution;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;
using PCHealth.Testing;

namespace PCHealth.App.Tests;

/// <summary>"Clean up all suggested" (T-67), through the real gateway on fixture files only.</summary>
public sealed class CleanUpAllTests : IDisposable
{
    private readonly TempRoot root = new();

    [Fact]
    public void Every_selected_action_runs_as_its_own_transaction_after_one_approval()
    {
        using var services = TestServices.Build(root);
        var (a, b, c) = (root.CreateFile("one/a.tmp", "aa"), root.CreateFile("one/b.tmp", "bbb"), root.CreateFile("two/c.tmp", "c"));
        var store = services.GetRequiredService<FindingsStore>();
        store.Publish(new RecommendationSet([PreviewTests.Recommend(new ExclusionList(), a, b), PreviewTests.Recommend(new ExclusionList(), c)], []));
        var main = services.GetRequiredService<MainViewModel>();
        main.Current = main.Pages[0];
        var overview = (OverviewViewModel)main.Current;

        overview.CleanUpAllCommand.Execute(null);
        var batch = Assert.IsType<BatchPreviewViewModel>(main.Current);

        Assert.Equal(2, batch.Entries.Count);
        Assert.Equal([a, b], batch.Entries[0].Paths.Select(p => p.Split(" · ")[0]));
        Assert.StartsWith("2 of 2 suggested actions selected, 3 items: about 6 bytes", batch.ExpectedEffect);
        Assert.Empty(services.GetRequiredService<TransactionLedger>().All());

        batch.ApproveCommand.Execute(null);

        Assert.All(batch.Entries, e => Assert.Equal(ExecutionStatus.Completed, e.Execution!.Status));
        Assert.False(File.Exists(a) || File.Exists(b) || File.Exists(c));
        Assert.Equal(2, services.GetRequiredService<TransactionLedger>().All().Count);
        Assert.StartsWith("Done: all 2 actions completed.", batch.Result);
        Assert.False(batch.ApproveCommand.CanExecute(null));

        batch.BackCommand.Execute(null);
        Assert.Same(overview, main.Current);
        Assert.Empty(store.Latest!.Recommendations);
    }

    [Fact]
    public void An_action_left_out_is_not_changed_and_stays_suggested()
    {
        using var services = TestServices.Build(root);
        var (a, c) = (root.CreateFile("one/a.tmp", "aa"), root.CreateFile("two/c.tmp", "c"));
        var store = services.GetRequiredService<FindingsStore>();
        var kept = PreviewTests.Recommend(new ExclusionList(), c);
        store.Publish(new RecommendationSet([PreviewTests.Recommend(new ExclusionList(), a), kept], []));
        var batch = services.GetRequiredService<Func<IReadOnlyList<Recommendation>, BatchPreviewViewModel>>()(store.Latest!.Recommendations);

        batch.Entries[1].IsSelected = false;
        Assert.StartsWith("1 of 2 suggested actions selected, 1 item: about 2 bytes", batch.ExpectedEffect);
        batch.Entries[0].IsSelected = false;
        Assert.False(batch.ApproveCommand.CanExecute(null));
        batch.Entries[0].IsSelected = true;
        batch.ApproveCommand.Execute(null);

        Assert.False(File.Exists(a));
        Assert.True(File.Exists(c));
        Assert.Null(batch.Entries[1].Execution);
        Assert.Single(services.GetRequiredService<TransactionLedger>().All());
    }

    public void Dispose() => root.Dispose();
}

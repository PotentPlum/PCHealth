using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Core;
using PCHealth.Execution;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Testing;

namespace PCHealth.App.Tests;

public sealed class ActivityTests : IDisposable
{
    private readonly TempRoot root = new();

    [Fact]
    public void The_history_says_what_changed_why_and_with_what_outcome()
    {
        using var services = TestServices.Build(root);
        var file = root.CreateFile("data/a.tmp", "aa");
        Approve(services, file);
        var activity = services.GetRequiredService<ActivityViewModel>();

        activity.Refresh();
        var entry = Assert.Single(activity.Entries);

        Assert.Equal("Moved 1 item to quarantine", entry.WhatChanged);
        Assert.Contains("Possible leftover data", entry.Why);
        Assert.Equal("1 items changed and checked.", entry.Outcome);
        Assert.Equal($"{file}: done", Assert.Single(entry.Items));
        Assert.StartsWith("Kept in quarantine until", entry.Deadline);
        Assert.True(entry.CanUndo);
        Assert.Contains("can be put back", entry.UndoTerms);
    }

    [Fact]
    public void Undo_is_offered_where_supported_and_runs_through_the_gateway()
    {
        using var services = TestServices.Build(root);
        var file = root.CreateFile("data/a.tmp", "aa");
        Approve(services, file);
        var main = services.GetRequiredService<MainViewModel>();
        main.Current = main.Pages.OfType<ActivityViewModel>().Single();
        var activity = (ActivityViewModel)main.Current;

        activity.Entries[0].UndoCommand.Execute(null);

        Assert.Equal("aa", File.ReadAllText(file));
        Assert.StartsWith("Undo: 1 items are back", activity.Message);
        Assert.Equal(2, activity.Entries.Count);
        var restore = activity.Entries.Single(e => e.Transaction.Operation == LedgerOperation.Restore);
        Assert.Equal("Put 1 item back", restore.WhatChanged);
        Assert.False(restore.CanUndo);
        Assert.False(restore.UndoCommand.CanExecute(null));
        var original = activity.Entries.Single(e => e.Transaction.Operation == LedgerOperation.MoveToQuarantine);
        Assert.False(original.CanUndo);
        Assert.Equal("Nothing from this change is left in quarantine.", original.UndoTerms);
    }

    [Fact]
    public void Limits_are_shown_honestly_where_undo_is_not_offered()
    {
        using var services = TestServices.Build(root);
        var ledger = services.GetRequiredService<TransactionLedger>();
        var recycled = new LedgerTransaction(
            Guid.NewGuid(), DateTimeOffset.UtcNow, LedgerOperation.MoveToRecycleBin, TransactionState.Pending, "Temporary files.", [],
            [new LedgerItem(root.Resolve("x.tmp"), IsDirectory: false, new ItemMetadata(1), Status: ItemStatus.Done, RecoveryLocation: root.Resolve("bin/x"))],
            1, 1, RecoveryMethod.RecycleBin, "Restorable until the Recycle Bin is emptied.");
        ledger.Save(recycled);
        ledger.Save(recycled with { State = TransactionState.Applied });
        ledger.Save(recycled with { State = TransactionState.Verified, Outcome = "1 items changed and checked." });
        var activity = services.GetRequiredService<ActivityViewModel>();

        activity.Refresh();
        var entry = Assert.Single(activity.Entries);

        Assert.Equal("Moved 1 item to the Recycle Bin", entry.WhatChanged);
        Assert.False(entry.CanUndo);
        Assert.StartsWith("Open the Recycle Bin and choose Restore.", entry.UndoTerms);
        Assert.Null(entry.Deadline);
    }

    [Fact]
    public void Undo_waits_while_changes_are_paused_and_history_stays_visible()
    {
        using var services = TestServices.Build(root);
        var file = root.CreateFile("data/a.tmp", "aa");
        Approve(services, file);
        var activity = services.GetRequiredService<ActivityViewModel>();
        activity.Refresh();
        services.GetRequiredService<GlobalPause>().Pause();

        activity.Entries[0].UndoCommand.Execute(null);

        Assert.False(File.Exists(file));
        Assert.StartsWith("Not undone: Changes are paused", activity.Message);
        Assert.Single(activity.Entries);
    }

    [Fact]
    public void Expired_quarantine_is_not_purged_at_startup_while_paused()
    {
        using var services = TestServices.Build(root);
        services.GetRequiredService<GlobalPause>().Pause();

        Assert.Null(services.GetRequiredService<StartupTasks>().Run().Purged);
    }

    public void Dispose() => root.Dispose();

    private static void Approve(ServiceProvider services, string file)
    {
        var recommendation = PreviewTests.Recommend(new ExclusionList(), file);
        var result = services.GetRequiredService<ExecutionGateway>().Execute(recommendation, recommendation.Operations);
        Assert.Equal(ExecutionStatus.Completed, result.Status);
    }
}

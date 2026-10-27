using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Core;
using PCHealth.Duplicates;
using PCHealth.Execution;
using PCHealth.Inventory;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;
using PCHealth.Testing;

namespace PCHealth.App.Tests;

/// <summary>The Duplicates page (T-40), through the real policy and gateway on fixture files and a fixture Recycle Bin.</summary>
public sealed class DuplicatesPageTests : IDisposable
{
    private readonly TempRoot root = new();

    [Fact]
    public void Groups_are_listed_biggest_first_with_times_side_by_side_and_their_evidence()
    {
        using var services = TestServices.Build(root);
        var small = root.CreateFile("files/a/small.txt", "small");
        root.CreateFile("files/b/small.txt", "small");
        foreach (var folder in new[] { "a", "b", "c" })
        {
            root.CreateFile($"files/{folder}/big.bin", new string('x', 100));
        }

        Publish(services);
        var page = services.GetRequiredService<DuplicatesViewModel>();

        Assert.Equal(new[] { "3 identical copies of big.bin", "2 identical copies of small.txt" }, page.Groups.Select(g => g.Title));
        Assert.StartsWith("2 groups of identical files", page.Summary);
        var copy = page.Groups[1].Copies.Single(c => c.Path == small);
        Assert.StartsWith("5 bytes", copy.Size);
        Assert.Equal($"Modified {DuplicatesViewModel.Time(File.GetLastWriteTimeUtc(small))}", copy.Modified);
        Assert.Equal($"Created {DuplicatesViewModel.Time(File.GetCreationTimeUtc(small))}", copy.Created);
        Assert.All(page.Groups.SelectMany(g => g.Copies), c => Assert.False(c.IsSelected));

        var evidence = page.Groups[0].Evidence;
        Assert.Equal("Size: every file is exactly 100 bytes.", evidence[0]);
        Assert.StartsWith("Partial check: the start and end of every file give the same digest (SHA-256/head+tail(", evidence[1]);
        Assert.StartsWith("Full check: the entire contents of every file give the same hash (SHA-256:", evidence[2]);
        Assert.StartsWith("Same-file check: the 3 paths are 3 separate files", evidence[3]);
        Assert.Contains(evidence, e => e.StartsWith(root.Resolve("files/c/big.bin"), StringComparison.Ordinal));
        Assert.False(page.Groups[0].ShowEvidence);
        page.Groups[0].ToggleEvidenceCommand.Execute(null);
        Assert.True(page.Groups[0].ShowEvidence);
    }

    [Fact]
    public void At_least_one_copy_stays_and_an_excluded_copy_is_never_removed()
    {
        using var services = TestServices.Build(root);
        var (a, b) = (root.CreateFile("files/a/x.bin", "same"), root.CreateFile("files/b/x.bin", "same"));
        var (main, page) = Open(services);
        var group = page.Groups[0];

        page.RemoveCommand.Execute(group);
        Assert.Equal("Choose at least one copy to remove.", group.Message);

        foreach (var copy in group.Copies)
        {
            copy.IsSelected = true;
        }

        Assert.Equal("Move 2 selected copies to the Recycle Bin…", group.RemoveText);
        page.RemoveCommand.Execute(group);
        Assert.StartsWith("At least one copy has to stay.", group.Message);
        Assert.Same(page, main.Current);

        services.GetRequiredService<ExclusionList>().ExcludeFile(b, DateTimeOffset.UtcNow);
        page.Refresh();
        group = page.Groups[0];
        Assert.Equal($"Cannot be removed: you excluded '{b}'.", Copy(group, b).Blocked);
        Copy(group, b).IsSelected = true;
        page.RemoveCommand.Execute(group);
        Assert.Equal($"You excluded '{b}', so '{b}' cannot be removed.", group.Message);

        Assert.Same(page, main.Current);
        Assert.Empty(services.GetRequiredService<TransactionLedger>().All());
        Assert.True(File.Exists(a) && File.Exists(b));
    }

    [Fact]
    public void A_copy_that_changed_between_scan_and_approval_is_left_alone()
    {
        using var services = TestServices.Build(root);
        var (a, b, c) = (root.CreateFile("files/a/x.bin", "same"), root.CreateFile("files/b/x.bin", "same"), root.CreateFile("files/c/x.bin", "same"));
        var (main, page) = Open(services);
        Copy(page.Groups[0], b).IsSelected = true;
        Copy(page.Groups[0], c).IsSelected = true;

        page.RemoveCommand.Execute(page.Groups[0]);
        var preview = Assert.IsType<PreviewViewModel>(main.Current);
        File.AppendAllText(b, " and more");
        preview.ApproveCommand.Execute(null);

        Assert.Equal(ExecutionStatus.PartiallyCompleted, preview.Execution!.Status);
        Assert.Contains("changed since it was found", preview.Execution.Items.Single(i => i.Operation.Path == b).Reason);
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.False(File.Exists(c));
    }

    [Fact]
    public void A_copy_is_left_alone_when_the_copy_meant_to_stay_changed()
    {
        using var services = TestServices.Build(root);
        var (a, b) = (root.CreateFile("files/a/x.bin", "same"), root.CreateFile("files/b/x.bin", "same"));
        var (main, page) = Open(services);
        Copy(page.Groups[0], b).IsSelected = true;

        page.RemoveCommand.Execute(page.Groups[0]);
        var preview = Assert.IsType<PreviewViewModel>(main.Current);
        File.WriteAllText(a, "SAME");
        preview.ApproveCommand.Execute(null);

        Assert.Equal(ExecutionStatus.Failed, preview.Execution!.Status);
        Assert.Contains("no longer has the same contents", Assert.Single(preview.Execution.Items).Reason);
        Assert.Equal("same", File.ReadAllText(b));
    }

    [Fact]
    public void A_removed_copy_goes_to_the_recycle_bin_and_undo_from_activity_brings_it_back()
    {
        using var services = TestServices.Build(root);
        var (a, b) = (root.CreateFile("files/a/x.bin", "same"), root.CreateFile("files/b/x.bin", "same"));
        var (main, page) = Open(services);
        Copy(page.Groups[0], b).IsSelected = true;

        page.RemoveCommand.Execute(page.Groups[0]);
        var preview = Assert.IsType<PreviewViewModel>(main.Current);
        Assert.Equal(b, Assert.Single(preview.Items).Path);
        Assert.Contains($"Keeps '{a}'", preview.Recommendation.WhatWillChange);
        preview.ApproveCommand.Execute(null);

        Assert.Equal(ExecutionStatus.Completed, preview.Execution!.Status);
        Assert.False(File.Exists(b));
        Assert.Equal(LedgerOperation.MoveToRecycleBin, services.GetRequiredService<TransactionLedger>().All().Single().Operation);
        preview.BackCommand.Execute(null);
        Assert.Same(page, main.Current);
        Assert.Empty(page.Groups);

        var activity = services.GetRequiredService<ActivityViewModel>();
        activity.Refresh();
        var entry = Assert.Single(activity.Entries);
        Assert.Equal("Moved 1 item to the Recycle Bin", entry.WhatChanged);
        Assert.True(entry.CanUndo);
        entry.UndoCommand.Execute(null);

        Assert.Equal("same", File.ReadAllText(b));
        Assert.StartsWith("Undo: 1 items are back", activity.Message);
    }

    public void Dispose() => root.Dispose();

    private static DuplicateCopyViewModel Copy(DuplicateGroupViewModel group, string path) => group.Copies.Single(c => c.Path == path);

    private (MainViewModel Main, DuplicatesViewModel Page) Open(ServiceProvider services)
    {
        Publish(services);
        var main = services.GetRequiredService<MainViewModel>();
        var page = services.GetRequiredService<DuplicatesViewModel>();
        main.Current = page;
        return (main, page);
    }

    private void Publish(ServiceProvider services)
    {
        var scan = new RecursiveScanner().Scan(root.Resolve("files"), cancellationToken: TestContext.Current.CancellationToken);
        var groups = new DuplicateFinder().Find(scan.Entries.OfType<FileEntry>(), TestContext.Current.CancellationToken).Groups;
        services.GetRequiredService<FindingsStore>().Publish(new FullDiscovery(new RecommendationSet([], []), StorageSummary.From(scan), scan.Statistics, groups));
    }
}

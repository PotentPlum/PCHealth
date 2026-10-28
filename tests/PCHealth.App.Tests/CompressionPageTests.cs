using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Execution;
using PCHealth.Inventory;
using PCHealth.Ledger;
using PCHealth.Policy.Recommendations;
using PCHealth.Testing;

namespace PCHealth.App.Tests;

/// <summary>Compression from the Storage page (T-56): estimate, preview, gateway, measured saving in Activity, and Undo.</summary>
public sealed class CompressionPageTests : IDisposable
{
    private readonly TempRoot root = new();

    public CompressionPageTests()
    {
        root.CreateFile("user/Logs/app.log", string.Concat(Enumerable.Repeat("2026-10-28 12:00:00 INFO request served\n", 2000)));
        root.CreateFile("user/Logs/web.log", string.Concat(Enumerable.Repeat("2026-10-28 12:00:01 WARN slow response\n", 2000)));
        root.CreateFile("user/Logs/photo.jpg", new string('j', 10_000));
    }

    public void Dispose() => root.Dispose();

    [Fact]
    public async Task An_estimate_is_previewed_with_its_trade_offs_and_activity_shows_the_measured_saving()
    {
        using var services = TestServices.Build(root);
        var (main, storage) = Open(services);
        Assert.False(storage.HasEstimate);

        await storage.EstimateCompressionCommand.ExecuteAsync(storage.Folders.Single(f => f.Name == "Logs"));

        Assert.True(storage.HasEstimate);
        var lines = storage.CompressionEstimate;
        Assert.Equal($"Compression estimate for {root.Resolve("user/Logs")}", lines[0]);
        Assert.Contains("This is an estimate, not a measurement.", lines[1]);
        Assert.Contains(lines, l => l.StartsWith("How it was estimated: Xpress8K compression of a", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Windows decompresses it", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Savings depend on the files", StringComparison.Ordinal));

        storage.CompressCommand.Execute(null);
        var preview = Assert.IsType<PreviewViewModel>(main.Current);
        Assert.Equal(2, preview.Items.Count);
        Assert.StartsWith("2 of 2 files selected: about", preview.ExpectedEffect);
        Assert.Contains("(estimated from Xpress8K compression", preview.ExpectedEffect);
        Assert.Contains("processor time", preview.Afterward);
        Assert.StartsWith("Yes: Undo decompresses the files.", preview.RecoveryTerms);
        preview.ApproveCommand.Execute(null);

        Assert.Equal(ExecutionStatus.Completed, preview.Execution!.Status);
        preview.BackCommand.Execute(null);
        Assert.Same(storage, main.Current);
        Assert.False(storage.HasEstimate);
        var compression = (TestServices.FakeCompression)services.GetRequiredService<IFileCompression>();
        Assert.Equal(2, compression.Compressed.Count);

        var activity = services.GetRequiredService<ActivityViewModel>();
        activity.Refresh();
        var entry = Assert.Single(activity.Entries);
        var measured = services.GetRequiredService<TransactionLedger>().All().Single().ObservedBytes!.Value;
        Assert.True(measured > 0);
        Assert.Equal($"Compressed 2 items: {SizeText.Bytes(measured)} saved (measured)", entry.WhatChanged);
        entry.UndoCommand.Execute(null);

        Assert.Empty(compression.Compressed);
    }

    [Fact]
    public async Task Compression_is_only_ever_offered_when_asked_and_never_because_files_are_old()
    {
        foreach (var file in Directory.EnumerateFiles(root.Resolve("user/Logs")))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddYears(-5));
        }

        using var services = TestServices.Build(root);
        var (_, storage) = Open(services);

        Assert.False(storage.HasEstimate);
        Assert.False(storage.CompressCommand.CanExecute(null));
        Assert.DoesNotContain(services.GetRequiredService<FindingsStore>().Latest!.Recommendations, r => r.Category == Core.FindingCategory.Compression);

        await storage.EstimateCompressionCommand.ExecuteAsync(null);
        foreach (var word in new[] { "old", "not used", "months", "years", "inactive" })
        {
            Assert.DoesNotContain(storage.CompressionEstimate, l => l.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }

    private (MainViewModel Main, StorageViewModel Storage) Open(ServiceProvider services)
    {
        var scan = new RecursiveScanner().Scan(root.Resolve("user"), cancellationToken: TestContext.Current.CancellationToken);
        services.GetRequiredService<FindingsStore>().Publish(new FullDiscovery(new RecommendationSet([], []), StorageSummary.From(scan), scan.Statistics));
        var main = services.GetRequiredService<MainViewModel>();
        var storage = services.GetRequiredService<StorageViewModel>();
        main.Current = storage;
        return (main, storage);
    }
}

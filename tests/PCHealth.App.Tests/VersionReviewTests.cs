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

/// <summary>The "possible versions, not copies" tab of the Duplicates page (T-41), on fixture files and a fixture Recycle Bin.</summary>
public sealed class VersionReviewTests : IDisposable
{
    private readonly TempRoot root = new();

    [Fact]
    public async Task Versions_never_appear_among_verified_duplicates_and_nothing_is_preselected()
    {
        using var services = TestServices.Build(root);
        var profile = root.CreateDirectory("profile");
        var temp = root.CreateDirectory("profile/AppData/Local/Temp");

        // Same size, same start and end, different middle: candidates by size and partial digest, rejected by the full hash.
        var content = new string('p', (int)Discovery.DuplicateMinimumBytes).ToCharArray();
        root.CreateFile("profile/Documents/Plan v1.docx", new string(content));
        content[content.Length / 2] = 'q';
        root.CreateFile("profile/Documents/Plan v2.docx", new string(content));
        var discovery = new Discovery(
            new FirstRunTests.Locations(temp, root.Resolve("profile/AppData/Local")), new FirstRunTests.NoRegistrations(), new RecursiveScanner(), null,
            new ProtectionPolicy([]), new ExclusionList(), profile, TimeProvider.System);

        var full = await discovery.FullAsync(null, TestContext.Current.CancellationToken);
        services.GetRequiredService<FindingsStore>().Publish(full);
        var page = services.GetRequiredService<DuplicatesViewModel>();

        Assert.Empty(full.Duplicates!);
        Assert.Empty(page.Groups);
        Assert.DoesNotContain(full.Recommendations.ToAct, r => r.Category == FindingCategory.VersionCandidate);
        Assert.Contains(full.Recommendations.ForReview, r => r.Category == FindingCategory.VersionCandidate);
        var group = Assert.Single(page.Versions);
        Assert.Equal(2, group.Files.Count);
        Assert.All(group.Files, f => Assert.False(f.IsSelected));
        Assert.Equal("Possible versions, not copies", page.VersionsTab);
        Assert.Contains("A similar name is not evidence", page.VersionsExplanation);
        Assert.EndsWith("Nothing is selected.", page.VersionsSummary);
    }

    [Fact]
    public void Chosen_versions_go_to_the_recycle_bin_and_the_others_stay()
    {
        using var services = TestServices.Build(root);
        var (v1, v2, final) = (root.CreateFile("files/Plan v1.docx", "a"), root.CreateFile("files/Plan v2.docx", "bb"), root.CreateFile("files/Plan final.docx", "ccc"));
        var store = services.GetRequiredService<FindingsStore>();
        var scan = new RecursiveScanner().Scan(root.Resolve("files"), cancellationToken: TestContext.Current.CancellationToken);
        var versions = VersionCandidates.Find(scan.Entries.OfType<FileEntry>());
        var recommendations = services.GetRequiredService<Func<RecommendationBuilder>>()().Build(versions.Select(FindingSources.FromVersionCandidates));
        store.Publish(new FullDiscovery(recommendations, StorageSummary.From(scan), scan.Statistics, [], versions));
        var main = services.GetRequiredService<MainViewModel>();
        var page = services.GetRequiredService<DuplicatesViewModel>();
        main.Current = page;
        var group = Assert.Single(page.Versions);

        page.RemoveVersionsCommand.Execute(group);
        Assert.Equal("Choose at least one file to remove.", group.Message);
        Assert.Same(page, main.Current);

        group.Files.Single(f => f.Path == v1).IsSelected = true;
        Assert.Equal("Move 1 selected file to the Recycle Bin…", group.RemoveText);
        page.RemoveVersionsCommand.Execute(group);
        var preview = Assert.IsType<PreviewViewModel>(main.Current);
        Assert.Equal(v1, Assert.Single(preview.Items).Path);
        preview.ApproveCommand.Execute(null);

        Assert.Equal(ExecutionStatus.Completed, preview.Execution!.Status);
        Assert.False(File.Exists(v1));
        Assert.True(File.Exists(v2) && File.Exists(final));
        Assert.Equal(LedgerOperation.MoveToRecycleBin, services.GetRequiredService<TransactionLedger>().All().Single().Operation);

        preview.BackCommand.Execute(null);
        Assert.Same(page, main.Current);
        Assert.Equal(new[] { v2, final }.Order(StringComparer.Ordinal), Assert.Single(page.Versions).Files.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(store.Latest!.Recommendations, r => r.Category == FindingCategory.VersionCandidate);
    }

    public void Dispose() => root.Dispose();
}

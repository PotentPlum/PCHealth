using System.IO;
using PCHealth.App.ViewModels;
using PCHealth.Core;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;

namespace PCHealth.App.Tests;

public class OverviewTests
{
    private static readonly string Base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PCHealth.Tests", "overview"));

    [Fact]
    public void Before_discovery_the_overview_says_so()
    {
        var overview = new OverviewViewModel(new FindingsStore());

        Assert.Empty(overview.Items);
        Assert.Equal("No discovery has run yet.", overview.Summary);
    }

    [Fact]
    public void A_few_findings_are_shown_actions_first_largest_benefit_first()
    {
        var store = new FindingsStore();
        var overview = new OverviewViewModel(store);

        store.Publish(Set(Act("small", 10), Act("large", 9000), Review("odd"), Act("medium", 500), Review("odd2")));

        Assert.Equal(new[] { "large", "medium", "small" }, overview.Items.Select(i => i.Recommendation.Finding.Title));
        Assert.Equal("3 suggested actions and 2 items to review. The most useful are shown first.", overview.Summary);
    }

    [Fact]
    public void Each_finding_answers_the_six_questions_with_three_separate_dimensions()
    {
        var store = new FindingsStore();
        var overview = new OverviewViewModel(store);
        store.Publish(Set(Act("cache", 2048)));

        var item = Assert.Single(overview.Items);

        Assert.Equal($"cache ({P("cache")})", item.WhatWeFound);
        Assert.Equal("It is regenerable.", item.WhyItMatters);
        Assert.Equal("Known cache location.", item.Evidence);
        Assert.Equal("Moves 1 file to the Recycle Bin.", item.WhatWillChange);
        Assert.Equal("It is rebuilt on next use.", item.WhatCouldHappenAfterward);
        Assert.StartsWith("Yes, from the Recycle Bin.", item.CanIRecover);
        Assert.Equal("Confidence: High", item.Confidence);
        Assert.Equal("Impact: about 2 KB (estimated: sum of sizes)", item.Impact);
        Assert.Equal("Recovery: Recycle Bin", item.Recovery);
        Assert.Null(item.ReviewReason);
    }

    [Fact]
    public void Details_are_shown_on_demand()
    {
        var store = new FindingsStore();
        var overview = new OverviewViewModel(store);
        store.Publish(Set(Act("cache", 10)));
        var item = overview.Items[0];

        Assert.False(item.ShowDetails);
        item.ToggleDetailsCommand.Execute(null);

        Assert.True(item.ShowDetails);
        Assert.Contains(item.Details, d => d.StartsWith("Confidence rationale: Known location.", StringComparison.Ordinal));
        Assert.Contains(item.Details, d => d.StartsWith($"Would move to the Recycle Bin: {Path.Combine(P("cache"), "a.tmp")}", StringComparison.Ordinal));
        Assert.Contains(item.Details, d => d == "Evidence: Known cache location.");
    }

    [Fact]
    public void Only_actions_can_be_previewed_and_hidden_ones_are_left_out()
    {
        var store = new FindingsStore();
        var overview = new OverviewViewModel(store);
        Recommendation? requested = null;
        overview.PreviewRequested += (_, r) => requested = r;
        store.Publish(Set(Act("cache", 10), Review("odd")));

        overview.PreviewCommand.Execute(overview.Items[0]);

        Assert.Same(overview.Items[0].Recommendation, requested);
        Assert.False(overview.PreviewCommand.CanExecute(overview.Items[1]));
        Assert.StartsWith("For review:", overview.Items[1].ReviewReason);

        overview.IsHidden = r => r.Finding.Title == "cache";
        overview.Refresh();
        Assert.Equal("odd", Assert.Single(overview.Items).Recommendation.Finding.Title);
    }

    private static string P(string relative) => Path.Combine(Base, relative);

    private static RecommendationSet Set(params Finding[] findings) =>
        new RecommendationBuilder(new ProtectionPolicy([]), new ExclusionList()).Build(findings);

    private static Finding Act(string name, long bytes) => new(
        FindingCategory.Cache, name, "It is regenerable.",
        [new PathEvidence(P(name), "Known cache location.")],
        new Confidence(ConfidenceLevel.High, "Known location."),
        new Impact("Moves 1 file to the Recycle Bin.", new EstimatedSize(bytes, "sum of sizes"), "It is rebuilt on next use."),
        new Recovery(RecoveryMethod.RecycleBin, "Restorable until the Recycle Bin is emptied."),
        operations: [new ProposedOperation(OperationKind.MoveToRecycleBin, Path.Combine(P(name), "a.tmp"), isDirectory: false, new MeasuredSize(bytes))]);

    private static Finding Review(string name) => new(
        FindingCategory.AppResidue, name, "Maybe left over.",
        [new PathEvidence(P(name), "Application data folder.")],
        new Confidence(ConfidenceLevel.Low, "Uncertain."),
        new Impact("Moves the folder to quarantine."),
        new Recovery(RecoveryMethod.Quarantine, "Restorable while in quarantine."),
        reviewOnly: true,
        [new ProposedOperation(OperationKind.MoveToQuarantine, P(name), isDirectory: true, new MeasuredSize(1))]);
}

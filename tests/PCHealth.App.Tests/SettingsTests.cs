using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Execution;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;
using PCHealth.Testing;

namespace PCHealth.App.Tests;

/// <summary>Settings and local data, with the app's data folder and every fixture file in a temp root.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly TempRoot root = new();

    [Fact]
    public void The_quarantine_period_is_saved_applied_to_new_items_and_shown_in_recommendations()
    {
        var file = root.CreateFile("data/a.tmp", "aa");
        using (var services = TestServices.Build(root))
        {
            var settings = services.GetRequiredService<SettingsViewModel>();
            var store = services.GetRequiredService<FindingsStore>();
            store.Publish(new RecommendationSet([PreviewTests.Recommend(new ExclusionList(), file)], []));

            settings.RetentionDays = "0";
            settings.SaveRetentionCommand.Execute(null);
            Assert.Equal("Enter a whole number of days from 1 to 90.", settings.Message);
            Assert.Equal(TimeSpan.FromDays(14), services.GetRequiredService<QuarantineStore>().Retention);

            settings.RetentionDays = "30";
            settings.SaveRetentionCommand.Execute(null);

            Assert.Equal(TimeSpan.FromDays(30), services.GetRequiredService<QuarantineStore>().Retention);
            var rebuilt = Assert.Single(store.Latest!.Recommendations);
            Assert.Contains("for 30 days", rebuilt.CanIRecover);
            var result = services.GetRequiredService<ExecutionGateway>().Execute(rebuilt, rebuilt.Operations);
            Assert.Equal(ExecutionStatus.Completed, result.Status);
            var deadline = services.GetRequiredService<TransactionLedger>().Find(result.TransactionId!.Value)!.RetentionDeadlineUtc!.Value;
            Assert.InRange(deadline - DateTimeOffset.UtcNow, TimeSpan.FromDays(30) - TimeSpan.FromMinutes(5), TimeSpan.FromDays(30));
        }

        using var relaunched = TestServices.Build(root);
        Assert.Equal(TimeSpan.FromDays(30), relaunched.GetRequiredService<QuarantineStore>().Retention);
        Assert.Equal("30", relaunched.GetRequiredService<SettingsViewModel>().RetentionDays);
    }

    [Fact]
    public void Exclusions_can_be_added_and_removed_and_recommendations_follow()
    {
        using var services = TestServices.Build(root);
        var settings = services.GetRequiredService<SettingsViewModel>();
        var store = services.GetRequiredService<FindingsStore>();
        var file = root.CreateFile("data/a.tmp", "aa");
        store.Publish(new RecommendationSet([PreviewTests.Recommend(new ExclusionList(), file)], []));

        settings.NewExclusion = "relative\\path";
        settings.AddExclusionCommand.Execute(null);
        Assert.StartsWith("Enter a full path", settings.Message);
        Assert.Empty(settings.Exclusions);

        settings.NewExclusion = root.Resolve("data");
        settings.AddExclusionCommand.Execute(null);

        Assert.Equal($"Folder {root.Resolve("data")} and everything in it", Assert.Single(settings.Exclusions).Text);
        Assert.Empty(store.Latest!.Recommendations);
        Assert.Single(store.Latest.Excluded);
        Assert.Single(ExclusionList.Load(services.GetRequiredService<AppPaths>().Exclusions).Entries);

        settings.Exclusions[0].RemoveCommand.Execute(null);

        Assert.Empty(settings.Exclusions);
        Assert.Single(store.Latest!.Recommendations);
        Assert.Empty(ExclusionList.Load(services.GetRequiredService<AppPaths>().Exclusions).Entries);
    }

    [Fact]
    public void Clearing_is_refused_while_quarantine_holds_items_and_then_erases_the_products_data()
    {
        using var services = TestServices.Build(root);
        var paths = services.GetRequiredService<AppPaths>();
        var settings = services.GetRequiredService<SettingsViewModel>();
        var gateway = services.GetRequiredService<ExecutionGateway>();
        var file = root.CreateFile("data/a.tmp", "aa");
        var recommendation = PreviewTests.Recommend(new ExclusionList(), file);
        var move = gateway.Execute(recommendation, recommendation.Operations);
        settings.NewExclusion = root.Resolve("elsewhere");
        settings.AddExclusionCommand.Execute(null);
        File.WriteAllText(FirstRunViewModel.MarkerPath(paths), "done");

        settings.Refresh();
        settings.ClearConfirmed = true;

        Assert.Contains("History of changes: 1 recorded change", settings.StoredData[0]);
        Assert.StartsWith("Quarantine: 1 item, 2 bytes", settings.StoredData[1]);
        Assert.Equal("Exclusions: 1", settings.StoredData[2]);
        Assert.StartsWith("1 item is still in quarantine", settings.ClearBlockedBy);
        Assert.False(settings.ClearCommand.CanExecute(null));

        gateway.Undo(move.TransactionId!.Value);
        settings.ClearConfirmed = false;
        settings.Refresh();
        Assert.Null(settings.ClearBlockedBy);
        Assert.False(settings.ClearCommand.CanExecute(null));

        settings.ClearConfirmed = true;
        settings.ClearCommand.Execute(null);

        Assert.StartsWith("Cleared 2 recorded changes, 1 exclusion", settings.Message);
        Assert.Empty(services.GetRequiredService<TransactionLedger>().All());
        Assert.Empty(services.GetRequiredService<ExclusionList>().Entries);
        Assert.False(File.Exists(paths.Exclusions));
        Assert.True(FirstRunViewModel.IsNeeded(paths));
        Assert.True(File.Exists(file));
        Assert.False(settings.ClearConfirmed);
    }

    public void Dispose() => root.Dispose();
}

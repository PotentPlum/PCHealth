using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Maintenance;
using PCHealth.Testing;

namespace PCHealth.App.Tests;

/// <summary>The Maintenance page (T-59) on fake startup sources and scripted process samples. Nothing is changed.</summary>
public sealed class MaintenancePageTests : IDisposable
{
    private readonly TempRoot root = new();

    public void Dispose() => root.Dispose();

    [Fact]
    public async Task Startup_items_are_listed_with_their_mechanism_evidence_and_how_to_change_them()
    {
        using var services = TestServices.Build(root);
        var page = services.GetRequiredService<MaintenanceViewModel>();
        Assert.False(page.ObserveCommand.CanExecute(null));

        await page.ReadCommand.ExecuteAsync(null);

        Assert.Equal(["Chat", "Updater", "Vendor Service"], page.Items.Select(i => i.Name));
        Assert.Equal("3 startup items, 2 of them enabled. 1 places could not be read, so the list may be incomplete.", page.Summary);
        var chat = page.Items[0];
        Assert.Equal("Registry Run key", chat.Mechanism);
        Assert.Equal("Disabled. Disabled in Task Manager (StartupApproved).", chat.State);
        Assert.Equal(@"Program: C:\Apps\Chat\chat.exe (exists).", chat.Program);
        Assert.Equal("Signed by Chat Inc.", chat.Publisher);
        Assert.StartsWith("To change it: open Task Manager", chat.HowToChange);
        Assert.Contains("Task Scheduler (taskschd.msc)", page.Items[1].HowToChange);
        Assert.Contains("Services (services.msc)", page.Items[2].HowToChange);
        Assert.Contains("not found", page.Items[2].Program);
        Assert.Equal(@"HKLM\Software\Microsoft\Windows\CurrentVersion\Run: Access is denied.", Assert.Single(page.NotRead));
        Assert.Equal("Boot timing is not available without administrator rights.", page.BootTiming);
    }

    [Fact]
    public async Task An_observation_shows_measurements_side_by_side_with_their_caveats()
    {
        using var services = TestServices.Build(root);
        var page = services.GetRequiredService<MaintenanceViewModel>();
        await page.ReadCommand.ExecuteAsync(null);

        await page.ObserveCommand.ExecuteAsync(null);

        Assert.StartsWith("Seen running during the window", page.Items[0].Observation);
        Assert.Contains("CPU time 4.0 s", page.Items[0].Observation);
        Assert.Contains("memory up to", page.Items[0].Observation);
        Assert.Equal("Not seen running during the window.", page.Items[1].Observation);
        Assert.Contains(page.Caveats, c => c.StartsWith("Measured over 120 seconds", StringComparison.Ordinal));
        Assert.Contains("they are not combined into a score", page.ObservationSummary);
    }

    [Fact]
    public async Task When_did_it_start_shows_the_week_before_and_highlights_changes_as_possible_explanations()
    {
        using var services = TestServices.Build(root);
        var investigate = services.GetRequiredService<MaintenanceViewModel>().Investigate;
        investigate.ChosenDate = TestServices.FakeTimelineSources.Day;

        await investigate.ShowCommand.ExecuteAsync(null);

        Assert.Equal(["editor.exe stopped working (faulting module unknown, exception unknown).", "Installed Old VPN Client 3.1.0."], investigate.Events.Select(e => e.Summary));
        var install = investigate.Events[1];
        Assert.True(install.IsHighlighted);
        Assert.StartsWith("Possibly related (", install.Explanation);
        Assert.False(investigate.Events[0].IsHighlighted);
        Assert.StartsWith("1 change in the 7 days before", investigate.Summary);
        Assert.EndsWith("Some logs could not be read, so this may be incomplete.", investigate.Summary);
        Assert.Equal("System log: Reading System needs administrator rights.", Assert.Single(investigate.NotRead));
        Assert.Contains("is not proof that it caused it", investigate.Explanation);

        investigate.ChosenDate = TestServices.FakeTimelineSources.Day.AddDays(-19);
        Assert.Equal(["Installed Ancient Tool 3.1.0."], investigate.Events.Select(e => e.Summary));
    }

    [Fact]
    public void The_page_says_what_it_reads_and_that_it_changes_nothing()
    {
        using var services = TestServices.Build(root);
        var page = services.GetRequiredService<MaintenanceViewModel>();

        Assert.Contains("Run and RunOnce registry keys", page.PrivacyText);
        Assert.Contains("CPU time, memory and program path of running processes", page.PrivacyText);
        Assert.Contains("nothing is saved, nothing is sent anywhere, and nothing is changed", page.PrivacyText);
    }
}

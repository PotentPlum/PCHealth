using System.Net.Http;
using PCHealth.App.ViewModels;

namespace PCHealth.App.Tests;

/// <summary>The update flow with a fake update service: nothing here contacts the network.</summary>
public sealed class UpdatesTests
{
    [Fact]
    public void Nothing_is_checked_until_the_user_asks_and_the_check_is_disclosed()
    {
        var updates = new FakeUpdates("0.1.0");
        var page = new UpdatesViewModel(updates);

        Assert.Equal(0, updates.Checks);
        Assert.Equal("Not checked yet.", page.Status);
        Assert.Contains("contacts GitHub", UpdatesViewModel.Disclosure);
        Assert.Contains("never checks or downloads on its own", UpdatesViewModel.Disclosure);
        Assert.True(page.CheckCommand.CanExecute(null));
        Assert.False(page.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_newer_version_is_installed_only_after_a_second_explicit_step()
    {
        var updates = new FakeUpdates("0.1.0") { Latest = new AvailableUpdate("0.1.1", new object()) };
        var page = new UpdatesViewModel(updates);

        await page.CheckCommand.ExecuteAsync(null);

        Assert.Equal(1, updates.Checks);
        Assert.Equal("Version 0.1.1 is available. Install it when you are ready; the app restarts.", page.Status);
        Assert.Equal(0, updates.Downloads);
        Assert.Null(updates.Applied);

        await page.InstallCommand.ExecuteAsync(null);

        Assert.Equal(1, updates.Downloads);
        Assert.Same(updates.Latest, updates.Applied);
    }

    [Fact]
    public async Task The_latest_version_and_failures_are_reported_plainly()
    {
        var updates = new FakeUpdates("0.1.0");
        var page = new UpdatesViewModel(updates);

        await page.CheckCommand.ExecuteAsync(null);
        Assert.Equal("You have the latest version.", page.Status);
        Assert.False(page.InstallCommand.CanExecute(null));

        updates.Failure = new HttpRequestException("No connection.");
        await page.CheckCommand.ExecuteAsync(null);
        Assert.Equal("The update could not be completed: No connection. Nothing was changed.", page.Status);
        Assert.True(page.CheckCommand.CanExecute(null));
    }

    [Fact]
    public void A_copy_not_installed_with_the_installer_cannot_check()
    {
        var page = new UpdatesViewModel(new FakeUpdates(installedVersion: null));

        Assert.False(page.CheckCommand.CanExecute(null));
        Assert.StartsWith("This copy was not installed with the installer", page.Status);
    }

    private sealed class FakeUpdates(string? installedVersion) : IUpdates
    {
        public AvailableUpdate? Latest { get; init; }

        public Exception? Failure { get; set; }

        public int Checks { get; private set; }

        public int Downloads { get; private set; }

        public AvailableUpdate? Applied { get; private set; }

        public string? InstalledVersion => installedVersion;

        public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return Failure is null ? Task.FromResult(Latest) : Task.FromException<AvailableUpdate?>(Failure);
        }

        public Task DownloadAsync(AvailableUpdate update, IProgress<int>? progress, CancellationToken cancellationToken)
        {
            Downloads++;
            return Task.CompletedTask;
        }

        public void ApplyAndRestart(AvailableUpdate update) => Applied = update;
    }
}

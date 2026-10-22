using CommunityToolkit.Mvvm.Input;

namespace PCHealth.App.ViewModels;

/// <summary>
/// The update flow (D-010): nothing is checked automatically. "Check for updates" reads the release list from GitHub;
/// only if a newer version exists and the user then chooses "Install and restart" is it downloaded and installed.
/// </summary>
public sealed class UpdatesViewModel : PageViewModel
{
    public const string Disclosure =
        "Checking for updates contacts GitHub to read the list of PC Health Monitor releases. Installing downloads the new version "
        + "from GitHub. Nothing about your files or this PC is sent; like any website, GitHub sees your IP address. "
        + "The app never checks or downloads on its own.";

    private readonly IUpdates updates;
    private AvailableUpdate? available;
    private bool busy;
    private string status;

    public UpdatesViewModel(IUpdates updates)
        : base("Updates")
    {
        this.updates = updates;
        status = updates.InstalledVersion is null
            ? "This copy was not installed with the installer, so it cannot update itself. Install it from the releases page on GitHub to get updates."
            : "Not checked yet.";
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !busy && updates.InstalledVersion is not null);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !busy && available is not null);
    }

    public string Version => updates.InstalledVersion is { } version ? $"Installed version: {version}" : "Not installed with the installer";

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public AvailableUpdate? Available
    {
        get => available;
        private set
        {
            if (SetProperty(ref available, value))
            {
                InstallCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public IAsyncRelayCommand CheckCommand { get; }

    public IAsyncRelayCommand InstallCommand { get; }

    private async Task CheckAsync()
    {
        await Run(async () =>
        {
            Status = "Checking GitHub for a newer version…";
            Available = await updates.CheckAsync(CancellationToken.None);
            Status = Available is { } update
                ? $"Version {update.Version} is available. Install it when you are ready; the app restarts."
                : "You have the latest version.";
        });
    }

    private async Task InstallAsync()
    {
        var update = Available!;
        await Run(async () =>
        {
            Status = $"Downloading version {update.Version} from GitHub…";
            await updates.DownloadAsync(update, new Progress<int>(p => Status = $"Downloading version {update.Version} from GitHub… {p}%"), CancellationToken.None);
            Status = "Installing and restarting…";
            updates.ApplyAndRestart(update);
        });
    }

    private async Task Run(Func<Task> step)
    {
        busy = true;
        Changed();
        try
        {
            await step();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Status = $"The update could not be completed: {ex.Message} Nothing was changed.";
        }
        finally
        {
            busy = false;
            Changed();
        }
    }

    private void Changed()
    {
        CheckCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Maintenance;

namespace PCHealth.App.ViewModels;

/// <summary>One startup item: what starts it, the evidence, the latest observation, and how to change it yourself.</summary>
public sealed class MaintenanceItemViewModel(StartupItem item) : ObservableObject
{
    private string observation = "Not observed yet.";

    public StartupItem Item { get; } = item;

    public string Name => Item.Name;

    public string Mechanism => Item.MechanismText;

    public string Location => $"Registered at: {Item.Location}";

    public string? Command => Item.Command is null ? null : $"Runs: {Item.Command}";

    public string Program => (Item.ImagePath, Item.FileExists) switch
    {
        (null, _) => "Program: not known.",
        (var path, true) => $"Program: {path} (exists).",
        (var path, false) => $"Program: {path} (not found: the entry may be left over from an app that was removed).",
        (var path, null) => $"Program: {path} (found through the search path, not checked).",
    };

    public string Publisher => Item.Signature.Describe();

    public string State => Item.State switch
    {
        StartupState.Enabled => $"Enabled. {Item.StateEvidence}",
        StartupState.Disabled => $"Disabled. {Item.StateEvidence}",
        _ => $"State unknown. {Item.StateEvidence}",
    };

    /// <summary>Where the user can change this themselves; the app itself changes nothing (spec §5.4).</summary>
    public string HowToChange => Item.Mechanism switch
    {
        StartupMechanism.RunKey or StartupMechanism.StartupFolder =>
            "To change it: open Task Manager (Ctrl+Shift+Esc), go to Startup apps, select it and choose Disable or Enable.",
        StartupMechanism.RunOnceKey =>
            "It runs once at your next sign-in and Windows then removes it. It is usually part of an install or update finishing; leaving it is normally right.",
        StartupMechanism.ScheduledTask =>
            $"To change it: open Task Scheduler (taskschd.msc), find {Item.Location} in the Task Scheduler Library, and choose Disable or Enable.",
        _ =>
            $"To change it: open Services (services.msc), open \"{Item.Name}\" and set its Startup type. Windows and your apps may need it, so check what it is first.",
    };

    public string Observation
    {
        get => observation;
        set => SetProperty(ref observation, value);
    }
}

/// <summary>
/// The Maintenance page (spec §5.4, T-59): startup items with their mechanism and evidence, an observation the user
/// starts, and boot timing where Windows allows it. Each item says how the user can change it themselves; this page
/// changes nothing, and what it reads is kept in memory only.
/// </summary>
public sealed class MaintenanceViewModel : PageViewModel
{
    private readonly IStartupSources sources;
    private readonly Func<ResourceObserver> observer;
    private readonly Func<BootTiming> bootTiming;
    private CancellationTokenSource? observing;
    private string summary = "Nothing has been read yet. Reading lists what starts with Windows; nothing is changed.";
    private string? observationSummary;
    private string boot = "Boot timing is read with the startup items.";
    private bool busy;

    public MaintenanceViewModel(IStartupSources sources, Func<ResourceObserver> observer, Func<BootTiming> bootTiming)
        : base("Maintenance")
    {
        this.sources = sources;
        this.observer = observer;
        this.bootTiming = bootTiming;
        ReadCommand = new AsyncRelayCommand(ReadAsync, () => !busy);
        ObserveCommand = new AsyncRelayCommand(ObserveAsync, () => !busy && Items.Count > 0);
        StopCommand = new RelayCommand(() => observing?.Cancel(), () => observing is not null);
    }

    public ObservableCollection<MaintenanceItemViewModel> Items { get; } = [];

    public ObservableCollection<string> NotRead { get; } = [];

    public ObservableCollection<string> Caveats { get; } = [];

    public string Summary
    {
        get => summary;
        private set => SetProperty(ref summary, value);
    }

    public string? ObservationSummary
    {
        get => observationSummary;
        private set => SetProperty(ref observationSummary, value);
    }

    public string BootTiming
    {
        get => boot;
        private set => SetProperty(ref boot, value);
    }

    /// <summary>What this page reads, and what it does not do (spec §6.5).</summary>
    public string PrivacyText =>
        "What this page reads: the Run and RunOnce registry keys, Task Manager's startup records, the Startup folders, scheduled task "
        + "definitions, the list of services, and the signatures of the programs they start. When you start an observation, it also reads "
        + "the CPU time, memory and program path of running processes for that window, and the boot timing log if Windows allows it. "
        + "Everything stays in memory while the app is open: nothing is saved, nothing is sent anywhere, and nothing is changed.";

    public IAsyncRelayCommand ReadCommand { get; }

    /// <summary>Observes for <see cref="ResourceObserver.DefaultWindow"/>; only when the user asks.</summary>
    public IAsyncRelayCommand ObserveCommand { get; }

    public IRelayCommand StopCommand { get; }

    public string ObserveText => $"Observe for {ResourceObserver.DefaultWindow.TotalMinutes:0} minutes";

    private async Task ReadAsync()
    {
        SetBusy(true);
        Summary = "Reading what starts with Windows. Nothing is changed.";
        try
        {
            var (inventory, timing) = await Task.Run(() => (new StartupInventoryReader(sources).Read(), bootTiming()));
            Items.Clear();
            NotRead.Clear();
            foreach (var item in inventory.Items.OrderBy(i => i.Mechanism).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Items.Add(new MaintenanceItemViewModel(item));
            }

            foreach (var source in inventory.NotRead)
            {
                NotRead.Add($"{source.Source}: {source.Reason}");
            }

            var enabled = inventory.Items.Count(i => i.State == StartupState.Enabled);
            Summary = string.Create(CultureInfo.CurrentCulture, $"{Items.Count} startup items, {enabled} of them enabled.")
                + (inventory.IsComplete ? "" : $" {NotRead.Count} places could not be read, so the list may be incomplete.");
            BootTiming = timing.Describe();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Summary = $"Startup items could not be read: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ObserveAsync()
    {
        SetBusy(true);
        observing = new CancellationTokenSource();
        StopCommand.NotifyCanExecuteChanged();
        ObservationSummary = $"Observing for up to {ResourceObserver.DefaultWindow.TotalMinutes:0} minutes at low priority. You can stop at any time.";
        try
        {
            var result = await observer().ObserveAsync(cancellationToken: observing.Token);
            var mapped = ResourceObserver.Map(Items.Select(i => i.Item).ToArray(), result);
            for (var i = 0; i < Items.Count; i++)
            {
                Items[i].Observation = mapped[i].Observed is { } seen
                    ? string.Create(CultureInfo.CurrentCulture, $"{mapped[i].Note} CPU time {seen.CpuTime.TotalSeconds:0.0} s ({seen.CpuShare:P1} of all processors); memory up to {SizeText.Bytes(seen.PeakWorkingSetBytes)} (average {SizeText.Bytes(seen.AverageWorkingSetBytes)}).")
                    : mapped[i].Note;
            }

            Caveats.Clear();
            foreach (var caveat in result.Caveats)
            {
                Caveats.Add(caveat);
            }

            ObservationSummary = string.Create(CultureInfo.CurrentCulture, $"Observed {result.Processes.Count} programs in {result.SampleCount} samples. The numbers are shown side by side; they are not combined into a score.");
        }
        finally
        {
            observing.Dispose();
            observing = null;
            StopCommand.NotifyCanExecuteChanged();
            SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        ReadCommand.NotifyCanExecuteChanged();
        ObserveCommand.NotifyCanExecuteChanged();
    }
}

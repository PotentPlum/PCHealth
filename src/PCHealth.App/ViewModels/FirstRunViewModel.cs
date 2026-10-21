using System.IO;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Policy.Recommendations;

namespace PCHealth.App.ViewModels;

/// <summary>
/// The first session (spec §5.1 first-run value, §3.5, D-018): says what is inspected, what is stored and that nothing
/// leaves the PC, then runs read-only discovery and shows one useful finding as soon as there is one.
/// </summary>
public sealed class FirstRunViewModel : PageViewModel
{
    public const string NetworkStatement = "Nothing leaves this PC. Checking for updates contacts GitHub only when you ask.";

    private readonly IDiscovery discovery;
    private readonly FindingsStore findings;
    private readonly AppPaths paths;
    private CancellationTokenSource? running;
    private string status = "Nothing has been looked at yet.";
    private Recommendation? firstFinding;

    public FirstRunViewModel(IDiscovery discovery, FindingsStore findings, AppPaths paths)
        : base("Welcome")
    {
        this.discovery = discovery;
        this.findings = findings;
        this.paths = paths;
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning);
        CancelCommand = new RelayCommand(() => running?.Cancel(), () => IsRunning);
    }

    public event EventHandler? Completed;

    public static string MarkerPath(AppPaths paths) => Path.Combine(paths.DataRoot, "first-run-complete");

    public string WhatWeInspect =>
        "The names, sizes and dates of the files and folders in your user folder, known cache locations, and the list of "
        + "installed apps. File contents are read only to confirm exact duplicates. Nothing is changed.";

    public string WhatWeStore =>
        $"Only what you decide: the changes you approve and their outcome, your exclusions, and items you send to quarantine. "
        + $"All of it stays in '{paths.DataRoot}'.";

    public string Network => NetworkStatement;

    public bool IsRunning => running is not null;

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    /// <summary>The first useful finding: the best action if there is one, otherwise the first item to review.</summary>
    public Recommendation? FirstFinding
    {
        get => firstFinding;
        private set => SetProperty(ref firstFinding, value);
    }

    public IAsyncRelayCommand StartCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public static bool IsNeeded(AppPaths paths) => !File.Exists(MarkerPath(paths));

    private async Task StartAsync()
    {
        running = new CancellationTokenSource();
        Changed();
        try
        {
            Status = "Checking known cache locations…";
            var quick = await discovery.QuickAsync(running.Token);
            findings.Publish(quick);
            FirstFinding = Best(quick);

            Status = "Measuring your user folder. This can take a few minutes; nothing is changed.";
            var full = await discovery.FullAsync(null, running.Token);
            findings.Publish(full);
            FirstFinding ??= Best(full);

            Directory.CreateDirectory(paths.DataRoot);
            await File.WriteAllTextAsync(MarkerPath(paths), "First run completed.", CancellationToken.None);
            Status = FirstFinding is null ? "Done. Nothing needs your attention right now." : "Done. Here is what stands out.";
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped. Nothing was changed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Discovery stopped: {ex.Message} Nothing was changed.";
        }
        finally
        {
            running.Dispose();
            running = null;
            Changed();
        }
    }

    private static Recommendation? Best(RecommendationSet set) => set.ToAct.FirstOrDefault() ?? set.ForReview.FirstOrDefault();

    private void Changed()
    {
        OnPropertyChanged(nameof(IsRunning));
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
}

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PCHealth.Duplicates;
using PCHealth.Inventory;

namespace PCHealth.App.ViewModels;

/// <summary>
/// Shows how far a full discovery has got (T-66), without claiming more than is known (rule 9):
/// <list type="bullet">
/// <item>The steps, which one is running and which are done.</item>
/// <item>While measuring: files, folders and bytes counted, the elapsed time and the current folder. A first scan has
/// no known total, so it has no percentage. Later scans compare with the last scan's file count and say that this is
/// an estimate.</item>
/// <item>While checking duplicates: the share of bytes compared, which is measured because the total is known before
/// hashing starts, and the time left at the speed so far.</item>
/// </list>
/// </summary>
public sealed class DiscoveryProgressViewModel : ObservableObject
{
    private static readonly (DiscoveryStep Step, string Text)[] StepTexts =
    [
        (DiscoveryStep.Measuring, "Measuring your user folder"),
        (DiscoveryStep.Caches, "Checking known cache locations"),
        (DiscoveryStep.LeftoverAppData, "Looking for data left behind by removed apps"),
        (DiscoveryStep.Duplicates, "Comparing possible duplicates"),
        (DiscoveryStep.Suggestions, "Preparing suggestions"),
    ];

    private readonly AppSettings? settings;
    private readonly AppPaths? paths;
    private readonly TimeProvider clock;
    private DateTimeOffset started;
    private DateTimeOffset stepStarted;
    private DiscoveryStep? step;
    private bool isActive;
    private string counts = "";
    private string currentFolder = "";
    private double percent;
    private bool isIndeterminate = true;
    private string estimate = "";
    private IReadOnlyList<string> steps = [];

    /// <param name="settings">Where the last scan's counts are kept for the next estimate; without it there is no estimate.</param>
    public DiscoveryProgressViewModel(AppSettings? settings = null, AppPaths? paths = null, TimeProvider? clock = null)
    {
        this.settings = settings;
        this.paths = paths;
        this.clock = clock ?? TimeProvider.System;
    }

    public bool IsActive
    {
        get => isActive;
        private set => SetProperty(ref isActive, value);
    }

    /// <summary>One line per step: done, running or still to come.</summary>
    public IReadOnlyList<string> Steps
    {
        get => steps;
        private set => SetProperty(ref steps, value);
    }

    public string Counts
    {
        get => counts;
        private set => SetProperty(ref counts, value);
    }

    public string CurrentFolder
    {
        get => currentFolder;
        private set => SetProperty(ref currentFolder, value);
    }

    /// <summary>0–100 when there is a percentage to show; see <see cref="IsIndeterminate"/>.</summary>
    public double Percent
    {
        get => percent;
        private set => SetProperty(ref percent, value);
    }

    /// <summary>True when there is no honest percentage, so the bar only shows that work is going on.</summary>
    public bool IsIndeterminate
    {
        get => isIndeterminate;
        private set => SetProperty(ref isIndeterminate, value);
    }

    /// <summary>The percentage and time left in words, saying whether they are measured or estimated.</summary>
    public string Estimate
    {
        get => estimate;
        private set => SetProperty(ref estimate, value);
    }

    /// <summary>Starts showing a new discovery. Reports must arrive on the thread that shows them, as <see cref="Progress{T}"/> does.</summary>
    public IProgress<DiscoveryProgress> Start()
    {
        started = clock.GetUtcNow();
        step = null;
        (Counts, CurrentFolder, Estimate, Percent, IsIndeterminate) = ("", "", "", 0, true);
        IsActive = true;
        Update(new DiscoveryProgress(DiscoveryStep.Measuring));
        return new Progress<DiscoveryProgress>(Update);
    }

    /// <summary>Records this scan's counts for the next estimate and stops showing progress.</summary>
    public void Complete(FullDiscovery result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (settings is not null && paths is not null && result.Statistics is { } statistics)
        {
            settings.RecordScan(statistics.FilesScanned, statistics.DirectoriesScanned);
            settings.Save(paths.Settings);
        }

        IsActive = false;
    }

    /// <summary>Stops showing progress after a cancellation or error.</summary>
    public void Stop() => IsActive = false;

    public void Update(DiscoveryProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var now = clock.GetUtcNow();
        if (progress.Step != step)
        {
            step = progress.Step;
            stepStarted = now;
            Steps = StepTexts.Select(s => s.Step < step ? $"✓ {s.Text}" : s.Step == step ? $"▶ {s.Text}…" : $"   {s.Text}").ToArray();
            (CurrentFolder, Estimate, Percent, IsIndeterminate) = ("", "", 0, true);
        }

        if (progress.Scan is { } scan)
        {
            Measuring(scan);
        }
        else if (progress.Hashing is { } hashing)
        {
            Comparing(hashing, now - stepStarted);
        }

        if (progress.Scan is null)
        {
            Counts = $"{Duration(now - started)} elapsed";
        }
    }

    private void Measuring(ScanProgress scan)
    {
        Counts = string.Create(
            CultureInfo.CurrentCulture,
            $"{scan.FilesScanned:N0} files · {scan.DirectoriesScanned:N0} folders · {SizeText.Bytes(scan.BytesScanned.Bytes)} measured · {Duration(scan.Elapsed)} elapsed");
        CurrentFolder = $"Now: {scan.CurrentDirectory}";
        if (settings?.LastScanFiles is not { } last || last == 0)
        {
            IsIndeterminate = true;
            Estimate = "This is the first scan, so the total isn't known yet and no percentage is shown. Next time there will be an estimate.";
        }
        else if (scan.FilesScanned >= last)
        {
            IsIndeterminate = true;
            Estimate = string.Create(CultureInfo.CurrentCulture, $"More files than the last scan counted ({last:N0}), so there is no estimate of what is left.");
        }
        else
        {
            var share = (double)scan.FilesScanned / last;
            IsIndeterminate = false;
            Percent = share * 100;
            var left = share > 0 ? Duration(scan.Elapsed * ((1 - share) / share)) : null;
            Estimate = string.Create(
                CultureInfo.CurrentCulture,
                $"About {Percent:0}%{(left is null ? "" : $", roughly {left} left")}: an estimate, based on the {last:N0} files the last scan counted.");
        }
    }

    private void Comparing(HashProgress hashing, TimeSpan elapsed)
    {
        if (hashing.BytesTotal == 0)
        {
            IsIndeterminate = true;
            Estimate = "No files of 1 MB or more share a size, so there is nothing to compare.";
            return;
        }

        var share = (double)hashing.BytesDone / hashing.BytesTotal;
        IsIndeterminate = false;
        Percent = share * 100;
        var left = share > 0 && share < 1 ? $", about {Duration(elapsed * ((1 - share) / share))} left at the speed so far" : "";
        Estimate = string.Create(
            CultureInfo.CurrentCulture,
            $"{Percent:0}% of {SizeText.Bytes(hashing.BytesTotal)} compared ({hashing.FilesDone:N0} of {hashing.FilesTotal:N0} files){left}.");
    }

    internal static string Duration(TimeSpan time) =>
        time.TotalHours >= 1
            ? string.Create(CultureInfo.CurrentCulture, $"{(int)time.TotalHours} h {time.Minutes} min")
            : time.TotalMinutes >= 1
                ? string.Create(CultureInfo.CurrentCulture, $"{(int)time.TotalMinutes} min {time.Seconds} s")
                : string.Create(CultureInfo.CurrentCulture, $"{Math.Max(0, (int)time.TotalSeconds)} s");
}

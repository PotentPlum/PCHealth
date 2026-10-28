using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;

namespace PCHealth.App.ViewModels;

/// <summary>One exclusion, with a way to remove it.</summary>
public sealed class ExclusionItemViewModel
{
    public ExclusionItemViewModel(Exclusion exclusion, Action<ExclusionItemViewModel> remove)
    {
        Exclusion = exclusion;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public Exclusion Exclusion { get; }

    public string Text => Exclusion.Kind switch
    {
        ExclusionKind.Category => $"Everything of the kind '{Exclusion.Value}'",
        ExclusionKind.TriageCategory => $"Organize suggestions of the kind '{Exclusion.Value}'",
        ExclusionKind.Folder => $"Folder {Exclusion.Value} and everything in it",
        _ => $"File {Exclusion.Value}",
    };

    public IRelayCommand RemoveCommand { get; }
}

/// <summary>
/// Settings and the product's own data (spec §6.5): the quarantine period, exclusions, and what the app keeps on this PC,
/// with a way to clear it. Clearing never removes a recovery path the user could still be counting on (D-020).
/// </summary>
public sealed class SettingsViewModel : PageViewModel
{
    private readonly AppSettings settings;
    private readonly AppPaths paths;
    private readonly QuarantineStore quarantine;
    private readonly ExclusionList exclusions;
    private readonly DeclinedRecommendations declines;
    private readonly TransactionLedger ledger;
    private readonly FindingsStore findings;
    private readonly Func<RecommendationBuilder> builder;
    private readonly TimeProvider clock;
    private string retentionDays;
    private string newExclusion = "";
    private string? message;
    private string? clearBlockedBy;
    private bool clearConfirmed;

    public SettingsViewModel(
        AppSettings settings,
        AppPaths paths,
        QuarantineStore quarantine,
        ExclusionList exclusions,
        DeclinedRecommendations declines,
        TransactionLedger ledger,
        FindingsStore findings,
        Func<RecommendationBuilder> builder,
        TimeProvider clock)
        : base("Settings")
    {
        this.settings = settings;
        this.paths = paths;
        this.quarantine = quarantine;
        this.exclusions = exclusions;
        this.declines = declines;
        this.ledger = ledger;
        this.findings = findings;
        this.builder = builder;
        this.clock = clock;
        retentionDays = settings.QuarantineRetentionDays.ToString(CultureInfo.CurrentCulture);
        SaveRetentionCommand = new RelayCommand(SaveRetention);
        AddExclusionCommand = new RelayCommand(AddExclusion, () => !string.IsNullOrWhiteSpace(NewExclusion));
        ClearCommand = new RelayCommand(Clear, () => ClearConfirmed && clearBlockedBy is null);
        OpenFolderCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo(paths.DataRoot) { UseShellExecute = true })?.Dispose());
        Refresh();
    }

    public string RetentionDays
    {
        get => retentionDays;
        set => SetProperty(ref retentionDays, value);
    }

    public string RetentionHelp =>
        $"How long items moved to quarantine stay restorable before they are deleted permanently: {AppSettings.MinRetentionDays} to "
        + $"{AppSettings.MaxRetentionDays} days. A change applies to items moved from now on; items already in quarantine keep their date.";

    public ObservableCollection<ExclusionItemViewModel> Exclusions { get; } = [];

    /// <summary>A full folder or file path to leave alone.</summary>
    public string NewExclusion
    {
        get => newExclusion;
        set
        {
            if (SetProperty(ref newExclusion, value))
            {
                AddExclusionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string DataFolder => paths.DataRoot;

    /// <summary>Everything the app keeps on this PC, as it is now.</summary>
    public ObservableCollection<string> StoredData { get; } = [];

    /// <summary>What clearing removes and what it leaves, said before the user confirms.</summary>
    public string ClearTerms =>
        "Clearing erases the history of changes, your exclusions, declined recommendations and settings, and shows the welcome page again. "
        + "Undo is no longer offered for earlier changes; items you moved to the Recycle Bin stay there and can still be restored from the "
        + "Recycle Bin. The Pause setting stays as it is.";

    /// <summary>Why clearing is not possible right now, or null.</summary>
    public string? ClearBlockedBy
    {
        get => clearBlockedBy;
        private set
        {
            if (SetProperty(ref clearBlockedBy, value))
            {
                ClearCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ClearConfirmed
    {
        get => clearConfirmed;
        set
        {
            if (SetProperty(ref clearConfirmed, value))
            {
                ClearCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string? Message
    {
        get => message;
        private set => SetProperty(ref message, value);
    }

    public IRelayCommand SaveRetentionCommand { get; }

    public IRelayCommand AddExclusionCommand { get; }

    public IRelayCommand ClearCommand { get; }

    public IRelayCommand OpenFolderCommand { get; }

    public void Refresh()
    {
        Exclusions.Clear();
        foreach (var exclusion in exclusions.Entries)
        {
            Exclusions.Add(new ExclusionItemViewModel(exclusion, RemoveExclusion));
        }

        var history = ledger.All();
        var held = quarantine.List();
        StoredData.Clear();
        StoredData.Add($"History of changes: {Count(history.Count, "recorded change")} ({SizeText.Bytes(FileBytes(paths.Ledger, paths.Ledger + "-wal", paths.Ledger + "-shm"))} on disk)");
        StoredData.Add($"Quarantine: {Count(held.Entries.Count, "item")}, {SizeText.Bytes(held.Entries.Sum(e => e.Before.SizeBytes))} when they were moved"
            + (held.Unreadable.Count > 0 ? $", and {Count(held.Unreadable.Count, "unreadable slot")}" : ""));
        StoredData.Add($"Exclusions: {exclusions.Entries.Count}");
        StoredData.Add($"Declined recommendations: {declines.Entries.Count}");
        StoredData.Add($"Settings: quarantine period of {settings.QuarantineRetentionDays} days");
        StoredData.Add("Measurements of your folders are kept in memory only and are gone when the app closes.");

        var unfinished = history.Count(t => t.State is TransactionState.Pending or TransactionState.Applied);
        ClearBlockedBy = held.Entries.Count + held.Unreadable.Count > 0
            ? $"{Count(held.Entries.Count + held.Unreadable.Count, "item")} {(held.Entries.Count + held.Unreadable.Count == 1 ? "is" : "are")} still in quarantine, "
                + "and clearing would remove the only way to put them back. Undo them or wait until their quarantine period ends"
                + (held.Entries.Count > 0 ? $" (the last ends {held.Entries.Max(e => e.DeadlineUtc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)})." : ".")
            : unfinished > 0
                ? "A change is not finished yet. Restart the app so it can be resolved, then clear."
                : null;
    }

    private void SaveRetention()
    {
        if (!int.TryParse(RetentionDays, NumberStyles.Integer, CultureInfo.CurrentCulture, out var days)
            || days is < AppSettings.MinRetentionDays or > AppSettings.MaxRetentionDays)
        {
            Message = $"Enter a whole number of days from {AppSettings.MinRetentionDays} to {AppSettings.MaxRetentionDays}.";
            return;
        }

        settings.SetQuarantineRetentionDays(days);
        settings.Save(paths.Settings);
        quarantine.Retention = settings.QuarantineRetention;
        findings.Rebuild(builder());
        Message = $"Items moved to quarantine from now on are kept for {days} days. Items already there keep their date.";
        Refresh();
    }

    private void AddExclusion()
    {
        var path = NewExclusion.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path))
        {
            Message = "Enter a full path, such as C:\\Users\\you\\Projects.";
            return;
        }

        var now = clock.GetUtcNow();
        var exclusion = File.Exists(path) ? exclusions.ExcludeFile(path, now) : exclusions.ExcludeFolder(path, now);
        exclusions.Save(paths.Exclusions);
        findings.Rebuild(builder());
        NewExclusion = "";
        Message = $"{exclusion.Value} is left alone from now on.";
        Refresh();
    }

    private void RemoveExclusion(ExclusionItemViewModel item)
    {
        exclusions.Remove(item.Exclusion);
        exclusions.Save(paths.Exclusions);
        findings.Rebuild(builder());
        Message = $"{item.Exclusion.Value} is no longer excluded. It can be suggested again.";
        Refresh();
    }

    private void Clear()
    {
        Refresh();
        if (ClearBlockedBy is not null)
        {
            return;
        }

        int changes;
        try
        {
            changes = ledger.Clear();
        }
        catch (InvalidOperationException ex)
        {
            Message = ex.Message;
            Refresh();
            return;
        }

        var excluded = exclusions.Entries.Count;
        foreach (var exclusion in exclusions.Entries.ToArray())
        {
            exclusions.Remove(exclusion);
        }

        var declined = declines.Entries.Count;
        foreach (var decline in declines.Entries.ToArray())
        {
            declines.Remove(decline.Key);
        }

        settings.SetQuarantineRetentionDays(AppSettings.DefaultRetentionDays);
        quarantine.Retention = settings.QuarantineRetention;
        RetentionDays = settings.QuarantineRetentionDays.ToString(CultureInfo.CurrentCulture);
        foreach (var file in new[] { paths.Exclusions, paths.Declines, paths.Settings, FirstRunViewModel.MarkerPath(paths) })
        {
            File.Delete(file);
        }

        findings.Rebuild(builder());
        ClearConfirmed = false;
        Message = $"Cleared {Count(changes, "recorded change")}, {Count(excluded, "exclusion")} and {Count(declined, "declined recommendation")}, "
            + "and reset the settings. Items in the Recycle Bin can still be restored from the Recycle Bin.";
        Refresh();
    }

    private static long FileBytes(params string[] files) => files.Where(File.Exists).Sum(f => new FileInfo(f).Length);

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

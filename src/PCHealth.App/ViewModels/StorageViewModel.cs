using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Inventory;
using PCHealth.Policy.Recommendations;

namespace PCHealth.App.ViewModels;

/// <summary>One folder in the storage view: its measured size, and what the suggested actions estimate could be freed in it.</summary>
public sealed class StorageFolderViewModel(FolderSize folder, long parentBytes, long reclaimableBytes)
{
    public FolderSize Folder { get; } = folder;

    public string Name => Path.GetFileName(Folder.FullPath) is { Length: > 0 } name ? name : Folder.FullPath;

    public string Size => SizeText.Measured(Folder.TotalSize);

    public string Files => StorageViewModel.Files(Folder.FileCount);

    /// <summary>The folder's share of the folder it is in, for a simple bar instead of a treemap.</summary>
    public double Share => parentBytes == 0 ? 0 : (double)Folder.TotalSize.Bytes / parentBytes;

    public string ShareText => string.Create(CultureInfo.CurrentCulture, $"{Share:P0}");

    public string? Reclaimable => StorageViewModel.ReclaimableText(reclaimableBytes);
}

/// <summary>
/// The storage detail view (spec §5.1): where the space in the user folder goes, folder by folder, with drill-down.
/// Folder sizes are measured by the scan; what could be freed is an estimate drawn from the suggested actions, and the
/// two are always labeled differently. No visualization beyond a share bar: treemaps are optional extras.
/// </summary>
public sealed class StorageViewModel : PageViewModel
{
    public const int CategoriesShown = 6;

    private readonly FindingsStore findings;
    private readonly IDiscovery discovery;
    private readonly Func<Recommendation, bool> isHidden;
    private StorageSummary? shown;
    private FolderSize? current;
    private CancellationTokenSource? running;
    private string status = "";

    public StorageViewModel(FindingsStore findings, IDiscovery discovery, DeclinedRecommendations? declines = null, DiscoveryProgressViewModel? progress = null)
        : base("Storage")
    {
        this.findings = findings;
        this.discovery = discovery;
        Progress = progress ?? new DiscoveryProgressViewModel();
        isHidden = declines is null ? _ => false : declines.IsDeclined;
        OpenCommand = new RelayCommand<StorageFolderViewModel>(folder => Show(folder!.Folder), folder => folder is not null);
        UpCommand = new RelayCommand(() => Show(Parent!), () => Parent is not null);
        MeasureCommand = new AsyncRelayCommand(MeasureAsync, () => !IsRunning);
        CancelCommand = new RelayCommand(() => running?.Cancel(), () => IsRunning);
        findings.Updated += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<StorageFolderViewModel> Folders { get; } = [];

    public ObservableCollection<string> Categories { get; } = [];

    public bool HasMeasurement => current is not null;

    /// <summary>The folder being looked at.</summary>
    public string? Location => current?.FullPath;

    public string? Total => current is null ? null : $"{SizeText.Measured(current.TotalSize)} in {Files(current.FileCount)}";

    /// <summary>The space taken by files directly in this folder rather than in a subfolder.</summary>
    public string? FilesHere
    {
        get
        {
            if (current is null || shown is null)
            {
                return null;
            }

            var below = shown.Children(current.FullPath);
            var bytes = current.TotalSize.Bytes - below.Sum(f => f.TotalSize.Bytes);
            var count = current.FileCount - below.Sum(f => f.FileCount);
            return $"Files directly in this folder: {SizeText.Bytes(bytes)} in {Files(count)} (measured)";
        }
    }

    /// <summary>What the suggested actions estimate could be freed below this folder; never presented as measured.</summary>
    public string? Reclaimable => current is null ? null : ReclaimableText(ReclaimableBelow(current.FullPath)) ?? "No suggested actions here.";

    /// <summary>Measured is not exact when part of the folder could not be read; say so.</summary>
    public string? Completeness => shown is null
        ? null
        : shown.IsComplete
            ? "Sizes are measured from the last scan: the sizes of the files, not the space they take on disk."
            : "Some folders could not be read, so measured sizes are at least what is shown.";

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public bool IsRunning => running is not null;

    public DiscoveryProgressViewModel Progress { get; }

    public IRelayCommand<StorageFolderViewModel> OpenCommand { get; }

    public IRelayCommand UpCommand { get; }

    public IAsyncRelayCommand MeasureCommand { get; }

    public IRelayCommand CancelCommand { get; }

    internal static string? ReclaimableText(long bytes) =>
        bytes <= 0 ? null : $"About {SizeText.Bytes(bytes)} could be freed by the suggested actions (estimated, not guaranteed)";

    private FolderSize? Parent =>
        current is null || shown is null || current == shown.Root || Path.GetDirectoryName(current.FullPath) is not { } parent
            ? null
            : shown.Folder(parent);

    /// <summary>Shows a new measurement from the root, or keeps the current folder if the measurement did not change.</summary>
    public void Refresh()
    {
        if (findings.Storage is not { } storage)
        {
            shown = null;
            current = null;
            Status = "Nothing has been measured since the app started. Measure to see where your space goes.";
            Update();
            return;
        }

        if (!ReferenceEquals(storage, shown))
        {
            shown = storage;
            current = storage.Root;
            Status = "";
        }

        Update();
    }

    private void Show(FolderSize folder)
    {
        current = folder;
        Update();
    }

    private void Update()
    {
        Folders.Clear();
        Categories.Clear();
        if (current is not null && shown is not null)
        {
            foreach (var child in shown.Children(current.FullPath))
            {
                Folders.Add(new StorageFolderViewModel(child, current.TotalSize.Bytes, ReclaimableBelow(child.FullPath)));
            }

            foreach (var category in shown.LargestCategories(CategoriesShown).Where(c => c.FileCount > 0))
            {
                Categories.Add($"{category.Category}: {SizeText.Measured(category.TotalSize)} in {Files(category.FileCount)}");
            }
        }

        OnPropertyChanged(nameof(HasMeasurement));
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(FilesHere));
        OnPropertyChanged(nameof(Reclaimable));
        OnPropertyChanged(nameof(Completeness));
        UpCommand.NotifyCanExecuteChanged();
    }

    private long ReclaimableBelow(string folder) =>
        findings.Latest is not { } latest
            ? 0
            : latest.ToAct
                .Where(r => !isHidden(r))
                .SelectMany(r => r.Operations)
                .Where(o => IsSameOrBelow(o.Path, folder))
                .Sum(o => o.ExpectedSize.Bytes);

    private static bool IsSameOrBelow(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(folder);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path.Length > root.Length && path[root.Length] is '\\' or '/');
    }

    internal static string Files(long count) =>
        count == 1 ? "1 file" : string.Create(CultureInfo.CurrentCulture, $"{count:N0} files");

    private async Task MeasureAsync()
    {
        running = new CancellationTokenSource();
        Changed();
        try
        {
            Status = "Measuring your user folder and looking for suggestions. Nothing is changed.";
            var full = await discovery.FullAsync(Progress.Start(), running.Token);
            Progress.Complete(full);
            findings.Publish(full);
            Status = "Measured.";
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped. Nothing was changed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Measuring stopped: {ex.Message} Nothing was changed.";
        }
        finally
        {
            Progress.Stop();
            running.Dispose();
            running = null;
            Changed();
        }
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsRunning));
        MeasureCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
}

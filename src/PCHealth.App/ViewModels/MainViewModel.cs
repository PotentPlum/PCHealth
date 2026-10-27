using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Policy.Recommendations;

namespace PCHealth.App.ViewModels;

/// <summary>The shell: navigation between pages and the global Pause control.</summary>
public sealed class MainViewModel : ObservableObject
{
    private PageViewModel current;

    private readonly HashSet<PageViewModel> opened = [];

    public MainViewModel(
        FirstRunViewModel firstRun,
        OverviewViewModel overview,
        StorageViewModel storage,
        DuplicatesViewModel duplicates,
        ActivityViewModel activity,
        BenchmarkViewModel benchmark,
        SettingsViewModel settings,
        UpdatesViewModel updates,
        PauseViewModel pause,
        AppPaths paths,
        Func<Recommendation, PreviewViewModel> preview,
        Func<IReadOnlyList<Recommendation>, BatchPreviewViewModel> batchPreview,
        FindingsStore findings)
    {
        overview.CleanUpAllRequested += (_, recommendations) =>
        {
            var page = batchPreview(recommendations);
            page.Finished += (_, _) =>
            {
                foreach (var entry in page.Executed)
                {
                    findings.Remove(entry.Recommendation);
                }

                overview.Refresh();
                opened.Remove(page);
                Current = overview;
            };
            opened.Add(page);
            Current = page;
        };
        overview.PreviewRequested += (_, recommendation) =>
        {
            var page = preview(recommendation);
            page.Finished += (_, _) =>
            {
                if (page.Execution is not null)
                {
                    findings.Remove(recommendation);
                }

                overview.Refresh();
                opened.Remove(page);
                Current = overview;
            };
            opened.Add(page);
            Current = page;
        };
        duplicates.PreviewRequested += (_, removal) =>
        {
            var page = preview(removal.Recommendation);
            page.Finished += (_, _) =>
            {
                var done = page.Execution?.Items.Where(i => i.Status == Ledger.ItemStatus.Done).Select(i => i.Operation.Path).ToHashSet(StringComparer.Ordinal) ?? [];
                if (done.Count > 0)
                {
                    removal.Removed(done);
                }

                duplicates.Refresh();
                opened.Remove(page);
                Current = duplicates;
            };
            opened.Add(page);
            Current = page;
        };
        Pages = [overview, storage, duplicates, activity, benchmark, settings, updates];
        FirstRun = firstRun;
        current = FirstRunViewModel.IsNeeded(paths) ? firstRun : overview;
        firstRun.Completed += (_, _) => Current = overview;
        Pause = pause;
        NavigateCommand = new RelayCommand<PageViewModel>(page => Current = page!, page => page is not null && Pages.Contains(page));
    }

    /// <summary>The pages in the navigation list.</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>Shown instead of the overview until the first discovery has completed.</summary>
    public FirstRunViewModel FirstRun { get; }

    public PageViewModel Current
    {
        get => current;
        set
        {
            if (value != FirstRun && !Pages.Contains(value) && !opened.Contains(value))
            {
                throw new ArgumentException("Only the shell's own pages can be shown.", nameof(value));
            }

            if (value is ActivityViewModel activity)
            {
                activity.Refresh();
            }
            else if (value is SettingsViewModel settings)
            {
                settings.Refresh();
            }

            SetProperty(ref current, value);
        }
    }

    public PauseViewModel Pause { get; }

    public IRelayCommand<PageViewModel> NavigateCommand { get; }
}

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
        ActivityViewModel activity,
        PauseViewModel pause,
        AppPaths paths,
        Func<Recommendation, PreviewViewModel> preview,
        FindingsStore findings)
    {
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
        Pages = [overview, activity];
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

            SetProperty(ref current, value);
        }
    }

    public PauseViewModel Pause { get; }

    public IRelayCommand<PageViewModel> NavigateCommand { get; }
}

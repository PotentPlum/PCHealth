using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PCHealth.App.ViewModels;

/// <summary>The shell: navigation between pages and the global Pause control.</summary>
public sealed class MainViewModel : ObservableObject
{
    private PageViewModel current;

    public MainViewModel(OverviewViewModel overview, ActivityViewModel activity, PauseViewModel pause)
    {
        Pages = [overview, activity];
        current = overview;
        Pause = pause;
        NavigateCommand = new RelayCommand<PageViewModel>(page => Current = page!, page => page is not null && Pages.Contains(page));
    }

    public IReadOnlyList<PageViewModel> Pages { get; }

    public PageViewModel Current
    {
        get => current;
        set
        {
            if (!Pages.Contains(value))
            {
                throw new ArgumentException("Only the shell's own pages can be shown.", nameof(value));
            }

            SetProperty(ref current, value);
        }
    }

    public PauseViewModel Pause { get; }

    public IRelayCommand<PageViewModel> NavigateCommand { get; }
}

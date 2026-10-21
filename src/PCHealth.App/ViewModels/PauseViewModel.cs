using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Execution;

namespace PCHealth.App.ViewModels;

/// <summary>
/// The global Pause control (spec §4). It shows and changes the same <see cref="GlobalPause"/> the execution gateway
/// checks, so pausing here blocks every change, and a pause set elsewhere shows up here.
/// </summary>
public sealed class PauseViewModel : ObservableObject
{
    private readonly GlobalPause pause;

    public PauseViewModel(GlobalPause pause)
    {
        this.pause = pause;
        ToggleCommand = new RelayCommand(Toggle);
        pause.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(Status));
        };
    }

    public bool IsPaused => pause.IsPaused;

    public string Label => IsPaused ? "Resume changes" : "Pause all changes";

    public string Status => IsPaused
        ? "Paused: nothing will be changed until you resume. Your activity history is kept."
        : "Changes run only after you preview and approve them.";

    public IRelayCommand ToggleCommand { get; }

    private void Toggle()
    {
        if (pause.IsPaused)
        {
            pause.Resume();
        }
        else
        {
            pause.Pause();
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace PCHealth.App.ViewModels;

/// <summary>A page the shell can navigate to.</summary>
public abstract class PageViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;
}

/// <summary>What changed, why, and what can be undone (T-32).</summary>
public sealed class ActivityViewModel() : PageViewModel("Activity");

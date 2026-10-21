using CommunityToolkit.Mvvm.ComponentModel;

namespace PCHealth.App.ViewModels;

/// <summary>A page the shell can navigate to.</summary>
public abstract class PageViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;
}


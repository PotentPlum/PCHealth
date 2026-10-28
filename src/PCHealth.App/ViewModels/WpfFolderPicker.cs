using Microsoft.Win32;

namespace PCHealth.App.ViewModels;

/// <summary>The standard Windows folder dialog.</summary>
public sealed class WpfFolderPicker : IFolderPicker
{
    public string? Pick(string title, string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (initialFolder is not null)
        {
            dialog.InitialDirectory = initialFolder;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

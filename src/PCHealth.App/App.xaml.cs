using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Execution.Windows;

namespace PCHealth.App;

public partial class App : Application
{
    private ServiceProvider? services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var resolver = new WindowsPathResolver();
        var paths = AppPaths.ForCurrentUser(resolver);
        try
        {
            DataMigration.Run(AppPaths.LegacyRoot(resolver), paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Starting with an empty data folder would hide the history and the quarantine; stop instead.
            MessageBox.Show(
                $"PC Health Monitor could not move its data to '{paths.DataRoot}': {ex.Message}\n\nClose any other copy of the app and start it again. Nothing was deleted.",
                "PC Health Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        services = AppServices.Build(paths);
        services.GetRequiredService<StartupTasks>().Run();
        var window = new MainWindow { DataContext = services.GetRequiredService<MainViewModel>() };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        services?.Dispose();
        base.OnExit(e);
    }
}

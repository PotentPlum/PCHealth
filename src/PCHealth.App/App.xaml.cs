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
        services = AppServices.Build(AppPaths.ForCurrentUser(new WindowsPathResolver()));
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

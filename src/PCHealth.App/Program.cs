using Velopack;

namespace PCHealth.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must run first: handles install, update and uninstall hooks, then returns. It never contacts the network.
        // A downloaded update is applied only when the user chose "Install and restart", never at the next startup.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

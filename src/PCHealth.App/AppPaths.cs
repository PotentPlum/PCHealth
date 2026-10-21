using System.IO;

namespace PCHealth.App;

/// <summary>Where the app keeps its own data: one folder under LocalAppData (D-016), nothing elsewhere.</summary>
public sealed record AppPaths(string DataRoot)
{
    public string Ledger => Path.Combine(DataRoot, "ledger.db");

    public string Quarantine => Path.Combine(DataRoot, "Quarantine");

    public string Exclusions => Path.Combine(DataRoot, "exclusions.json");

    public string PauseState => Path.Combine(DataRoot, "paused");

    public string Declines => Path.Combine(DataRoot, "declined.json");

    /// <summary><c>%LocalAppData%\PCHealth</c>, resolved to its final path so quarantine moves compare like with like.</summary>
    public static AppPaths ForCurrentUser(Execution.IPathResolver resolver)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        var final = resolver.Resolve(local, Execution.LinkHandling.Follow).FinalPath ?? local;
        return new AppPaths(Path.Combine(final, "PCHealth"));
    }
}

using System.IO;

namespace PCHealth.App;

/// <summary>
/// Where the app keeps its own data: one folder under LocalAppData (D-016, D-021), nothing elsewhere. It is never the
/// folder the app is installed in, so uninstalling or reinstalling can't delete the history or the quarantine.
/// </summary>
public sealed record AppPaths(string DataRoot)
{
    public string Ledger => Path.Combine(DataRoot, "ledger.db");

    public string Quarantine => Path.Combine(DataRoot, "Quarantine");

    public string Exclusions => Path.Combine(DataRoot, "exclusions.json");

    public string PauseState => Path.Combine(DataRoot, "paused");

    public string Declines => Path.Combine(DataRoot, "declined.json");

    public string Settings => Path.Combine(DataRoot, "settings.json");

    /// <summary><c>%LocalAppData%\PCHealthData</c>, resolved to its final path so quarantine moves compare like with like.</summary>
    public static AppPaths ForCurrentUser(Execution.IPathResolver resolver) => new(Path.Combine(LocalAppData(resolver), "PCHealthData"));

    /// <summary>
    /// <c>%LocalAppData%\PCHealth</c>: where version 0.1.0 kept its data, which is also the per-user install folder.
    /// <see cref="DataMigration"/> moves the data out of it.
    /// </summary>
    public static string LegacyRoot(Execution.IPathResolver resolver) => Path.Combine(LocalAppData(resolver), "PCHealth");

    private static string LocalAppData(Execution.IPathResolver resolver)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        return resolver.Resolve(local, Execution.LinkHandling.Follow).FinalPath ?? local;
    }
}

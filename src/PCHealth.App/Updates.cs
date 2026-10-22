using Velopack;
using Velopack.Sources;

namespace PCHealth.App;

/// <summary>A newer release found by a check the user started.</summary>
public sealed record AvailableUpdate(string Version, object Release);

/// <summary>
/// Updates from public GitHub Releases (D-009, D-010). Nothing here runs on its own: every method is called only
/// because the user pressed a button, and checking is the app's only network traffic (spec §3.5).
/// </summary>
public interface IUpdates
{
    /// <summary>The installed version, or null when this copy was not installed by the installer and cannot update itself.</summary>
    string? InstalledVersion { get; }

    /// <summary>Reads the list of releases from GitHub. Returns null when the installed version is the latest.</summary>
    Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the update's package from GitHub.</summary>
    Task DownloadAsync(AvailableUpdate update, IProgress<int>? progress, CancellationToken cancellationToken);

    /// <summary>Closes the app, installs the downloaded update and starts the new version.</summary>
    void ApplyAndRestart(AvailableUpdate update);
}

/// <summary>Velopack against the public GitHub Releases of this repository, without a token.</summary>
public sealed class VelopackUpdates : IUpdates
{
    public const string RepositoryUrl = "https://github.com/PotentPlum/PCHealth";

    private readonly UpdateManager? manager;

    public VelopackUpdates()
    {
        // Creating the manager only reads the install folder; it contacts nothing.
        try
        {
            manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            manager = null;
        }
    }

    public string? InstalledVersion => manager is { IsInstalled: true } m ? m.CurrentVersion?.ToString() : null;

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        var info = await Installed().CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return info is null ? null : new AvailableUpdate(info.TargetFullRelease.Version.ToString(), info);
    }

    public Task DownloadAsync(AvailableUpdate update, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        return Installed().DownloadUpdatesAsync((UpdateInfo)update.Release, p => progress?.Report(p), cancellationToken);
    }

    public void ApplyAndRestart(AvailableUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Installed().ApplyUpdatesAndRestart(((UpdateInfo)update.Release).TargetFullRelease);
    }

    private UpdateManager Installed() =>
        manager is { IsInstalled: true } m ? m : throw new InvalidOperationException("This copy was not installed with the installer, so it cannot update itself.");
}

namespace PCHealth.Policy.Residue;

public enum RegistrationSource
{
    /// <summary>An entry under the Windows "Uninstall" registry keys (classic installers).</summary>
    UninstallEntry,

    /// <summary>An AppX/MSIX package family registered for the user.</summary>
    Package,
}

/// <summary>An installed application or package, as Windows reports it.</summary>
/// <param name="Id">The uninstall key name, or the package family name.</param>
public sealed record AppRegistration(
    RegistrationSource Source,
    string Id,
    string DisplayName,
    string? Publisher = null,
    string? InstallLocation = null);

/// <summary>Every registration that could be read, and the sources that could not be read completely.</summary>
/// <param name="Gaps">User-readable descriptions of sources that were missing or unreadable. Any gap makes ownership uncertain.</param>
public sealed record RegistrationSnapshot(IReadOnlyList<AppRegistration> Registrations, IReadOnlyList<string> Gaps)
{
    public bool IsComplete => Gaps.Count == 0;

    public int Count(RegistrationSource source) => Registrations.Count(r => r.Source == source);
}

/// <summary>Reads application and package registrations. Windows implements it from the registry; tests use fakes.</summary>
public interface IAppRegistrations
{
    RegistrationSnapshot Read();
}

using System.Security;
using Microsoft.Win32;
using PCHealth.Policy.Residue;

namespace PCHealth.Execution.Windows;

/// <summary>
/// Reads installed applications from the Uninstall registry keys (machine 64- and 32-bit views, and the current user)
/// and the current user's AppX/MSIX package families from the AppModel repository (D-015). Read-only. A key that is
/// missing or unreadable is reported as a gap, so ownership decisions become uncertain instead of wrong.
/// </summary>
public sealed class WindowsAppRegistrations : IAppRegistrations
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string PackageFamiliesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Families";

    public RegistrationSnapshot Read()
    {
        var registrations = new List<AppRegistration>();
        var gaps = new List<string>();
        ReadUninstall(RegistryHive.LocalMachine, RegistryView.Registry64, "machine-wide (64-bit)", required: true, registrations, gaps);
        ReadUninstall(RegistryHive.LocalMachine, RegistryView.Registry32, "machine-wide (32-bit)", required: true, registrations, gaps);
        ReadUninstall(RegistryHive.CurrentUser, RegistryView.Default, "your account", required: false, registrations, gaps);
        ReadPackages(registrations, gaps);
        return new RegistrationSnapshot(registrations, gaps);
    }

    private static void ReadUninstall(
        RegistryHive hive,
        RegistryView view,
        string scope,
        bool required,
        List<AppRegistration> registrations,
        List<string> gaps)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallKey);
            if (uninstall is null)
            {
                if (required)
                {
                    gaps.Add($"the {scope} list of installed applications is missing.");
                }

                return;
            }

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                if (entry is null)
                {
                    gaps.Add($"the {scope} uninstall entry '{name}' could not be read.");
                    continue;
                }

                var displayName = entry.GetValue("DisplayName") as string;
                var location = entry.GetValue("InstallLocation") as string;
                if (string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(location))
                {
                    continue;
                }

                registrations.Add(new AppRegistration(
                    RegistrationSource.UninstallEntry,
                    name,
                    string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim(),
                    (entry.GetValue("Publisher") as string)?.Trim(),
                    string.IsNullOrWhiteSpace(location) ? null : Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'))));
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            gaps.Add($"the {scope} list of installed applications could not be read ({ex.Message}).");
        }
    }

    private static void ReadPackages(List<AppRegistration> registrations, List<string> gaps)
    {
        try
        {
            using var families = Registry.CurrentUser.OpenSubKey(PackageFamiliesKey);
            if (families is null)
            {
                gaps.Add("the list of installed app packages is missing.");
                return;
            }

            foreach (var family in families.GetSubKeyNames())
            {
                var separator = family.LastIndexOf('_');
                registrations.Add(new AppRegistration(
                    RegistrationSource.Package,
                    family,
                    separator > 0 ? family[..separator] : family));
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            gaps.Add($"the list of installed app packages could not be read ({ex.Message}).");
        }
    }
}

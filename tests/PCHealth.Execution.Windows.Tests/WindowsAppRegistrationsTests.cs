using PCHealth.Core;
using PCHealth.Inventory;
using PCHealth.Policy;
using PCHealth.Policy.Catalog;
using PCHealth.Policy.Residue;
using PCHealth.Testing;

namespace PCHealth.Execution.Windows.Tests;

/// <summary>Reads this PC's registrations (read-only) and checks them against fixture folders, never real AppData.</summary>
public class WindowsAppRegistrationsTests
{
    private static readonly RegistrationSnapshot Snapshot = new WindowsAppRegistrations().Read();

    [Fact]
    public void Installed_applications_are_read_from_the_uninstall_keys()
    {
        Assert.True(Snapshot.Count(RegistrationSource.UninstallEntry) > 0);
        Assert.All(Snapshot.Registrations, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Id));
            Assert.False(string.IsNullOrWhiteSpace(r.DisplayName));
        });
        Assert.DoesNotContain(Snapshot.Gaps, g => g.Contains("machine-wide", StringComparison.Ordinal));
    }

    [Fact]
    public void Packages_are_read_as_family_names_or_reported_as_a_gap()
    {
        var packages = Snapshot.Registrations.Where(r => r.Source == RegistrationSource.Package).ToArray();

        if (Snapshot.Gaps.Any(g => g.Contains("app packages", StringComparison.Ordinal)))
        {
            Assert.Empty(packages);
            return;
        }

        Assert.All(packages, p => Assert.Contains('_', p.Id));
    }

    [Fact]
    public void A_folder_named_after_an_installed_application_is_owned_and_an_unknown_one_is_not()
    {
        var installed = Snapshot.Registrations.First(r =>
            r.Source == RegistrationSource.UninstallEntry
            && r.DisplayName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !r.DisplayName.EndsWith('.')
            && r.DisplayName.Count(char.IsLetter) >= 4);
        using var root = new TempRoot();
        var owned = root.CreateFile($"Roaming/{installed.DisplayName}/state.dat", "x");
        var unknown = root.CreateFile($"Roaming/residue-{Guid.NewGuid():N}/state.dat", "x");
        File.SetLastWriteTimeUtc(owned, DateTime.UtcNow.AddDays(-400));
        File.SetLastWriteTimeUtc(unknown, DateTime.UtcNow.AddDays(-400));

        var result = ResidueDetector.Evaluate(
            new Locations(root.Resolve("Roaming")),
            new FixedRegistrations(Snapshot),
            new RecursiveScanner(),
            new ProtectionPolicy([]),
            new ExclusionList(),
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Skipped, s => s.Path == Path.GetDirectoryName(owned) && s.Reason.StartsWith("Belongs to", StringComparison.Ordinal));
        var finding = Assert.Single(result.Findings);
        Assert.Equal(FindingCategory.AppResidue, finding.Category);
        Assert.Contains(Path.GetFileName(Path.GetDirectoryName(unknown))!, finding.Title);
        Assert.Equal(!Snapshot.IsComplete, finding.ReviewOnly);
    }

    private sealed class Locations(string roaming) : IKnownLocations
    {
        public string? Resolve(string token) => token == "RoamingAppData" ? roaming : null;
    }

    private sealed class FixedRegistrations(RegistrationSnapshot snapshot) : IAppRegistrations
    {
        public RegistrationSnapshot Read() => snapshot;
    }
}

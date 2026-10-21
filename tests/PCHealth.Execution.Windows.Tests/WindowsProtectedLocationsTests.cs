using PCHealth.Policy;
using PCHealth.Testing;

namespace PCHealth.Execution.Windows.Tests;

/// <summary>Protection checks against this machine's real known folders. Read-only: nothing is changed.</summary>
public class WindowsProtectedLocationsTests
{
    private static readonly ProtectionPolicy Policy = WindowsProtectedLocations.CreateDefaultPolicy();

    [Theory]
    [InlineData(Environment.SpecialFolder.Windows, ProtectionKind.OperatingSystem)]
    [InlineData(Environment.SpecialFolder.System, ProtectionKind.OperatingSystem)]
    [InlineData(Environment.SpecialFolder.ProgramFiles, ProtectionKind.ProgramFiles)]
    [InlineData(Environment.SpecialFolder.ProgramFilesX86, ProtectionKind.ProgramFiles)]
    [InlineData(Environment.SpecialFolder.MyDocuments, ProtectionKind.PersonalFolder)]
    [InlineData(Environment.SpecialFolder.DesktopDirectory, ProtectionKind.PersonalFolder)]
    [InlineData(Environment.SpecialFolder.MyPictures, ProtectionKind.PersonalFolder)]
    public void Known_folders_are_protected_by_default(Environment.SpecialFolder folder, ProtectionKind kind)
    {
        var path = Canonical(Environment.GetFolderPath(folder));
        Assert.False(string.IsNullOrEmpty(path));

        var decision = Policy.Check(Path.Combine(path, "some", "file.txt"));

        Assert.True(decision.IsBlocked);
        Assert.Equal(kind, decision.Kind);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void System_binaries_are_protected()
    {
        var kernel = Canonical(Path.Combine(Environment.SystemDirectory, "kernel32.dll"));

        var decision = Policy.Check(kernel);

        Assert.Equal(ProtectionKind.OperatingSystem, decision.Kind);
    }

    [Fact]
    public void Boot_areas_on_the_system_drive_are_protected()
    {
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;

        Assert.Equal(ProtectionKind.Boot, Policy.Check(Path.Combine(systemRoot, "pagefile.sys")).Kind);
        Assert.Equal(ProtectionKind.Boot, Policy.Check(Path.Combine(systemRoot, "Recovery", "x")).Kind);
        Assert.Equal(ProtectionKind.VolumeRoot, Policy.Check(systemRoot).Kind);
    }

    [Fact]
    public void The_user_profile_is_protected_because_it_contains_personal_folders()
    {
        var profile = Canonical(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        var decision = Policy.Check(profile);

        Assert.True(decision.IsBlocked);
        Assert.Contains("contains", decision.Reason);
    }

    [Fact]
    public void Temp_fixture_files_are_not_protected()
    {
        using var root = new TempRoot();
        var file = root.CreateFile("cache/entry.tmp");

        var decision = Policy.Check(Canonical(file));

        Assert.False(decision.IsBlocked, decision.Reason);
    }

    [Fact]
    public void Every_discovered_location_has_a_reason()
    {
        var locations = WindowsProtectedLocations.Discover();

        Assert.NotEmpty(locations);
        Assert.All(locations, l => Assert.False(string.IsNullOrWhiteSpace(l.Reason)));
        Assert.Contains(locations, l => l.Kind == ProtectionKind.PersonalFolder);
    }

    private static string Canonical(string path) =>
        new WindowsPathResolver().Resolve(path, LinkHandling.Follow) is { IsResolved: true, FinalPath: { } final } ? final : path;
}

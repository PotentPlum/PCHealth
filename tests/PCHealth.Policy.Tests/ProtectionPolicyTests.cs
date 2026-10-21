using PCHealth.Testing;

namespace PCHealth.Policy.Tests;

public class ProtectionPolicyTests
{
    [Fact]
    public void Blocks_a_protected_folder_and_everything_below_it_with_a_reason()
    {
        using var root = new TempRoot();
        var policy = new ProtectionPolicy([new(root.Resolve("Windows"), ProtectionKind.OperatingSystem, "it holds Windows system files.")]);

        foreach (var path in new[] { root.Resolve("Windows"), root.Resolve("Windows/System32/drivers/etc/hosts") })
        {
            var decision = policy.Check(path);
            Assert.True(decision.IsBlocked);
            Assert.Equal(ProtectionKind.OperatingSystem, decision.Kind);
            Assert.Contains("it holds Windows system files.", decision.Reason);
            Assert.Contains(path, decision.Reason);
        }
    }

    [Fact]
    public void Blocks_a_folder_that_contains_a_protected_location()
    {
        using var root = new TempRoot();
        var policy = new ProtectionPolicy([new(root.Resolve("Users/me/Documents"), ProtectionKind.PersonalFolder, "it is your Documents folder.")]);

        var decision = policy.Check(root.Resolve("Users/me"));

        Assert.True(decision.IsBlocked);
        Assert.Equal(ProtectionKind.PersonalFolder, decision.Kind);
        Assert.Contains("contains", decision.Reason);
    }

    [Fact]
    public void Allows_unprotected_paths_including_siblings_that_share_a_prefix()
    {
        using var root = new TempRoot();
        var policy = new ProtectionPolicy([new(root.Resolve("Users/me/Documents"), ProtectionKind.PersonalFolder, "it is your Documents folder.")]);

        Assert.False(policy.Check(root.Resolve("Users/me/Documents-old/report.pdf")).IsBlocked);
        Assert.False(policy.Check(root.Resolve("Users/me/AppData/Local/Temp/x.tmp")).IsBlocked);
        Assert.Equal("", policy.Check(root.Resolve("Users/me/Downloads/a.zip")).Reason);
    }

    [Theory]
    [InlineData("tool.exe")]
    [InlineData("library.DLL")]
    [InlineData("driver.sys")]
    [InlineData("boot.efi")]
    public void Blocks_program_binaries_anywhere(string name)
    {
        using var root = new TempRoot();
        var policy = new ProtectionPolicy([]);

        var decision = policy.Check(root.Resolve("Downloads/" + name));

        Assert.True(decision.IsBlocked);
        Assert.Equal(ProtectionKind.ApplicationBinary, decision.Kind);
        Assert.Contains(name, decision.Reason);
    }

    [Fact]
    public void Blocks_a_drive_root()
    {
        var policy = new ProtectionPolicy([]);
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var decision = policy.Check(root);

        Assert.Equal(ProtectionKind.VolumeRoot, decision.Kind);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void Every_location_needs_a_reason_and_a_full_path()
    {
        using var root = new TempRoot();

        Assert.Throws<ArgumentException>(() => new ProtectionPolicy([new(root.Path, ProtectionKind.Boot, " ")]));
        Assert.Throws<ArgumentException>(() => new ProtectionPolicy([new("relative", ProtectionKind.Boot, "why")]));
        Assert.Throws<ArgumentException>(() => new ProtectionPolicy([]).Check("relative/file.txt"));
    }

    [Fact]
    public void Trailing_separators_do_not_change_the_result()
    {
        using var root = new TempRoot();
        var policy = new ProtectionPolicy([new(root.Resolve("Program Files") + Path.DirectorySeparatorChar, ProtectionKind.ProgramFiles, "installed applications live here.")]);

        Assert.True(policy.Check(root.Resolve("Program Files") + Path.DirectorySeparatorChar).IsBlocked);
        Assert.True(policy.Check(root.Resolve("Program Files/App/readme.txt")).IsBlocked);
    }
}

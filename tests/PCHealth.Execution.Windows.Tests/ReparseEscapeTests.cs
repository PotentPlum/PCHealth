using System.Diagnostics;
using PCHealth.Testing;

namespace PCHealth.Execution.Windows.Tests;

/// <summary>Links created inside the fixture that try to escape the allowed scope (spec §6.3).</summary>
public class ReparseEscapeTests
{
    private readonly ScopeValidator validator = new(new WindowsPathResolver());

    [Fact]
    public void A_file_reached_through_a_junction_to_outside_is_rejected()
    {
        using var scope = new TempRoot();
        using var outside = new TempRoot();
        outside.CreateFile("victim.txt", "must not be touched");
        Mklink("/J", scope.Resolve("junction"), outside.Path);

        var decision = validator.Check(scope.Resolve("junction/victim.txt"), scope.Path);

        Assert.Equal(ScopeRejectionReason.OutsideScope, decision.Reason);
        Assert.Contains("victim.txt", decision.Message);
    }

    [Fact]
    public void A_junction_to_outside_is_rejected_as_a_target()
    {
        using var scope = new TempRoot();
        using var outside = new TempRoot();
        Mklink("/J", scope.Resolve("junction"), outside.Path);

        var decision = validator.Check(scope.Resolve("junction"), scope.Path);

        Assert.Equal(ScopeRejectionReason.LinkLeadsOutsideScope, decision.Reason);
    }

    [Fact]
    public void A_directory_symlink_to_outside_is_rejected_both_ways()
    {
        using var scope = new TempRoot();
        using var outside = new TempRoot();
        outside.CreateFile("victim.txt");
        CreateSymlinkOrSkip(() => Directory.CreateSymbolicLink(scope.Resolve("link"), outside.Path));

        Assert.Equal(ScopeRejectionReason.LinkLeadsOutsideScope, validator.Check(scope.Resolve("link"), scope.Path).Reason);
        Assert.Equal(ScopeRejectionReason.OutsideScope, validator.Check(scope.Resolve("link/victim.txt"), scope.Path).Reason);
    }

    [Fact]
    public void A_file_symlink_to_outside_is_rejected()
    {
        using var scope = new TempRoot();
        using var outside = new TempRoot();
        var victim = outside.CreateFile("victim.txt");
        CreateSymlinkOrSkip(() => File.CreateSymbolicLink(scope.Resolve("link.txt"), victim));

        var decision = validator.Check(scope.Resolve("link.txt"), scope.Path);

        Assert.Equal(ScopeRejectionReason.LinkLeadsOutsideScope, decision.Reason);
    }

    [Fact]
    public void A_dangling_symlink_fails_closed()
    {
        using var scope = new TempRoot();
        CreateSymlinkOrSkip(() => File.CreateSymbolicLink(scope.Resolve("dangling.txt"), scope.Resolve("gone.txt")));

        var decision = validator.Check(scope.Resolve("dangling.txt"), scope.Path);

        Assert.Equal(ScopeRejectionReason.LinkLeadsOutsideScope, decision.Reason);
        Assert.Equal(PathRejectionReason.NotFound, decision.PathRejection);
    }

    [Fact]
    public void A_mount_point_style_junction_to_a_volume_path_outside_is_rejected()
    {
        // A volume mount point is a junction whose target is a \\?\Volume{GUID}\ path. Mounting a real volume needs
        // elevation, so this points such a junction at a folder on the system volume, outside the scope.
        using var scope = new TempRoot();
        using var outside = new TempRoot();
        outside.CreateFile("victim.txt");
        var volume = VolumeGuidPath(outside.Path);
        var target = volume + outside.Path[3..];
        if (!TryMklink("/J", scope.Resolve("mount"), target, out var error))
        {
            Assert.Skip($"Cannot create a volume-path junction here: {error}");
        }

        Assert.Equal(ScopeRejectionReason.OutsideScope, validator.Check(scope.Resolve("mount/victim.txt"), scope.Path).Reason);
        Assert.Equal(ScopeRejectionReason.LinkLeadsOutsideScope, validator.Check(scope.Resolve("mount"), scope.Path).Reason);
    }

    [Fact]
    public void Links_that_stay_inside_the_scope_are_allowed()
    {
        using var scope = new TempRoot();
        scope.CreateFile("real/file.txt");
        Mklink("/J", scope.Resolve("junction"), scope.Resolve("real"));

        var viaJunction = validator.Check(scope.Resolve("junction/file.txt"), scope.Path);
        var junction = validator.Check(scope.Resolve("junction"), scope.Path);

        Assert.True(viaJunction.IsAllowed, viaJunction.Message);
        Assert.EndsWith(@"\real\file.txt", viaJunction.FinalPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(junction.IsAllowed, junction.Message);
    }

    [Fact]
    public void Dot_dot_out_of_the_scope_is_rejected()
    {
        using var parent = new TempRoot();
        parent.CreateFile("outside.txt");
        var scope = parent.CreateDirectory("scope");

        var decision = validator.Check(scope + @"\..\outside.txt", scope);

        Assert.Equal(ScopeRejectionReason.OutsideScope, decision.Reason);
    }

    [Fact]
    public void A_scope_given_through_a_junction_is_checked_at_its_real_location()
    {
        using var root = new TempRoot();
        root.CreateFile("real/file.txt");
        Mklink("/J", root.Resolve("alias"), root.Resolve("real"));

        var decision = validator.Check(root.Resolve("real/file.txt"), root.Resolve("alias"));

        Assert.True(decision.IsAllowed, decision.Message);
    }

    private static void CreateSymlinkOrSkip(Action create)
    {
        try
        {
            create();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Needs Developer Mode or SeCreateSymbolicLinkPrivilege; tests never request elevation.
            Assert.Skip($"Cannot create symlinks here: {ex.Message}");
        }
    }

    private static void Mklink(string kind, string link, string target)
    {
        Assert.True(TryMklink(kind, link, target, out var error), $"mklink {kind} failed: {error}");
    }

    private static bool TryMklink(string kind, string link, string target, out string error)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", kind, link, target },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static string VolumeGuidPath(string path)
    {
        var buffer = new System.Text.StringBuilder(64);
        Assert.True(
            GetVolumeNameForVolumeMountPointW(Path.GetPathRoot(path)!, buffer, (uint)buffer.Capacity),
            "Could not read the volume GUID path.");
        return buffer.ToString();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string lpszVolumeMountPoint, System.Text.StringBuilder lpszVolumeName, uint cchBufferLength);
}

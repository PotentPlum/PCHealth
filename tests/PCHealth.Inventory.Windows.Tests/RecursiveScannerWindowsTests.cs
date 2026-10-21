using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using PCHealth.Core;
using PCHealth.Testing;

namespace PCHealth.Inventory.Windows.Tests;

/// <summary>Windows file-system cases for the fallback scanner (spec §5.1, §9).</summary>
public class RecursiveScannerWindowsTests
{
    private readonly RecursiveScanner scanner = new();

    [Fact]
    public void Records_junctions_without_following_them()
    {
        using var root = new TempRoot();
        using var outside = new TempRoot();
        outside.CreateFile("secret.txt", "outside the scan root");
        CreateJunction(root.Resolve("junction"), outside.Path);

        var result = Scan(root.Path);

        var junction = Assert.IsType<DirectoryEntry>(Single(root, result, "junction"));
        Assert.True(junction.IsReparsePoint);
        Assert.Equal(new[] { "", "junction" }, RelativePaths(root, result));
    }

    [Fact]
    public void Terminates_on_a_junction_cycle()
    {
        using var root = new TempRoot();
        root.CreateFile("sub/file.txt");
        CreateJunction(root.Resolve("sub/loop"), root.Path);

        var result = Scan(root.Path);

        Assert.Equal(new[] { "", "sub", "sub/file.txt", "sub/loop" }, RelativePaths(root, result));
    }

    [Fact]
    public void Records_directory_symlinks_without_following_them()
    {
        using var root = new TempRoot();
        using var outside = new TempRoot();
        outside.CreateFile("secret.txt", "outside the scan root");
        try
        {
            Directory.CreateSymbolicLink(root.Resolve("link"), outside.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Needs Developer Mode or SeCreateSymbolicLinkPrivilege; tests never request elevation.
            Assert.Skip($"Cannot create directory symlinks here: {ex.Message}");
        }

        var result = Scan(root.Path);

        var link = Assert.IsType<DirectoryEntry>(Single(root, result, "link"));
        Assert.True(link.IsReparsePoint);
        Assert.Equal(new[] { "", "link" }, RelativePaths(root, result));
    }

    [Fact]
    public void Includes_hidden_and_system_entries_with_their_attributes()
    {
        using var root = new TempRoot();
        var directory = root.CreateDirectory("sysdir");
        var file = root.CreateFile("sysdir/hidden.dat", "abc");
        File.SetAttributes(file, FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(directory, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);
        try
        {
            var result = Scan(root.Path);

            var dirEntry = Single(root, result, "sysdir");
            var fileEntry = Assert.IsType<FileEntry>(Single(root, result, "sysdir/hidden.dat"));
            Assert.True(dirEntry.Attributes.HasFlag(FileAttributes.Hidden | FileAttributes.System));
            Assert.True(fileEntry.Attributes.HasFlag(FileAttributes.Hidden | FileAttributes.System));
            Assert.Equal(3, fileEntry.Size.Bytes);
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.SetAttributes(directory, FileAttributes.Directory);
        }
    }

    [Fact]
    public void Scans_paths_longer_than_260_characters()
    {
        using var root = new TempRoot();
        var segment = new string('d', 60);
        var relative = string.Join('/', Enumerable.Repeat(segment, 5)) + "/long.txt";
        var file = root.CreateFile(relative, "12345");
        Assert.True(file.Length > 260, $"Fixture path is only {file.Length} characters.");

        var result = Scan(root.Path);

        Assert.True(result.IsComplete);
        var entry = Assert.IsType<FileEntry>(Single(root, result, relative));
        Assert.Equal(5, entry.Size.Bytes);
        Assert.True(entry.FullPath.Length > 260);
    }

    [Fact]
    public void Records_access_denied_as_evidence_and_continues()
    {
        using var root = new TempRoot();
        root.CreateFile("locked/inner.txt");
        root.CreateFile("open/visible.txt");
        var locked = new DirectoryInfo(root.Resolve("locked"));
        var user = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = locked.GetAccessControl();
        security.AddAccessRule(deny);
        locked.SetAccessControl(security);
        try
        {
            var result = Scan(root.Path);

            var denied = Assert.Single(result.Inaccessible);
            Assert.Equal(locked.FullName, denied.Path);
            Assert.Equal(InaccessibleReason.AccessDenied, denied.Reason);
            Assert.False(result.IsComplete);
            Assert.Contains("locked", RelativePaths(root, result));
            Assert.Contains("open/visible.txt", RelativePaths(root, result));
            Assert.DoesNotContain("locked/inner.txt", RelativePaths(root, result));
        }
        finally
        {
            // Lift the deny so the fixture can delete the directory.
            var restore = locked.GetAccessControl();
            restore.RemoveAccessRule(deny);
            locked.SetAccessControl(restore);
        }
    }

    private ScanResult Scan(string path) => scanner.Scan(path, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Junctions have no managed API; <c>mklink /J</c> needs no elevation.</summary>
    private static void CreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", linkPath, targetPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /J failed ({process.ExitCode}): {error}");
        Assert.True(new DirectoryInfo(linkPath).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    private static string Relative(TempRoot root, FileSystemEntry entry)
    {
        var relative = Path.GetRelativePath(root.Path, entry.FullPath).Replace('\\', '/');
        return relative == "." ? "" : relative;
    }

    private static string[] RelativePaths(TempRoot root, ScanResult result) =>
        result.Entries.Select(e => Relative(root, e)).Order(StringComparer.Ordinal).ToArray();

    private static FileSystemEntry Single(TempRoot root, ScanResult result, string relative) =>
        Assert.Single(result.Entries, e => Relative(root, e) == relative);
}

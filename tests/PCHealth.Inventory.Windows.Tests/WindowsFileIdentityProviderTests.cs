using System.Diagnostics;
using PCHealth.Core;
using PCHealth.Duplicates;
using PCHealth.Testing;

namespace PCHealth.Inventory.Windows.Tests;

public class WindowsFileIdentityProviderTests
{
    private readonly WindowsFileIdentityProvider provider = new();

    [Fact]
    public void Hard_links_share_an_identity_and_copies_do_not()
    {
        using var root = new TempRoot();
        var original = root.CreateFile("original.txt", "content");
        var link = root.Resolve("link.txt");
        CreateHardLink(link, original);
        var copy = root.Resolve("copy.txt");
        File.Copy(original, copy);

        var originalId = Identity(original);
        Assert.NotNull(originalId);
        Assert.Equal(originalId, Identity(link));
        Assert.NotEqual(originalId, Identity(copy));
        Assert.Equal(originalId, Identity(original));
    }

    [Fact]
    public void Hard_links_alone_are_never_reported_as_duplicates()
    {
        using var root = new TempRoot();
        var original = root.CreateFile("original.bin", "the same bytes");
        CreateHardLink(root.Resolve("link.bin"), original);

        var result = Find(root);

        Assert.Empty(result.Groups);
    }

    [Fact]
    public void Hard_links_and_a_real_copy_form_one_group_of_two_physical_copies()
    {
        using var root = new TempRoot();
        var original = root.CreateFile("original.bin", "the same bytes");
        CreateHardLink(root.Resolve("link.bin"), original);
        File.Copy(original, root.Resolve("copy.bin"));

        var group = Assert.Single(Find(root).Groups);

        Assert.Equal(3, group.Files.Count);
        Assert.Equal(2, group.PhysicalCopies.Count);
        Assert.Contains(group.PhysicalCopies, c => c.Count == 2 && c.All(f => f.File.Name is "original.bin" or "link.bin"));
    }

    private FileIdentity? Identity(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return provider.TryGetIdentity(handle, path);
    }

    private VerificationResult Find(TempRoot root)
    {
        var scan = new RecursiveScanner().Scan(root.Path, cancellationToken: TestContext.Current.CancellationToken);
        return new DuplicateFinder(provider).Find(scan.Entries.OfType<FileEntry>(), TestContext.Current.CancellationToken);
    }

    /// <summary><c>mklink /H</c> needs no elevation.</summary>
    private static void CreateHardLink(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/H", linkPath, targetPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /H failed ({process.ExitCode}): {error}");
    }
}

using PCHealth.Core;
using PCHealth.Testing;

namespace PCHealth.Inventory.Tests;

public class RecursiveScannerTests
{
    private readonly RecursiveScanner scanner = new();

    [Fact]
    public void Lists_every_file_and_directory_with_measured_sizes()
    {
        using var root = new TempRoot();
        root.CreateFile("a.txt", "12345");
        root.CreateFile("sub/b.bin", "123");
        root.CreateFile("sub/deeper/c.log", "");
        root.CreateDirectory("empty");

        var result = Scan(root.Path);

        Assert.True(result.IsComplete);
        Assert.Equal(
            new[] { "", "a.txt", "empty", "sub", "sub/b.bin", "sub/deeper", "sub/deeper/c.log" },
            RelativePaths(root, result));
        Assert.IsType<DirectoryEntry>(Single(root, result, ""));
        Assert.Equal(5, Assert.IsType<FileEntry>(Single(root, result, "a.txt")).Size.Bytes);
        Assert.Equal(3, Assert.IsType<FileEntry>(Single(root, result, "sub/b.bin")).Size.Bytes);
    }

    [Fact]
    public void Leaves_the_tree_unchanged()
    {
        using var root = new TempRoot();
        root.CreateFile("a.txt", "hello");
        root.CreateFile("sub/b.txt", "world");
        var before = Snapshot(root);

        Scan(root.Path);

        Assert.Equal(before, Snapshot(root));
    }

    [Fact]
    public void Includes_hidden_entries()
    {
        using var root = new TempRoot();
        var hidden = root.CreateFile(".hidden/file.txt", "x");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hidden, FileAttributes.Hidden);
        }

        var result = Scan(root.Path);

        Assert.Contains(".hidden/file.txt", RelativePaths(root, result));
    }

    [Fact]
    public void Records_directory_symlinks_without_following_them()
    {
        using var root = new TempRoot();
        using var outside = new TempRoot();
        outside.CreateFile("secret.txt", "outside the scan root");
        CreateDirectoryLinkOrSkip(root.Resolve("link"), outside.Path);

        var result = Scan(root.Path);

        var link = Assert.IsType<DirectoryEntry>(Single(root, result, "link"));
        Assert.True(link.IsReparsePoint);
        Assert.Equal(new[] { "", "link" }, RelativePaths(root, result));
    }

    [Fact]
    public void Terminates_on_a_symlink_cycle()
    {
        using var root = new TempRoot();
        root.CreateFile("sub/file.txt");
        CreateDirectoryLinkOrSkip(root.Resolve("sub/loop"), root.Path);

        var result = Scan(root.Path);

        Assert.Equal(new[] { "", "sub", "sub/file.txt", "sub/loop" }, RelativePaths(root, result));
    }

    [Fact]
    public void Records_file_symlinks_without_measuring_the_target()
    {
        using var root = new TempRoot();
        using var outside = new TempRoot();
        var target = outside.CreateFile("big.bin", new string('x', 1000));
        var linkPath = root.Resolve("link.bin");
        try
        {
            File.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Cannot create symlinks here: {ex.Message}");
        }

        var result = Scan(root.Path);

        var link = Assert.IsType<FileEntry>(Single(root, result, "link.bin"));
        Assert.True(link.IsReparsePoint);
        Assert.Equal(0, link.Size.Bytes);
    }

    [Fact]
    public void Does_not_descend_into_a_root_that_is_a_symlink()
    {
        using var root = new TempRoot();
        root.CreateFile("target/file.txt");
        CreateDirectoryLinkOrSkip(root.Resolve("link"), root.Resolve("target"));

        var result = Scan(root.Resolve("link"));

        var only = Assert.Single(result.Entries);
        Assert.True(only.IsReparsePoint);
    }

    [Fact]
    public void Records_access_denied_as_evidence_and_continues()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows ACL cases are covered by the Windows tests (T-05).");
            return;
        }

        using var root = new TempRoot();
        root.CreateFile("locked/inner.txt");
        root.CreateFile("open/visible.txt");
        var locked = root.Resolve("locked");
        var mode = File.GetUnixFileMode(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (CanList(locked))
            {
                Assert.Skip("Running with privileges that bypass directory permissions (e.g. root).");
            }

            var result = Scan(root.Path);

            var denied = Assert.Single(result.Inaccessible);
            Assert.Equal(locked, denied.Path);
            Assert.Equal(InaccessibleReason.AccessDenied, denied.Reason);
            Assert.False(result.IsComplete);
            Assert.Contains("locked", RelativePaths(root, result));
            Assert.Contains("open/visible.txt", RelativePaths(root, result));
            Assert.DoesNotContain("locked/inner.txt", RelativePaths(root, result));
        }
        finally
        {
            File.SetUnixFileMode(locked, mode);
        }
    }

    [Fact]
    public void Throws_when_the_root_does_not_exist()
    {
        using var root = new TempRoot();

        Assert.Throws<DirectoryNotFoundException>(() => Scan(root.Resolve("missing")));
    }

    [Fact]
    public void Reports_progress_with_growing_counts_and_final_totals()
    {
        using var root = new TempRoot();
        root.CreateFile("a.txt", "12");
        root.CreateFile("x/b.txt", "345");
        root.CreateFile("x/y/c.txt", "6");
        var reports = new List<ScanProgress>();

        Scan(root.Path, new SyncProgress(reports.Add));

        Assert.Equal(3, reports.Count);
        Assert.Equal(reports.Select(r => r.DirectoriesScanned).Order(), reports.Select(r => r.DirectoriesScanned));
        Assert.Equal(reports.Select(r => r.FilesScanned).Order(), reports.Select(r => r.FilesScanned));
        var last = reports[^1];
        Assert.Equal(3, last.DirectoriesScanned);
        Assert.Equal(3, last.FilesScanned);
        Assert.Equal(6, last.BytesScanned.Bytes);
    }

    [Fact]
    public void Stops_when_cancelled_before_starting()
    {
        using var root = new TempRoot();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => scanner.Scan(root.Path, cancellationToken: cts.Token));
    }

    [Fact]
    public void Stops_when_cancelled_during_the_scan()
    {
        using var root = new TempRoot();
        for (var i = 0; i < 10; i++)
        {
            root.CreateFile($"d{i}/f.txt");
        }

        using var cts = new CancellationTokenSource();
        var reports = 0;

        Assert.ThrowsAny<OperationCanceledException>(() => scanner.Scan(
            root.Path,
            new SyncProgress(_ =>
            {
                reports++;
                cts.Cancel();
            }),
            cts.Token));
        Assert.Equal(1, reports);
    }

    private ScanResult Scan(string path, IProgress<ScanProgress>? progress = null) =>
        scanner.Scan(path, progress, TestContext.Current.CancellationToken);

    private static void CreateDirectoryLinkOrSkip(string linkPath, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Cannot create symlinks here: {ex.Message}");
        }
    }

    private static bool CanList(string directory)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(directory).Any();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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

    private static string[] Snapshot(TempRoot root) =>
        Directory.EnumerateFileSystemEntries(root.Path, "*", SearchOption.AllDirectories)
            .Select(p => File.Exists(p)
                ? $"file|{p}|{File.GetLastWriteTimeUtc(p):O}|{File.ReadAllText(p)}"
                : $"dir|{p}|{Directory.GetLastWriteTimeUtc(p):O}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private sealed class SyncProgress(Action<ScanProgress> onReport) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => onReport(value);
    }
}

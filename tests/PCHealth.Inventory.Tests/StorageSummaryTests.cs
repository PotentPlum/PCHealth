using PCHealth.Core;
using PCHealth.Testing;

namespace PCHealth.Inventory.Tests;

public class StorageSummaryTests
{
    private static readonly DateTimeOffset Written = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rolls_file_sizes_up_into_every_ancestor()
    {
        using var root = new TempRoot();
        root.CreateFile("top.txt", new string('a', 10));
        root.CreateFile("media/clip.mp4", new string('v', 100));
        root.CreateFile("media/photos/a.jpg", new string('i', 20));
        root.CreateFile("media/photos/b.jpg", new string('i', 30));
        root.CreateDirectory("empty");

        var summary = Summarize(root);

        Assert.Equal(160, summary.Root.TotalSize.Bytes);
        Assert.Equal(4, summary.Root.FileCount);
        AssertFolder(summary, root.Resolve("media"), bytes: 150, files: 3);
        AssertFolder(summary, root.Resolve("media/photos"), bytes: 50, files: 2);
        AssertFolder(summary, root.Resolve("empty"), bytes: 0, files: 0);
        Assert.True(summary.IsComplete);
    }

    [Fact]
    public void Ranks_the_largest_folders_below_the_root()
    {
        using var root = new TempRoot();
        root.CreateFile("big/f.bin", new string('x', 300));
        root.CreateFile("big/nested/f.bin", new string('x', 100));
        root.CreateFile("medium/f.bin", new string('x', 200));
        root.CreateFile("small/f.bin", new string('x', 1));

        var top = Summarize(root).LargestFolders(3);

        Assert.Equal(
            new[] { root.Resolve("big"), root.Resolve("medium"), root.Resolve("big/nested") },
            top.Select(f => f.FullPath));
        Assert.Equal(new long[] { 400, 200, 100 }, top.Select(f => f.TotalSize.Bytes));
    }

    [Fact]
    public void Children_are_the_direct_subfolders_biggest_first()
    {
        using var root = new TempRoot();
        root.CreateFile("media/clip.mp4", new string('v', 100));
        root.CreateFile("media/photos/a.jpg", new string('i', 20));
        root.CreateFile("media/music/deep/b.mp3", new string('a', 30));
        root.CreateFile("docs/c.txt", "c");

        var summary = Summarize(root);

        Assert.Equal(new[] { root.Resolve("media"), root.Resolve("docs") }, summary.Children(root.Path).Select(f => f.FullPath));
        Assert.Equal(
            new[] { root.Resolve("media/music"), root.Resolve("media/photos") },
            summary.Children(root.Resolve("media") + Path.DirectorySeparatorChar).Select(f => f.FullPath));
        Assert.Empty(summary.Children(root.Resolve("docs")));
        Assert.Empty(summary.Children(root.Resolve("not-scanned")));
    }

    [Fact]
    public void Ties_are_ordered_by_path_so_results_are_stable()
    {
        using var root = new TempRoot();
        root.CreateFile("b/f", "xx");
        root.CreateFile("a/f", "xx");

        var top = Summarize(root).LargestFolders(2);

        Assert.Equal(new[] { root.Resolve("a"), root.Resolve("b") }, top.Select(f => f.FullPath));
    }

    [Fact]
    public void Ranks_file_categories_by_size()
    {
        using var root = new TempRoot();
        root.CreateFile("movie.MKV", new string('x', 500));
        root.CreateFile("a/report.pdf", new string('x', 40));
        root.CreateFile("b/notes.txt", new string('x', 60));
        root.CreateFile("c/unknown.zzz", new string('x', 7));

        var top = Summarize(root).LargestCategories(3);

        Assert.Equal(new[] { FileCategory.Video, FileCategory.Document, FileCategory.Other }, top.Select(c => c.Category));
        Assert.Equal(100, top[1].TotalSize.Bytes);
        Assert.Equal(2, top[1].FileCount);
    }

    [Fact]
    public void Linked_folders_add_nothing()
    {
        using var root = new TempRoot();
        using var outside = new TempRoot();
        outside.CreateFile("huge.bin", new string('x', 1000));
        root.CreateFile("own.bin", "x");
        try
        {
            Directory.CreateSymbolicLink(root.Resolve("link"), outside.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Cannot create symlinks here: {ex.Message}");
        }

        var summary = Summarize(root);

        Assert.Equal(1, summary.Root.TotalSize.Bytes);
        AssertFolder(summary, root.Resolve("link"), bytes: 0, files: 0);
    }

    [Fact]
    public void Accepts_a_root_with_a_trailing_separator_and_flags_incomplete_scans()
    {
        using var root = new TempRoot();
        var rootWithSeparator = root.Path + Path.DirectorySeparatorChar;
        var scan = new ScanResult(
            [
                new DirectoryEntry(rootWithSeparator, FileAttributes.Directory, Written),
                new FileEntry(root.Resolve("a.txt"), new MeasuredSize(5), FileAttributes.Normal, Written),
            ],
            [new InaccessibleEvidence(root.Resolve("locked"), InaccessibleReason.AccessDenied, "denied")],
            new ScanStatistics(1, 1, new MeasuredSize(5), TimeSpan.Zero, TimeSpan.Zero));

        var summary = StorageSummary.From(scan);

        Assert.Equal(5, summary.Root.TotalSize.Bytes);
        Assert.False(summary.IsComplete);
    }

    [Fact]
    public void Unknown_folders_return_null()
    {
        using var root = new TempRoot();

        Assert.Null(Summarize(root).Folder(root.Resolve("not-scanned")));
    }

    [Fact]
    public void Rejects_a_scan_without_a_root_directory()
    {
        Assert.Throws<ArgumentException>(() => StorageSummary.From(new ScanResult([], [], new ScanStatistics(0, 0, MeasuredSize.Zero, TimeSpan.Zero, TimeSpan.Zero))));
    }

    [Theory]
    [InlineData("clip.MP4", FileCategory.Video)]
    [InlineData("setup.exe", FileCategory.Installer)]
    [InlineData("disk.vhdx", FileCategory.DiskImage)]
    [InlineData("Makefile", FileCategory.Other)]
    [InlineData(".gitignore", FileCategory.Other)]
    public void Classifies_by_extension_ignoring_case(string name, FileCategory expected)
    {
        Assert.Equal(expected, FileCategories.Classify(name));
    }

    private static StorageSummary Summarize(TempRoot root) =>
        StorageSummary.From(new RecursiveScanner().Scan(root.Path, cancellationToken: TestContext.Current.CancellationToken));

    private static void AssertFolder(StorageSummary summary, string path, long bytes, long files)
    {
        var folder = summary.Folder(path);
        Assert.NotNull(folder);
        Assert.Equal(bytes, folder.TotalSize.Bytes);
        Assert.Equal(files, folder.FileCount);
    }
}

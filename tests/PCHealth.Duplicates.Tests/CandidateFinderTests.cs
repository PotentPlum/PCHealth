using PCHealth.Core;
using PCHealth.Testing;

namespace PCHealth.Duplicates.Tests;

public class CandidateFinderTests
{
    [Fact]
    public void Size_stage_keeps_only_shared_non_zero_sizes()
    {
        using var root = new TempRoot();
        var a = Entry(root.CreateFile("a.txt", "same"));
        var b = Entry(root.CreateFile("b.txt", "four"));
        var unique = Entry(root.CreateFile("unique.txt", "longer"));
        var empty1 = Entry(root.CreateFile("e1", ""));
        var empty2 = Entry(root.CreateFile("e2", ""));

        var groups = CandidateFinder.GroupBySize([a, b, unique, empty1, empty2]);

        var group = Assert.Single(groups);
        Assert.Equal(new[] { a.FullPath, b.FullPath }, group.Select(f => f.FullPath));
    }

    [Fact]
    public void Size_stage_ignores_repeated_paths_and_links()
    {
        using var root = new TempRoot();
        var a = Entry(root.CreateFile("a.txt", "same"));
        var link = new FileEntry(root.Resolve("link"), a.Size, FileAttributes.ReparsePoint, a.LastWriteTimeUtc);

        Assert.Empty(CandidateFinder.GroupBySize([a, a, link]));
    }

    [Fact]
    public void Identical_files_become_one_candidate_group()
    {
        using var root = new TempRoot();
        var files = new[]
        {
            Entry(root.CreateFile("x/copy1.bin", "identical content")),
            Entry(root.CreateFile("y/copy2.bin", "identical content")),
            Entry(root.CreateFile("z/copy3.bin", "identical content")),
        };

        var result = new CandidateFinder().Find(files, TestContext.Current.CancellationToken);

        var group = Assert.Single(result.Groups);
        Assert.Equal(17, group.Size.Bytes);
        Assert.Equal(3, group.Files.Count);
        Assert.StartsWith("SHA-256/head+tail(65536):", group.PartialDigest);
        Assert.Empty(result.Unreadable);
        Assert.Empty(result.ChangedSinceScan);
    }

    [Fact]
    public void Same_size_different_content_is_split_by_the_partial_digest()
    {
        using var root = new TempRoot();
        var files = new[]
        {
            Entry(root.CreateFile("a.txt", "AAAA")),
            Entry(root.CreateFile("b.txt", "BBBB")),
            Entry(root.CreateFile("c.txt", "AAAA")),
        };

        var result = new CandidateFinder().Find(files, TestContext.Current.CancellationToken);

        var group = Assert.Single(result.Groups);
        Assert.Equal(new[] { files[0].FullPath, files[2].FullPath }, group.Files.Select(f => f.FullPath));
    }

    [Fact]
    public void Differences_in_the_tail_are_caught()
    {
        using var root = new TempRoot();
        var files = new[]
        {
            Entry(root.CreateFile("a.txt", "head-middle-tail1")),
            Entry(root.CreateFile("b.txt", "head-middle-tail2")),
        };

        var result = new CandidateFinder(sampleBytes: 4).Find(files, TestContext.Current.CancellationToken);

        Assert.Empty(result.Groups);
    }

    [Fact]
    public void Differences_only_in_the_middle_stay_candidates_for_full_verification()
    {
        // The partial digest cannot see the middle; only the full hash (T-10) may separate these.
        using var root = new TempRoot();
        var files = new[]
        {
            Entry(root.CreateFile("a.txt", "head-XXXXXX-tail")),
            Entry(root.CreateFile("b.txt", "head-YYYYYY-tail")),
        };

        var result = new CandidateFinder(sampleBytes: 4).Find(files, TestContext.Current.CancellationToken);

        Assert.Equal(2, Assert.Single(result.Groups).Files.Count);
    }

    [Fact]
    public void Files_that_vanished_are_recorded_and_dropped()
    {
        using var root = new TempRoot();
        var a = Entry(root.CreateFile("a.txt", "same"));
        var b = Entry(root.CreateFile("b.txt", "same"));
        var gone = Entry(root.CreateFile("gone.txt", "same"));
        File.Delete(gone.FullPath);

        var result = new CandidateFinder().Find([a, b, gone], TestContext.Current.CancellationToken);

        Assert.Equal(2, Assert.Single(result.Groups).Files.Count);
        var evidence = Assert.Single(result.Unreadable);
        Assert.Equal(gone.FullPath, evidence.Path);
        Assert.Equal(InaccessibleReason.Vanished, evidence.Reason);
    }

    [Fact]
    public void Files_changed_since_the_scan_are_dropped()
    {
        using var root = new TempRoot();
        var a = Entry(root.CreateFile("a.txt", "same"));
        var b = Entry(root.CreateFile("b.txt", "same"));
        var resized = Entry(root.CreateFile("c.txt", "same"));
        var touched = Entry(root.CreateFile("d.txt", "same"));
        File.WriteAllText(resized.FullPath, "longer now");
        File.SetLastWriteTimeUtc(touched.FullPath, touched.LastWriteTimeUtc.UtcDateTime.AddMinutes(5));

        var result = new CandidateFinder().Find([a, b, resized, touched], TestContext.Current.CancellationToken);

        Assert.Equal(new[] { a.FullPath, b.FullPath }, Assert.Single(result.Groups).Files.Select(f => f.FullPath));
        Assert.Equal(new[] { resized.FullPath, touched.FullPath }, result.ChangedSinceScan.Select(f => f.FullPath).Order());
    }

    [Fact]
    public void Reading_leaves_files_unchanged()
    {
        using var root = new TempRoot();
        var path = root.CreateFile("a.txt", "same");
        root.CreateFile("b.txt", "same");
        var before = File.GetLastWriteTimeUtc(path);

        new CandidateFinder().Find([Entry(path), Entry(root.Resolve("b.txt"))], TestContext.Current.CancellationToken);

        Assert.Equal("same", File.ReadAllText(path));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Honours_cancellation()
    {
        using var root = new TempRoot();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var files = new[] { Entry(root.CreateFile("a", "x")), Entry(root.CreateFile("b", "x")) };

        Assert.ThrowsAny<OperationCanceledException>(() => new CandidateFinder().Find(files, cts.Token));
    }

    internal static FileEntry Entry(string path)
    {
        var info = new FileInfo(path);
        return new FileEntry(info.FullName, new MeasuredSize(info.Length), info.Attributes, info.LastWriteTimeUtc);
    }
}

using System.IO.Compression;
using PCHealth.Core;
using PCHealth.Inventory.Compression;
using PCHealth.Testing;

namespace PCHealth.Inventory.Tests;

/// <summary>The estimate's sampling, skipping and bounds (T-54), with Deflate standing in for XPRESS.</summary>
public sealed class CompressionEstimatorTests : IDisposable
{
    private readonly TempRoot root = new();

    public void Dispose() => root.Dispose();

    [Fact]
    public void Text_compresses_and_the_saving_is_an_estimate_with_its_sample()
    {
        root.CreateFile("logs/a.log", string.Concat(Enumerable.Repeat("2026-10-28 INFO all is well\n", 4000)));
        root.CreateFile("logs/b.log", string.Concat(Enumerable.Repeat("2026-10-28 WARN disk is slow\n", 4000)));

        var estimate = Estimate("logs");

        Assert.Equal(2, estimate.EligibleFiles);
        Assert.Equal(2, estimate.FilesSampled);
        Assert.True(estimate.SavingRatio > 0.5, $"ratio {estimate.SavingRatio}");
        Assert.True(estimate.SampleBytes > 0 && estimate.SampleBytes <= estimate.EligibleBytes.Bytes);
        Assert.Contains("in memory; the savings depend on the files", estimate.Saving.Basis);
        Assert.Contains("from 2 of 2 files", estimate.Saving.Basis);
    }

    [Fact]
    public void Incompressible_files_save_nothing()
    {
        var random = new byte[200_000];
        new Random(42).NextBytes(random);
        root.CreateDirectory("bin");
        File.WriteAllBytes(root.Resolve("bin/noise.dat"), random);

        var estimate = Estimate("bin");

        Assert.Equal(1, estimate.EligibleFiles);
        Assert.Equal(0, estimate.Saving.Bytes);
    }

    [Fact]
    public void Already_compressed_formats_small_files_and_links_are_skipped_with_a_reason()
    {
        root.CreateFile("mixed/photo.JPG", new string('x', 10_000));
        root.CreateFile("mixed/archive.zip", new string('x', 10_000));
        root.CreateFile("mixed/tiny.txt", "small");
        root.CreateFile("mixed/notes.txt", new string('x', 10_000));

        var estimate = Estimate("mixed");

        Assert.Equal(1, estimate.EligibleFiles);
        Assert.Equal(10_000, estimate.EligibleBytes.Bytes);
        Assert.Equal(
            [".zip files are already compressed.", ".JPG files are already compressed.", "Too small to gain: it fills one cluster either way."],
            estimate.Skipped.OrderBy(s => s.Path, StringComparer.Ordinal).Select(s => s.Reason));
    }

    [Fact]
    public void An_empty_folder_estimates_nothing()
    {
        var estimate = new CompressionEstimator(new DeflateChunks()).Estimate(root.CreateDirectory("empty"), [], TestContext.Current.CancellationToken);

        Assert.Equal(0, estimate.Saving.Bytes);
        Assert.Equal(0, estimate.EligibleFiles);
        Assert.Equal(0, estimate.SampleBytes);
        Assert.Equal("no file here can be compressed", estimate.Saving.Basis);
    }

    [Fact]
    public void The_sample_never_exceeds_its_bound()
    {
        for (var i = 0; i < 40; i++)
        {
            root.CreateFile($"many/file{i:00}.txt", new string((char)('a' + (i % 26)), 50_000));
        }

        var estimate = new CompressionEstimator(new DeflateChunks(), maxSampleBytes: 64 * 1024)
            .Estimate(root.Resolve("many"), Files("many"), TestContext.Current.CancellationToken);

        Assert.True(estimate.SampleBytes <= 64 * 1024, $"sampled {estimate.SampleBytes}");
        Assert.True(estimate.FilesSampled < 40);
        Assert.Equal(40, estimate.EligibleFiles);
        Assert.Equal(40 * 50_000, estimate.EligibleBytes.Bytes);
    }

    [Fact]
    public void Estimating_changes_no_file()
    {
        var file = root.CreateFile("keep/report.txt", new string('r', 20_000));
        var written = File.GetLastWriteTimeUtc(file);

        Estimate("keep");

        Assert.Equal(new string('r', 20_000), File.ReadAllText(file));
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
    }

    private CompressionEstimate Estimate(string folder) =>
        new CompressionEstimator(new DeflateChunks()).Estimate(root.Resolve(folder), Files(folder), TestContext.Current.CancellationToken);

    private IEnumerable<FileEntry> Files(string folder) =>
        new RecursiveScanner().Scan(root.Resolve(folder), cancellationToken: TestContext.Current.CancellationToken).Entries.OfType<FileEntry>();

    /// <summary>Deflate in 8 KB chunks: a portable stand-in with the same shape as XPRESS8K.</summary>
    private sealed class DeflateChunks : IChunkCompressor
    {
        public CompressionAlgorithm Algorithm => CompressionAlgorithm.Xpress8K;

        public int ChunkBytes => 8192;

        public int CompressedSize(ReadOnlySpan<byte> chunk)
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                deflate.Write(chunk);
            }

            return (int)output.Length;
        }
    }
}

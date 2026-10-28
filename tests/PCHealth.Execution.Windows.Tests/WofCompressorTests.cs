using System.Text;
using PCHealth.Core;
using PCHealth.Inventory;
using PCHealth.Inventory.Compression;
using PCHealth.Testing;

namespace PCHealth.Execution.Windows.Tests;

/// <summary>The in-memory XPRESS compressor the estimate uses (T-54, D-030), on the CI runner.</summary>
public sealed class WofCompressorTests : IDisposable
{
    private readonly TempRoot root = new();

    public void Dispose() => root.Dispose();

    [Fact]
    public void Repetitive_data_shrinks_and_random_data_is_stored_as_it_is()
    {
        using var compressor = new WofCompressor();
        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("all work and no play ", 400)))[..8192];
        var noise = new byte[8192];
        new Random(7).NextBytes(noise);

        Assert.Equal(8192, compressor.ChunkBytes);
        Assert.InRange(compressor.CompressedSize(text), 1, 2048);
        Assert.InRange(compressor.CompressedSize(noise), 8000, 8192);
        Assert.Equal(0, compressor.CompressedSize([]));
    }

    [Fact]
    public void Lzx_is_not_estimated_in_memory()
    {
        Assert.Throws<NotSupportedException>(() => new WofCompressor(CompressionAlgorithm.Lzx));
    }

    [Fact]
    public void A_folder_estimate_with_the_real_compressor_reports_its_sample_and_leaves_files_unchanged()
    {
        var log = root.CreateFile("logs/app.log", string.Concat(Enumerable.Repeat("2026-10-28 12:00:00 INFO request served\n", 3000)));
        var written = File.GetLastWriteTimeUtc(log);
        using var compressor = new WofCompressor();
        var files = new RecursiveScanner().Scan(root.Resolve("logs"), cancellationToken: TestContext.Current.CancellationToken).Entries.OfType<FileEntry>();

        var estimate = new CompressionEstimator(compressor).Estimate(root.Resolve("logs"), files, TestContext.Current.CancellationToken);

        Assert.True(estimate.SavingRatio > 0.5, $"ratio {estimate.SavingRatio}");
        Assert.StartsWith("Xpress8K compression of a", estimate.Saving.Basis);
        Assert.Equal(written, File.GetLastWriteTimeUtc(log));
        Assert.False(File.GetAttributes(log).HasFlag(FileAttributes.Compressed));
    }
}

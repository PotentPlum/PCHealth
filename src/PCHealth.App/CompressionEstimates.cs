using PCHealth.Core;
using PCHealth.Inventory;
using PCHealth.Inventory.Compression;

namespace PCHealth.App;

/// <summary>An estimate for one folder and the files it looked at, kept together so the action matches what was shown.</summary>
public sealed record FolderEstimate(CompressionEstimate Estimate, IReadOnlyList<FileEntry> Files);

/// <summary>Scans a folder read-only and estimates what compressing it might save (T-56, D-030). Nothing is changed.</summary>
public sealed class CompressionEstimates(IInventoryScanner scanner, IChunkCompressor compressor)
{
    public Task<FolderEstimate> EstimateAsync(string folder, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                var files = scanner.Scan(folder, cancellationToken: cancellationToken).Entries.OfType<FileEntry>().ToArray();
                return new FolderEstimate(new CompressionEstimator(compressor).Estimate(folder, files, cancellationToken), files);
            },
            cancellationToken);
}

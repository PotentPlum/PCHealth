using PCHealth.Core;

namespace PCHealth.Inventory.Compression;

/// <summary>Compresses one chunk in memory, the way the file system would store it.</summary>
public interface IChunkCompressor
{
    CompressionAlgorithm Algorithm { get; }

    /// <summary>The chunk size the file system compresses in, e.g. 8 KB for XPRESS8K.</summary>
    int ChunkBytes { get; }

    /// <summary>The compressed size of <paramref name="chunk"/>; at least its own length when compression does not help.</summary>
    int CompressedSize(ReadOnlySpan<byte> chunk);
}

/// <summary>
/// Estimates what compressing a folder would save (T-54, spec §5.3, rule 9). It reads a bounded sample of chunks spread
/// through the eligible files, compresses them in memory, and applies the ratio to the eligible bytes. Files are only
/// read, never changed. Formats that are already compressed and files too small to gain are skipped with a reason.
/// </summary>
public sealed class CompressionEstimator(IChunkCompressor compressor, long maxSampleBytes = CompressionEstimator.DefaultMaxSampleBytes)
{
    /// <summary>At most this much is read and compressed for one estimate.</summary>
    public const long DefaultMaxSampleBytes = 32L * 1024 * 1024;

    /// <summary>At most this much is read from any one file, so one large file can't dominate the sample.</summary>
    public const int MaxBytesPerFile = 1024 * 1024;

    /// <summary>A file this small fills one cluster either way, so compressing it saves nothing.</summary>
    public const long MinimumFileBytes = 4096;

    // Formats whose contents are already compressed; compressing them again gains next to nothing.
    private static readonly HashSet<string> CompressedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab", ".lz", ".lzma",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif",
        ".mp3", ".aac", ".m4a", ".ogg", ".opus", ".flac", ".wma",
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub", ".jar", ".apk", ".nupkg", ".vsix", ".appx", ".msix",
    };

    private readonly IChunkCompressor compressor = compressor ?? throw new ArgumentNullException(nameof(compressor));

    public CompressionEstimate Estimate(string folder, IEnumerable<FileEntry> files, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(files);
        var skipped = new List<SkippedFile>();
        var eligible = new List<FileEntry>();
        foreach (var file in files.OrderBy(f => f.FullPath, StringComparer.Ordinal))
        {
            if (file.IsReparsePoint)
            {
                skipped.Add(new SkippedFile(file.FullPath, file.Size, "It is a link; links are never compressed."));
            }
            else if (CompressedFormats.Contains(Path.GetExtension(file.Name)))
            {
                skipped.Add(new SkippedFile(file.FullPath, file.Size, $"{Path.GetExtension(file.Name)} files are already compressed."));
            }
            else if (file.Size.Bytes < MinimumFileBytes)
            {
                skipped.Add(new SkippedFile(file.FullPath, file.Size, "Too small to gain: it fills one cluster either way."));
            }
            else
            {
                eligible.Add(file);
            }
        }

        var chunk = compressor.ChunkBytes;
        var perFile = (int)Math.Clamp(maxSampleBytes / Math.Max(1, eligible.Count) / chunk * chunk, chunk, MaxBytesPerFile);

        // When even one chunk per file would exceed the bound, every n-th file is sampled instead.
        var stride = Math.Max(1, (int)Math.Ceiling(eligible.Count * (double)chunk / maxSampleBytes));
        long raw = 0, compressed = 0, eligibleBytes = 0;
        var sampled = 0;
        var buffer = new byte[chunk];
        var position = 0;
        foreach (var file in eligible.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position++ % stride != 0 || raw + chunk > maxSampleBytes)
            {
                eligibleBytes += file.Size.Bytes;
                continue;
            }

            try
            {
                var (fileRaw, fileCompressed) = Sample(file, perFile, buffer, cancellationToken);
                (raw, compressed) = (raw + fileRaw, compressed + fileCompressed);
                eligibleBytes += file.Size.Bytes;
                sampled++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                eligible.Remove(file);
                skipped.Add(new SkippedFile(file.FullPath, file.Size, $"It could not be read: {ex.Message}"));
            }
        }

        var ratio = raw == 0 ? 1 : (double)compressed / raw;
        var saving = (long)Math.Round(eligibleBytes * (1 - ratio));
        var basis = raw == 0
            ? "no file here can be compressed"
            : $"{compressor.Algorithm} compression of a {raw:N0}-byte sample from {sampled} of {eligible.Count} files, in memory; the savings depend on the files";
        return new CompressionEstimate(
            folder, compressor.Algorithm, new EstimatedSize(Math.Max(0, saving), basis), new MeasuredSize(eligibleBytes), eligible.Count, raw, sampled, skipped);
    }

    /// <summary>Chunks spread evenly through the file, up to <paramref name="limit"/> bytes; the file is only read.</summary>
    private (long Raw, long Compressed) Sample(FileEntry file, int limit, byte[] buffer, CancellationToken cancellationToken)
    {
        using var handle = File.OpenHandle(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = RandomAccess.GetLength(handle);
        var chunks = (int)Math.Max(1, Math.Min(limit / buffer.Length, (length + buffer.Length - 1) / buffer.Length));
        var step = Math.Max(buffer.Length, length / chunks / buffer.Length * buffer.Length);
        long raw = 0, compressed = 0;
        for (var i = 0; i < chunks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(handle, buffer, i * step);
            if (read == 0)
            {
                break;
            }

            raw += read;
            compressed += Math.Min(read, compressor.CompressedSize(buffer.AsSpan(0, read)));
        }

        return (raw, compressed);
    }
}

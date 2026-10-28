namespace PCHealth.Core;

/// <summary>Windows Overlay Filter compression formats (D-030). The number is the chunk size each chunk is compressed in.</summary>
public enum CompressionAlgorithm
{
    Xpress4K,
    Xpress8K,
    Xpress16K,
    Lzx,
}

/// <summary>A file the estimate or the compression left out, and why.</summary>
public sealed record SkippedFile(string Path, MeasuredSize Size, string Reason);

/// <summary>
/// How much compressing a folder might save (T-54, spec §5.3). Always an estimate from a bounded sample compressed in
/// memory; never a measurement, and never a promise for files that were not sampled.
/// </summary>
/// <param name="Saving">The estimated bytes saved on the eligible files.</param>
/// <param name="EligibleBytes">The measured size of the files that could be compressed.</param>
/// <param name="SampleBytes">How many bytes were compressed in memory to make the estimate.</param>
/// <param name="Skipped">Files left out: already compressed formats, files too small to gain, unreadable files.</param>
public sealed record CompressionEstimate(
    string Folder,
    CompressionAlgorithm Algorithm,
    EstimatedSize Saving,
    MeasuredSize EligibleBytes,
    int EligibleFiles,
    long SampleBytes,
    int FilesSampled,
    IReadOnlyList<SkippedFile> Skipped)
{
    /// <summary>The estimated share of the eligible bytes saved, from 0 to 1; 0 when nothing is eligible.</summary>
    public double SavingRatio => EligibleBytes.Bytes == 0 ? 0 : (double)Saving.Bytes / EligibleBytes.Bytes;
}

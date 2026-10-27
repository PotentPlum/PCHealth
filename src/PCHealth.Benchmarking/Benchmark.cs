using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using PCHealth.Core;
using PCHealth.Duplicates;
using PCHealth.Inventory;

namespace PCHealth.Benchmarking;

/// <param name="Machine">The only name the report gives the machine: never the computer name, user name or paths.</param>
public sealed record BenchmarkOptions(string Machine, int Files = 20_000, int Large = 200, int Runs = 3)
{
    /// <summary>About how much the fixture writes with these options, for a free-space check before it starts.</summary>
    public long ApproximateBytes => (Files * 8L * 1024) + (Large * 2_621_440L);
}

/// <param name="Markdown">The report, ready to add to docs/benchmarks.md.</param>
/// <param name="DuplicatesAsExpected">Whether the duplicate stage found exactly the pairs the fixture holds.</param>
public sealed record BenchmarkReport(string Markdown, bool DuplicatesAsExpected);

/// <summary>
/// Performance baseline (spec §6.1, §9; T-37, T-69): times the read-only discovery stages on a generated fixture tree
/// and reports wall time, CPU time and share, allocations and working set. These are measurements of one run on one
/// machine, not guarantees. The fixture is written to a new folder that only this class creates, and that folder is
/// deleted afterwards, also when the run is stopped or fails. Nothing else is touched.
/// </summary>
public static class Benchmark
{
    public const long LargeMinimum = 1024 * 1024;

    /// <param name="workFolder">Where the fixture's own new folder is created, usually the temp folder.</param>
    /// <param name="progress">Short descriptions of what is happening now.</param>
    public static BenchmarkReport Run(BenchmarkOptions options, string workFolder, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(workFolder);
        var root = Path.Combine(Path.GetFullPath(workFolder), $"PCHealth.Benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            return Run(options, root, Process.GetCurrentProcess(), progress, cancellationToken);
        }
        finally
        {
            progress?.Report("Deleting the test files…");
            Directory.Delete(root, recursive: true);
        }
    }

    private static BenchmarkReport Run(BenchmarkOptions options, string root, Process process, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var fixture = Fixture.Generate(root, options.Files, options.Large, progress, cancellationToken);
        var rows = new List<string>();
        ScanResult? scan = null;
        for (var run = 1; run <= options.Runs; run++)
        {
            progress?.Report($"Scanning the test files, run {run} of {options.Runs}…");
            rows.Add(Measure(process, $"Scan, run {run}", () => scan = new RecursiveScanner().Scan(root, cancellationToken: cancellationToken)));
        }

        progress?.Report("Summarizing storage…");
        rows.Add(Measure(process, "Storage summary", () => StorageSummary.From(scan!)));
        var files = scan!.Entries.OfType<FileEntry>().ToArray();
        VerificationResult? duplicates = null;
        progress?.Report("Comparing the large files for duplicates…");
        rows.Add(Measure(process, "Duplicates (files of 1 MB or more)", () => duplicates = new DuplicateFinder().Find(files.Where(f => f.Size.Bytes >= LargeMinimum), cancellationToken)));
        process.Refresh();

        var report = $"""
            ### {DateTimeOffset.UtcNow:yyyy-MM-dd} · {options.Machine}: {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical processors, {Gib(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)} memory available to .NET, {RuntimeInformation.FrameworkDescription}

            Fixture: {fixture.Folders:N0} folders, {files.Length:N0} files, {Mib(fixture.Bytes)} in total; {fixture.DuplicatePairs:N0} pairs of identical large files. Freshly written, so the file system cache is warm.

            | Stage | Wall time | CPU time | CPU use (of all cores) | Allocated | Working set after |
            |-|-|-|-|-|-|
            {string.Join(Environment.NewLine, rows)}

            Peak working set of the process: {Mib(process.PeakWorkingSet64)}. Duplicate groups found: {duplicates!.Groups.Count:N0} (expected {fixture.DuplicatePairs:N0}).
            """;
        return new BenchmarkReport(report, duplicates.Groups.Count == fixture.DuplicatePairs);
    }

    private static string Measure(Process process, string stage, Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        process.Refresh();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        action();
        timer.Stop();
        process.Refresh();
        var cpuUsed = process.TotalProcessorTime - cpu;
        var share = cpuUsed.TotalMilliseconds / (Math.Max(timer.Elapsed.TotalMilliseconds, 1) * Environment.ProcessorCount);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"| {stage} | {timer.Elapsed.TotalSeconds:0.00} s | {cpuUsed.TotalSeconds:0.00} s | {share:P0} | {Mib(GC.GetTotalAllocatedBytes(precise: true) - allocated)} | {Mib(process.WorkingSet64)} |");
    }

    private static string Mib(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.0} MiB");

    private static string Gib(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0 / 1024.0:0.0} GiB");

    /// <summary>A deterministic tree: 10 top folders, 10 subfolders each, 10 below those; small files spread over the leaves.</summary>
    private sealed record Fixture(int Folders, long Bytes, int DuplicatePairs)
    {
        public static Fixture Generate(string root, int smallFiles, int largeFiles, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            var random = new Random(37);
            var leaves = new List<string>();
            for (var a = 0; a < 10; a++)
            {
                for (var b = 0; b < 10; b++)
                {
                    for (var c = 0; c < 10; c++)
                    {
                        leaves.Add(Directory.CreateDirectory(Path.Combine(root, $"top{a}", $"mid{b}", $"leaf{c}")).FullName);
                    }
                }
            }

            long bytes = 0;
            var buffer = new byte[4 * 1024 * 1024];
            for (var i = 0; i < smallFiles; i++)
            {
                if (i % 1000 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(string.Create(CultureInfo.CurrentCulture, $"Writing test files: {i:N0} of {smallFiles + largeFiles:N0}…"));
                }

                var size = random.Next(512, 16 * 1024);
                random.NextBytes(buffer.AsSpan(0, size));
                File.WriteAllBytes(Path.Combine(leaves[i % leaves.Count], $"file{i}.dat"), buffer[..size]);
                bytes += size;
            }

            // Half of the large files are unique; the other half are pairs of identical copies in different folders.
            var pairs = largeFiles / 4;
            for (var i = 0; i < largeFiles - pairs; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (i % 10 == 0)
                {
                    progress?.Report(string.Create(CultureInfo.CurrentCulture, $"Writing test files: {smallFiles + i + Math.Min(i, pairs):N0} of {smallFiles + largeFiles:N0}…"));
                }

                var size = random.Next((int)LargeMinimum, buffer.Length);
                random.NextBytes(buffer.AsSpan(0, size));
                var content = buffer[..size];
                File.WriteAllBytes(Path.Combine(leaves[random.Next(leaves.Count)], $"large{i}.bin"), content);
                bytes += size;
                if (i < pairs)
                {
                    File.WriteAllBytes(Path.Combine(leaves[(i * 7 + 3) % leaves.Count], $"copy{i}.bin"), content);
                    bytes += size;
                }
            }

            return new Fixture(leaves.Count + 110, bytes, pairs);
        }
    }
}

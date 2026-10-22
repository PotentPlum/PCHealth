using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using PCHealth.Core;
using PCHealth.Duplicates;
using PCHealth.Inventory;
using PCHealth.Testing;

// Performance baseline (spec §6.1, §9; T-37): scan time, CPU and memory of the read-only discovery stages on a generated
// fixture tree under the temp folder. These are measurements of one run on one machine, not guarantees.
//
//   dotnet run --project tools/PCHealth.Benchmarks -c Release -- --machine "host" [--files 20000] [--large 200] [--runs 3] [--out result.md]
//
// The report names the machine only by the --machine label: never the computer name, user name or real paths (public repo).

var options = Options.Parse(args);
using var root = new TempRoot();
Console.Error.WriteLine($"Generating {options.Files:N0} small and {options.Large:N0} large files under {root.Path}…");
var fixture = Fixture.Generate(root, options.Files, options.Large);

var process = Process.GetCurrentProcess();
var rows = new List<string>();
ScanResult? scan = null;
for (var run = 1; run <= options.Runs; run++)
{
    rows.Add(Measure($"Scan, run {run}", () => scan = new RecursiveScanner().Scan(root.Path)));
}

rows.Add(Measure("Storage summary", () => StorageSummary.From(scan!)));
var files = scan!.Entries.OfType<FileEntry>().ToArray();
VerificationResult? duplicates = null;
rows.Add(Measure("Duplicates (files of 1 MB or more)", () => duplicates = new DuplicateFinder().Find(files.Where(f => f.Size.Bytes >= Fixture.LargeMinimum))));
process.Refresh();

var report = $"""
    ### {DateTimeOffset.UtcNow:yyyy-MM-dd} · {options.Machine}: {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical processors, {Gib(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)} memory available to .NET, {RuntimeInformation.FrameworkDescription}

    Fixture: {fixture.Folders:N0} folders, {files.Length:N0} files, {Mib(fixture.Bytes)} in total; {fixture.DuplicatePairs:N0} pairs of identical large files. Freshly written, so the file system cache is warm.

    | Stage | Wall time | CPU time | CPU use (of all cores) | Allocated | Working set after |
    |-|-|-|-|-|-|
    {string.Join(Environment.NewLine, rows)}

    Peak working set of the process: {Mib(process.PeakWorkingSet64)}. Duplicate groups found: {duplicates!.Groups.Count:N0} (expected {fixture.DuplicatePairs:N0}).
    """;
Console.WriteLine(report);
if (options.Out is { } output)
{
    File.WriteAllText(output, report + Environment.NewLine);
}

return duplicates.Groups.Count == fixture.DuplicatePairs ? 0 : 1;

string Measure(string stage, Action action)
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
    var share = cpuUsed.TotalMilliseconds / (timer.Elapsed.TotalMilliseconds * Environment.ProcessorCount);
    return string.Create(
        CultureInfo.InvariantCulture,
        $"| {stage} | {timer.Elapsed.TotalSeconds:0.00} s | {cpuUsed.TotalSeconds:0.00} s | {share:P0} | {Mib(GC.GetTotalAllocatedBytes(precise: true) - allocated)} | {Mib(process.WorkingSet64)} |");
}

static string Mib(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.0} MiB");

static string Gib(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0 / 1024.0:0.0} GiB");

internal sealed record Options(string Machine, int Files, int Large, int Runs, string? Out)
{
    public static Options Parse(string[] args)
    {
        var options = new Options("unnamed machine", 20_000, 200, 3, null);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            options = args[i] switch
            {
                "--machine" => options with { Machine = args[i + 1] },
                "--files" => options with { Files = int.Parse(args[i + 1], CultureInfo.InvariantCulture) },
                "--large" => options with { Large = int.Parse(args[i + 1], CultureInfo.InvariantCulture) },
                "--runs" => options with { Runs = int.Parse(args[i + 1], CultureInfo.InvariantCulture) },
                "--out" => options with { Out = Path.GetFullPath(args[i + 1]) },
                _ => throw new ArgumentException($"Unknown option '{args[i]}'."),
            };
        }

        return options;
    }
}

/// <summary>A deterministic tree: 10 top folders, 10 subfolders each, 10 below those; small files spread over the leaves.</summary>
internal sealed record Fixture(int Folders, long Bytes, int DuplicatePairs)
{
    public const long LargeMinimum = 1024 * 1024;

    public static Fixture Generate(TempRoot root, int smallFiles, int largeFiles)
    {
        var random = new Random(37);
        var leaves = new List<string>();
        for (var a = 0; a < 10; a++)
        {
            for (var b = 0; b < 10; b++)
            {
                for (var c = 0; c < 10; c++)
                {
                    leaves.Add(root.CreateDirectory(Path.Combine($"top{a}", $"mid{b}", $"leaf{c}")));
                }
            }
        }

        long bytes = 0;
        var buffer = new byte[4 * 1024 * 1024];
        for (var i = 0; i < smallFiles; i++)
        {
            var size = random.Next(512, 16 * 1024);
            random.NextBytes(buffer.AsSpan(0, size));
            File.WriteAllBytes(Path.Combine(leaves[i % leaves.Count], $"file{i}.dat"), buffer[..size]);
            bytes += size;
        }

        // Half of the large files are unique; the other half are pairs of identical copies in different folders.
        var pairs = largeFiles / 4;
        for (var i = 0; i < largeFiles - pairs; i++)
        {
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

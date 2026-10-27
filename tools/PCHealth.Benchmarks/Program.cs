using System.Globalization;
using PCHealth.Benchmarking;

// Performance baseline (spec §6.1, §9; T-37): scan time, CPU and memory of the read-only discovery stages on a generated
// fixture tree under the temp folder. These are measurements of one run on one machine, not guarantees. The app's
// Benchmark page runs the same code (T-69).
//
//   dotnet run --project tools/PCHealth.Benchmarks -c Release -- --machine "host" [--files 20000] [--large 200] [--runs 3] [--out result.md]
//
// The report names the machine only by the --machine label: never the computer name, user name or real paths (public repo).

var (options, output) = Parse(args);
var report = Benchmark.Run(options, Path.GetTempPath(), new Progress<string>(Console.Error.WriteLine));
Console.WriteLine(report.Markdown);
if (output is not null)
{
    File.WriteAllText(output, report.Markdown + Environment.NewLine);
}

return report.DuplicatesAsExpected ? 0 : 1;

static (BenchmarkOptions Options, string? Out) Parse(string[] args)
{
    var options = new BenchmarkOptions("unnamed machine");
    string? output = null;
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        switch (args[i])
        {
            case "--machine": options = options with { Machine = args[i + 1] }; break;
            case "--files": options = options with { Files = int.Parse(args[i + 1], CultureInfo.InvariantCulture) }; break;
            case "--large": options = options with { Large = int.Parse(args[i + 1], CultureInfo.InvariantCulture) }; break;
            case "--runs": options = options with { Runs = int.Parse(args[i + 1], CultureInfo.InvariantCulture) }; break;
            case "--out": output = Path.GetFullPath(args[i + 1]); break;
            default: throw new ArgumentException($"Unknown option '{args[i]}'.");
        }
    }

    return (options, output);
}

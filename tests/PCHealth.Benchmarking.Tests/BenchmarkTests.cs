using PCHealth.Testing;

namespace PCHealth.Benchmarking.Tests;

/// <summary>A small benchmark in a temp root: it reports every stage and leaves nothing behind.</summary>
public class BenchmarkTests
{
    private static readonly BenchmarkOptions Small = new("test machine", Files: 50, Large: 4, Runs: 1);

    [Fact]
    public void A_run_reports_every_stage_and_deletes_its_test_files()
    {
        using var root = new TempRoot();
        var messages = new List<string>();

        var report = Benchmark.Run(Small, root.Path, new Recorder(messages), TestContext.Current.CancellationToken);

        Assert.True(report.DuplicatesAsExpected);
        Assert.Contains(" · test machine: ", report.Markdown);
        Assert.Contains("| Scan, run 1 |", report.Markdown);
        Assert.Contains("| Storage summary |", report.Markdown);
        Assert.Contains("| Duplicates (files of 1 MB or more) |", report.Markdown);
        Assert.Contains("Duplicate groups found: 1 (expected 1).", report.Markdown);
        Assert.DoesNotContain(Environment.MachineName, report.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root.Path, report.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Deleting the test files…", messages[^1]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Fact]
    public void A_stopped_run_still_deletes_its_test_files()
    {
        using var root = new TempRoot();
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Benchmark.Run(Small, root.Path, null, stop.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    private sealed class Recorder(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}

using System.IO;
using PCHealth.App.ViewModels;
using PCHealth.Benchmarking;
using PCHealth.Testing;

namespace PCHealth.App.Tests;

/// <summary>The Benchmark page (T-69), with a small fixture and the reports folder in a temp root.</summary>
public sealed class BenchmarkPageTests : IDisposable
{
    private readonly TempRoot root = new();

    [Fact]
    public async Task A_run_saves_a_new_report_and_shows_where()
    {
        var page = new BenchmarkViewModel(
            root.Resolve("Reports"), new BenchmarkOptions("this PC", Files: 30, Large: 4, Runs: 1), root.CreateDirectory("work"), new ManualClock(new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.Zero)));
        Assert.False(page.OpenFolderCommand.CanExecute(null));

        await page.RunCommand.ExecuteAsync(null);
        var first = page.ReportPath!;
        await page.RunCommand.ExecuteAsync(null);

        Assert.StartsWith("Done. The report is saved in", page.Status);
        Assert.Equal(root.Resolve("Reports"), Path.GetDirectoryName(first));
        Assert.NotEqual(first, page.ReportPath);
        Assert.Equal(2, Directory.GetFiles(root.Resolve("Reports"), "benchmark-*.md").Length);
        Assert.StartsWith("### ", File.ReadAllText(page.ReportPath!));
        Assert.Contains("this PC", page.Report);
        Assert.True(page.OpenFolderCommand.CanExecute(null));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Resolve("work")));
    }

    public void Dispose() => root.Dispose();
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Benchmarking;

namespace PCHealth.App.ViewModels;

/// <summary>
/// Runs the performance benchmark from the app (T-69, D-023): the same code as tools/PCHealth.Benchmarks, on test
/// files it writes to a new folder under the temp folder and deletes afterwards. Each report is saved as its own new
/// file in an easy-to-find folder under Documents, so it can be shared; the page shows where.
/// </summary>
public sealed class BenchmarkViewModel : PageViewModel
{
    private readonly BenchmarkOptions options;
    private readonly string workFolder;
    private readonly TimeProvider clock;
    private CancellationTokenSource? running;
    private string status = "Not run yet.";
    private string? reportPath;
    private string? report;

    public BenchmarkViewModel(string reportsFolder, BenchmarkOptions? options = null, string? workFolder = null, TimeProvider? clock = null)
        : base("Benchmark")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportsFolder);
        ReportsFolder = reportsFolder;
        this.options = options ?? new BenchmarkOptions("this PC");
        this.workFolder = workFolder ?? Path.GetTempPath();
        this.clock = clock ?? TimeProvider.System;
        RunCommand = new AsyncRelayCommand(RunAsync, () => !IsRunning);
        StopCommand = new RelayCommand(() => running?.Cancel(), () => IsRunning);
        OpenFolderCommand = new RelayCommand(
            () => Process.Start(new ProcessStartInfo(ReportsFolder) { UseShellExecute = true })?.Dispose(),
            () => Directory.Exists(ReportsFolder));
        CopyPathCommand = new RelayCommand(() => Clipboard.SetText(ReportPath ?? ReportsFolder));
    }

    /// <summary>The default place for reports: Documents\PC Health Monitor\Benchmarks.</summary>
    public static string DefaultReportsFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PC Health Monitor", "Benchmarks");

    public string Explanation =>
        "Measures how long this app takes to scan, summarize and compare files, and how much processor time and memory it uses. "
        + string.Create(CultureInfo.CurrentCulture, $"It writes {options.Files + options.Large:N0} test files (about {SizeText.Bytes(options.ApproximateBytes)}) to a new folder in your temp folder, ")
        + string.Create(CultureInfo.CurrentCulture, $"measures {options.Runs} scans of them, then deletes them. ")
        + "Your own files are not read or changed. It takes about a minute and keeps the processor busy meanwhile. "
        + "The results are measurements of this run on this PC, not guarantees. The report names the PC only as \"this PC\": no computer name, user name or paths.";

    public string ReportsFolder { get; }

    public bool IsRunning => running is not null;

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    /// <summary>The report file of the latest run, once saved.</summary>
    public string? ReportPath
    {
        get => reportPath;
        private set => SetProperty(ref reportPath, value);
    }

    public string? Report
    {
        get => report;
        private set => SetProperty(ref report, value);
    }

    public IAsyncRelayCommand RunCommand { get; }

    public IRelayCommand StopCommand { get; }

    public IRelayCommand OpenFolderCommand { get; }

    public IRelayCommand CopyPathCommand { get; }

    private async Task RunAsync()
    {
        var free = FreeSpace(workFolder);
        if (free is { } bytes && bytes < options.ApproximateBytes * 2)
        {
            Status = $"Not run: the benchmark needs about {SizeText.Bytes(options.ApproximateBytes * 2)} free where your temp folder is, and {SizeText.Bytes(bytes)} is free.";
            return;
        }

        running = new CancellationTokenSource();
        Changed();
        try
        {
            var token = running.Token;
            var progress = new Progress<string>(message => Status = message);
            var result = await Task.Run(() => Benchmark.Run(options, workFolder, progress, token), token);
            Directory.CreateDirectory(ReportsFolder);
            ReportPath = NewReportFile();
            await File.WriteAllTextAsync(ReportPath, result.Markdown + Environment.NewLine, CancellationToken.None);
            Report = result.Markdown;
            OpenFolderCommand.NotifyCanExecuteChanged();
            Status = result.DuplicatesAsExpected
                ? $"Done. The report is saved in {ReportPath}."
                : $"Done, but the duplicate check did not find what the test files hold; the report says what it found. It is saved in {ReportPath}.";
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped. The test files were deleted; nothing else was changed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"The benchmark stopped: {ex.Message}";
        }
        finally
        {
            running.Dispose();
            running = null;
            Changed();
        }
    }

    /// <summary>A new file name for every run; an existing report is never overwritten.</summary>
    private string NewReportFile()
    {
        var name = $"benchmark-{clock.GetLocalNow():yyyyMMdd-HHmmss}";
        var path = Path.Combine(ReportsFolder, name + ".md");
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(ReportsFolder, $"{name}-{i}.md");
        }

        return path;
    }

    private static long? FreeSpace(string folder)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(folder)) is { Length: > 0 } drive ? new DriveInfo(drive).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsRunning));
        RunCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }
}

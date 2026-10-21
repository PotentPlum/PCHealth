using PCHealth.Testing;

namespace PCHealth.Inventory.Tests;

public class BackgroundScanTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Runs_on_a_below_normal_priority_background_thread()
    {
        using var root = new TempRoot();
        root.CreateFile("a/f.txt");
        Thread? scanThread = null;
        var priority = ThreadPriority.Normal;
        var isBackground = false;

        using var scan = Start(root.Path, new SyncProgress(_ =>
        {
            scanThread = Thread.CurrentThread;
            priority = Thread.CurrentThread.Priority;
            isBackground = Thread.CurrentThread.IsBackground;
        }));
        await scan.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.NotNull(scanThread);
        Assert.NotSame(Thread.CurrentThread, scanThread);
        Assert.True(isBackground);
        Assert.Equal(ThreadPriority.BelowNormal, priority);
    }

    [Fact]
    public async Task Reports_counts_and_elapsed_time()
    {
        using var root = new TempRoot();
        root.CreateFile("a.txt", "123");
        root.CreateFile("sub/b.txt", "45");
        var reports = new List<ScanProgress>();

        using var scan = Start(root.Path, new SyncProgress(reports.Add));
        var result = await scan.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var stats = result.Statistics;
        Assert.Equal(2, stats.DirectoriesScanned);
        Assert.Equal(2, stats.FilesScanned);
        Assert.Equal(5, stats.BytesScanned.Bytes);
        Assert.True(stats.Elapsed > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, stats.Paused);
        Assert.Equal(stats.Elapsed, stats.Active);
        Assert.Equal(reports.Select(r => r.Elapsed).Order(), reports.Select(r => r.Elapsed));
        Assert.True(reports[^1].Elapsed <= stats.Elapsed);
    }

    [Fact]
    public async Task Pauses_and_resumes()
    {
        using var root = new TempRoot();
        for (var i = 0; i < 5; i++)
        {
            root.CreateFile($"d{i}/f.txt");
        }

        using var firstReport = new ManualResetEventSlim();
        var reports = 0;
        BackgroundScan? scan = null;
        using var started = new ManualResetEventSlim();
        scan = Start(root.Path, new SyncProgress(_ =>
        {
            started.Wait(TestContext.Current.CancellationToken);
            if (Interlocked.Increment(ref reports) == 1)
            {
                scan!.Pause();
                firstReport.Set();
            }
        }));
        using (scan)
        {
            started.Set();
            Assert.True(firstReport.Wait(Timeout, TestContext.Current.CancellationToken));
            Assert.True(scan.IsPaused);

            await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref reports));
            Assert.False(scan.Completion.IsCompleted);

            scan.Resume();
            var result = await scan.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(6, Volatile.Read(ref reports));
            Assert.Equal(6, result.Statistics.DirectoriesScanned);
            Assert.True(result.Statistics.Paused >= TimeSpan.FromMilliseconds(200), $"Paused only {result.Statistics.Paused}.");
            Assert.True(result.Statistics.Active < result.Statistics.Elapsed);
        }
    }

    [Fact]
    public async Task Cancels_while_paused()
    {
        using var root = new TempRoot();
        root.CreateFile("a/f.txt");
        root.CreateFile("b/f.txt");
        using var paused = new ManualResetEventSlim();
        BackgroundScan? scan = null;
        using var started = new ManualResetEventSlim();
        scan = Start(root.Path, new SyncProgress(_ =>
        {
            started.Wait(TestContext.Current.CancellationToken);
            scan!.Pause();
            paused.Set();
        }));
        using (scan)
        {
            started.Set();
            Assert.True(paused.Wait(Timeout, TestContext.Current.CancellationToken));

            scan.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken));
            Assert.True(scan.Completion.IsCanceled);
        }
    }

    [Fact]
    public async Task Honours_the_callers_cancellation_token()
    {
        using var root = new TempRoot();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var scan = BackgroundScan.Start(new RecursiveScanner(), root.Path, cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Surfaces_scan_errors_through_completion()
    {
        using var root = new TempRoot();

        using var scan = Start(root.Resolve("missing"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => scan.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken));
    }

    private static BackgroundScan Start(string path, IProgress<ScanProgress>? progress = null) =>
        BackgroundScan.Start(new RecursiveScanner(), path, progress, TestContext.Current.CancellationToken);

    private sealed class SyncProgress(Action<ScanProgress> onReport) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => onReport(value);
    }
}

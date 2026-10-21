namespace PCHealth.Inventory;

/// <summary>
/// Runs a scan on its own below-normal-priority thread so foreground work comes first (spec §3.5, §6.1),
/// and lets the caller pause, resume or cancel it.
/// </summary>
/// <remarks>
/// Thread priority covers CPU only. Lowering I/O priority needs a Windows API and belongs in a Windows project.
/// </remarks>
public sealed class BackgroundScan : IDisposable
{
    private readonly PauseTokenSource pause = new();
    private readonly CancellationTokenSource cancellation;
    private readonly TaskCompletionSource<ScanResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private BackgroundScan(CancellationToken cancellationToken)
    {
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    /// <summary>Completes with the scan result, or faults/cancels the way the scan did.</summary>
    public Task<ScanResult> Completion => completion.Task;

    public bool IsPaused => pause.IsPaused;

    public static BackgroundScan Start(
        IInventoryScanner scanner,
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var scan = new BackgroundScan(cancellationToken);
        var thread = new Thread(() => scan.Run(scanner, rootPath, progress))
        {
            Name = "PCHealth inventory scan",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
        return scan;
    }

    public void Pause() => pause.Pause();

    public void Resume() => pause.Resume();

    public void Cancel() => cancellation.Cancel();

    /// <summary>Call only after <see cref="Completion"/> has finished.</summary>
    public void Dispose()
    {
        pause.Dispose();
        cancellation.Dispose();
    }

    private void Run(IInventoryScanner scanner, string rootPath, IProgress<ScanProgress>? progress)
    {
        try
        {
            completion.SetResult(scanner.Scan(rootPath, progress, pause.Token, cancellation.Token));
        }
        catch (OperationCanceledException ex)
        {
            completion.SetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }
}

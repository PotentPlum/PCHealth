using System.Diagnostics;

namespace PCHealth.Inventory;

/// <summary>Lets a caller pause and resume a running scan, the way a <see cref="CancellationTokenSource"/> cancels one.</summary>
public sealed class PauseTokenSource : IDisposable
{
    private readonly ManualResetEventSlim running = new(initialState: true);

    public PauseToken Token => new(this);

    public bool IsPaused => !running.IsSet;

    public void Pause() => running.Reset();

    public void Resume() => running.Set();

    public void Dispose() => running.Dispose();

    internal TimeSpan WaitWhilePaused(CancellationToken cancellationToken)
    {
        if (running.IsSet)
        {
            return TimeSpan.Zero;
        }

        var started = Stopwatch.GetTimestamp();
        running.Wait(cancellationToken);
        return Stopwatch.GetElapsedTime(started);
    }
}

/// <summary>Checked by a scan between units of work. The default token never pauses.</summary>
public readonly struct PauseToken
{
    private readonly PauseTokenSource? source;

    internal PauseToken(PauseTokenSource source) => this.source = source;

    public bool IsPaused => source?.IsPaused ?? false;

    /// <summary>Blocks while paused and returns how long it waited.</summary>
    /// <exception cref="OperationCanceledException">Cancelled while paused.</exception>
    public TimeSpan WaitWhilePaused(CancellationToken cancellationToken) =>
        source?.WaitWhilePaused(cancellationToken) ?? TimeSpan.Zero;
}

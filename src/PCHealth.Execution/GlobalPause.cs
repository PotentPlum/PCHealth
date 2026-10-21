namespace PCHealth.Execution;

/// <summary>
/// The global Pause (spec §4): while paused, the gateway refuses every change, including items still queued in a
/// running request. Pausing never touches the activity history. The state can be kept in a file so it survives restarts.
/// </summary>
public sealed class GlobalPause
{
    private readonly string? statePath;
    private readonly Lock gate = new();
    private bool paused;

    /// <param name="statePath">Where the paused state is kept; null keeps it in memory only.</param>
    public GlobalPause(string? statePath = null)
    {
        if (statePath is not null && !Path.IsPathFullyQualified(statePath))
        {
            throw new ArgumentException($"The pause state needs a full path: '{statePath}'.", nameof(statePath));
        }

        this.statePath = statePath;
        paused = statePath is not null && File.Exists(statePath);
    }

    public event EventHandler? Changed;

    public bool IsPaused
    {
        get
        {
            lock (gate)
            {
                return paused;
            }
        }
    }

    public void Pause() => Set(true);

    public void Resume() => Set(false);

    private void Set(bool value)
    {
        lock (gate)
        {
            if (paused == value)
            {
                return;
            }

            if (statePath is not null)
            {
                if (value)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                    File.WriteAllText(statePath, "Changes are paused. Delete this file or resume in the app to allow them again.");
                }
                else
                {
                    File.Delete(statePath);
                }
            }

            paused = value;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

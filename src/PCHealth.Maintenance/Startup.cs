namespace PCHealth.Maintenance;

/// <summary>How an item gets started (spec §5.4: "the specific mechanism involved").</summary>
public enum StartupMechanism
{
    /// <summary>A value under a <c>Run</c> registry key.</summary>
    RunKey,

    /// <summary>A value under a <c>RunOnce</c> key: started at the next sign-in, then removed by Windows.</summary>
    RunOnceKey,

    /// <summary>A file or shortcut in a Startup folder.</summary>
    StartupFolder,

    /// <summary>A scheduled task with a logon or boot trigger.</summary>
    ScheduledTask,

    /// <summary>A service that starts automatically.</summary>
    Service,
}

public enum StartupState
{
    Enabled,
    Disabled,

    /// <summary>The state could not be read; never assumed.</summary>
    Unknown,
}

/// <summary>What the signature of an item's file says.</summary>
public enum SignatureState
{
    /// <summary>Not checked, e.g. because the file does not exist.</summary>
    NotChecked,

    /// <summary>The file carries a signature Windows accepts.</summary>
    Valid,

    /// <summary>The file carries a signature that did not verify.</summary>
    Invalid,

    /// <summary>The file carries no signature of its own; Windows files are often signed in a catalog instead.</summary>
    Unsigned,
}

/// <summary>The signature of a file: its state and, when the file is signed, who signed it.</summary>
public sealed record Signature(SignatureState State, string? Publisher)
{
    public static readonly Signature NotChecked = new(SignatureState.NotChecked, null);

    public string Describe() => State switch
    {
        SignatureState.Valid => $"Signed by {Publisher ?? "an unnamed publisher"}.",
        SignatureState.Invalid => $"Carries a signature{(Publisher is null ? "" : $" from {Publisher}")} that did not verify.",
        SignatureState.Unsigned => "No signature in the file itself (Windows files are often signed in a catalog instead).",
        _ => "Signature not checked.",
    };
}

/// <summary>
/// One thing that starts with Windows or at sign-in (T-57): its mechanism, where it is registered, what it runs,
/// whether that file exists, who signed it, and whether it is enabled, each as read. Nothing here changes it.
/// </summary>
/// <param name="Location">Where it is registered, e.g. a registry key, a folder, a task path or a service name.</param>
/// <param name="Command">The command as registered; null when there is none.</param>
/// <param name="ImagePath">The program the command starts, once quotes and arguments are removed; null when unknown.</param>
/// <param name="FileExists">Whether the program exists; null when there is no path to check.</param>
/// <param name="StateEvidence">What the enabled state was read from.</param>
public sealed record StartupItem(
    string Name,
    StartupMechanism Mechanism,
    string Location,
    string? Command,
    string? ImagePath,
    bool? FileExists,
    Signature Signature,
    StartupState State,
    string StateEvidence)
{
    public string MechanismText => Mechanism switch
    {
        StartupMechanism.RunKey => "Registry Run key",
        StartupMechanism.RunOnceKey => "Registry RunOnce key (runs once at the next sign-in)",
        StartupMechanism.StartupFolder => "Startup folder",
        StartupMechanism.ScheduledTask => "Scheduled task",
        _ => "Service",
    };
}

/// <summary>A place that could not be read, so the inventory is not complete for it.</summary>
public sealed record UnreadSource(string Source, string Reason);

public sealed record StartupInventory(IReadOnlyList<StartupItem> Items, IReadOnlyList<UnreadSource> NotRead)
{
    public bool IsComplete => NotRead.Count == 0;
}

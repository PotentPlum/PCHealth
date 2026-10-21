namespace PCHealth.Execution;

/// <summary>What an item looks like right now, read right before acting.</summary>
public sealed record ItemState(bool IsDirectory, long SizeBytes, DateTimeOffset LastWriteTimeUtc, DateTimeOffset CreationTimeUtc, FileAttributes Attributes);

/// <summary>
/// The platform layer the gateway acts through. Windows implements it with handle-based moves, the Recycle Bin and
/// Restart Manager (T-24 to T-26); tests use a fake. Every method acts on a final path the gateway has just
/// resolved and checked. Nothing here decides whether a change is allowed; the gateway does.
/// </summary>
public interface IPlatformOperations
{
    /// <summary>The item's current state, or null if it no longer exists.</summary>
    ItemState? Inspect(string finalPath);

    /// <summary>Processes that hold the item open, by user-readable name; empty when none do.</summary>
    IReadOnlyList<string> ProcessesUsing(string finalPath);

    /// <summary>True if changing the item needs administrator rights (D-019: refused in Phase 1).</summary>
    bool NeedsElevation(string finalPath);

    /// <summary>True if both paths are on the same volume, so a move is a rename and not a copy (D-016).</summary>
    bool SameVolume(string path, string otherPath);

    /// <exception cref="IOException">The item could not be moved; nothing changed.</exception>
    void MoveToRecycleBin(string finalPath, bool isDirectory);

    /// <summary>Moves the item into <paramref name="destination"/> by handle, without following links (T-24).</summary>
    /// <exception cref="IOException">The item could not be moved; nothing changed.</exception>
    void MoveToQuarantine(string finalPath, string destination, bool isDirectory);
}

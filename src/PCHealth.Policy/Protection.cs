namespace PCHealth.Policy;

public enum ProtectionKind
{
    OperatingSystem,
    ProgramFiles,
    ApplicationBinary,
    Boot,
    PersonalFolder,

    /// <summary>The root of a drive.</summary>
    VolumeRoot,

    /// <summary>This app's own history, settings and quarantine (D-021).</summary>
    AppData,
}

/// <summary>A folder (and everything below it) that is protected by default, with the reason shown to the user.</summary>
public sealed record ProtectedLocation(string Path, ProtectionKind Kind, string Reason);

/// <summary>Whether a target may be changed, and if not, which protection blocked it and why.</summary>
public sealed record ProtectionDecision
{
    private ProtectionDecision(ProtectionKind? kind, string reason)
    {
        Kind = kind;
        Reason = reason;
    }

    public static ProtectionDecision Allowed { get; } = new(null, "");

    public bool IsBlocked => Kind is not null;

    public ProtectionKind? Kind { get; }

    /// <summary>A user-readable reason; empty when allowed.</summary>
    public string Reason { get; }

    internal static ProtectionDecision Block(ProtectionKind kind, string reason) => new(kind, reason);
}

/// <summary>
/// The default protections (spec §3.4, §6.3, D-014): system, program, boot and personal folders, drive roots,
/// and program binaries anywhere. Checks expect a final path from the path resolver, so links and short names
/// have already been resolved.
/// </summary>
public sealed class ProtectionPolicy
{
    /// <summary>Program code that may belong to an installed application.</summary>
    public static readonly IReadOnlySet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".drv", ".ocx", ".cpl", ".scr", ".efi", ".com", ".mui",
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public ProtectionPolicy(IEnumerable<ProtectedLocation> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        Locations = locations
            .Select(l => l with { Path = Normalize(l.Path) })
            .ToArray();
        if (Locations.Any(l => string.IsNullOrWhiteSpace(l.Reason)))
        {
            throw new ArgumentException("Every protected location needs a reason.", nameof(locations));
        }
    }

    public IReadOnlyList<ProtectedLocation> Locations { get; }

    public ProtectionDecision Check(string finalPath) => Check(finalPath, MoveRole.None);

    /// <summary>
    /// The protection for a move the user chose item by item into a folder they chose (T-51, D-029). It deletes
    /// nothing, so items inside personal folders may move, and a personal folder may receive them. The personal
    /// folders themselves, every other protected location, folders that contain one, and program files stay protected.
    /// </summary>
    public ProtectionDecision CheckMove(string finalPath, bool asDestination = false) =>
        Check(finalPath, asDestination ? MoveRole.Destination : MoveRole.Item);

    private enum MoveRole
    {
        None,
        Item,
        Destination,
    }

    private ProtectionDecision Check(string finalPath, MoveRole role)
    {
        var target = Normalize(finalPath);

        if (Path.GetPathRoot(target) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), target, PathComparison))
        {
            return ProtectionDecision.Block(ProtectionKind.VolumeRoot, $"'{target}' is the root of a drive.");
        }

        foreach (var location in Locations)
        {
            var movable = location.Kind == ProtectionKind.PersonalFolder
                && (role == MoveRole.Destination || (role == MoveRole.Item && !string.Equals(target, location.Path, PathComparison)));
            if (!movable && IsSameOrBelow(target, location.Path))
            {
                return ProtectionDecision.Block(location.Kind, $"'{target}' is protected: {location.Reason}");
            }
        }

        // Changing a folder changes everything in it, so a folder that contains a protected location is protected too.
        // A personal folder that receives a move is the one exception: putting items into it changes nothing already there.
        foreach (var location in Locations)
        {
            var receives = role == MoveRole.Destination && location.Kind == ProtectionKind.PersonalFolder
                && string.Equals(target, location.Path, PathComparison);
            if (!receives && IsSameOrBelow(location.Path, target))
            {
                return ProtectionDecision.Block(
                    location.Kind,
                    $"'{target}' contains '{location.Path}', which is protected: {location.Reason}");
            }
        }

        if (role != MoveRole.Destination && BinaryExtensions.Contains(Path.GetExtension(target)))
        {
            return ProtectionDecision.Block(
                ProtectionKind.ApplicationBinary,
                $"'{Path.GetFileName(target)}' is program code that may belong to an installed application.");
        }

        return ProtectionDecision.Allowed;
    }

    internal static bool IsSameOrBelow(string path, string folder)
    {
        if (string.Equals(path, folder, PathComparison))
        {
            return true;
        }

        var prefix = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"Protection checks need a full path: '{path}'.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}

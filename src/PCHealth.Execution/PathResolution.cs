namespace PCHealth.Execution;

public enum PathRejectionReason
{
    /// <summary>Empty, or contains characters no file name can hold.</summary>
    InvalidSyntax,

    /// <summary>Relative, drive-relative ("C:foo") or rooted without a drive ("\foo").</summary>
    NotFullyQualified,

    /// <summary>A device or volume namespace such as <c>\\.\</c> or <c>\\?\GLOBALROOT</c>.</summary>
    UnsupportedPrefix,

    /// <summary>UNC shares and paths that resolve to the network are out of scope.</summary>
    NetworkPath,

    AlternateDataStream,

    /// <summary>CON, PRN, AUX, NUL, COM1–9, LPT1–9, with or without an extension.</summary>
    ReservedDeviceName,

    /// <summary>Windows silently strips these, so the name would not mean what it says.</summary>
    TrailingDotOrSpace,

    NotFound,

    AccessDenied,

    /// <summary>The path exists but its final location could not be determined.</summary>
    Unresolvable,
}

/// <summary>
/// The outcome of resolving a path to the object it actually names (spec §6.3). Either a final path read
/// back from an open handle, or a rejection with a reason a user can read. There is no partial success.
/// </summary>
public sealed record PathResolution
{
    private PathResolution(string? finalPath, FileAttributes attributes, PathRejectionReason? rejection, string message)
    {
        FinalPath = finalPath;
        Attributes = attributes;
        Rejection = rejection;
        Message = message;
    }

    /// <summary>The canonical path: long names, true case, no <c>..</c>, no <c>\\?\</c> prefix. Null when rejected.</summary>
    public string? FinalPath { get; }

    public FileAttributes Attributes { get; }

    public PathRejectionReason? Rejection { get; }

    /// <summary>Why the path was rejected; empty when resolved.</summary>
    public string Message { get; }

    public bool IsResolved => FinalPath is not null;

    public bool IsDirectory => IsResolved && Attributes.HasFlag(FileAttributes.Directory);

    /// <summary>The path itself is a link (symlink, junction, mount point); it was not followed.</summary>
    public bool IsReparsePoint => IsResolved && Attributes.HasFlag(FileAttributes.ReparsePoint);

    public static PathResolution Resolved(string finalPath, FileAttributes attributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        return new(finalPath, attributes, null, "");
    }

    public static PathResolution Rejected(PathRejectionReason reason, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new(null, 0, reason, message);
    }
}

public enum LinkHandling
{
    /// <summary>Resolve the path itself; if its last component is a link, report the link (parent links are still resolved).</summary>
    DoNotFollow,

    /// <summary>Follow every link, including the last component, to the object finally reached.</summary>
    Follow,
}

/// <summary>Resolves a path to its final location. Implementations fail closed: anything they cannot resolve is rejected.</summary>
public interface IPathResolver
{
    PathResolution Resolve(string path, LinkHandling links = LinkHandling.DoNotFollow);
}

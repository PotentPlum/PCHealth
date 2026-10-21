namespace PCHealth.Core;

/// <summary>One fact that supports a finding (spec §7: "What is the evidence?").</summary>
public abstract record Evidence
{
    protected Evidence(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Description = description;
    }

    public string Description { get; }
}

/// <summary>A path involved in the finding.</summary>
public sealed record PathEvidence : Evidence
{
    public PathEvidence(string path, string description)
        : base(description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    public string Path { get; }
}

/// <summary>A size that was measured, not estimated.</summary>
public sealed record MeasurementEvidence : Evidence
{
    public MeasurementEvidence(MeasuredSize size, string description)
        : base(description)
    {
        Size = size;
    }

    public MeasuredSize Size { get; }
}

public enum InaccessibleReason
{
    AccessDenied,

    /// <summary>The entry disappeared between being listed and being read.</summary>
    Vanished,
    IoError,
}

/// <summary>A path that could not be read. Recorded so a partial result is never presented as complete.</summary>
public sealed record InaccessibleEvidence : Evidence
{
    public InaccessibleEvidence(string path, InaccessibleReason reason, string description)
        : base(description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        Path = path;
        Reason = reason;
    }

    public string Path { get; }

    public InaccessibleReason Reason { get; }
}

/// <summary>A file's identity at the moment its contents were hashed: path, size, last-write time and hash.</summary>
public sealed record FileContentEvidence : Evidence
{
    /// <param name="contentHash">The hash prefixed with its algorithm, e.g. <c>SHA-256:…</c> (D-006).</param>
    public FileContentEvidence(string path, MeasuredSize size, DateTimeOffset lastWriteTimeUtc, string contentHash)
        : base($"{path} · {size.Bytes} bytes · modified {lastWriteTimeUtc:u} · {contentHash}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        Path = path;
        Size = size;
        LastWriteTimeUtc = lastWriteTimeUtc;
        ContentHash = contentHash;
    }

    public string Path { get; }

    public MeasuredSize Size { get; }

    public DateTimeOffset LastWriteTimeUtc { get; }

    public string ContentHash { get; }
}

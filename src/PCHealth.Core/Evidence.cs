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

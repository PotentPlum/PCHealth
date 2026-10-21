namespace PCHealth.Core;

/// <summary>
/// A byte count read from the file system. Measured and estimated sizes are separate types
/// with no conversion between them, so an estimate can never be shown as a measurement (spec §5.1).
/// </summary>
public readonly record struct MeasuredSize
{
    public MeasuredSize(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Bytes = bytes;
    }

    public long Bytes { get; }

    public static MeasuredSize Zero => default;

    public static MeasuredSize operator +(MeasuredSize left, MeasuredSize right) => new(checked(left.Bytes + right.Bytes));

    public override string ToString() => $"{Bytes} bytes (measured)";
}

/// <summary>
/// A byte count that has not been measured, such as "potentially reclaimable" space.
/// <see cref="Basis"/> says how the figure was derived.
/// </summary>
public readonly record struct EstimatedSize
{
    public EstimatedSize(long bytes, string basis)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(basis);
        Bytes = bytes;
        Basis = basis;
    }

    public long Bytes { get; }

    public string Basis { get; }

    public override string ToString() => $"~{Bytes} bytes (estimated: {Basis})";
}

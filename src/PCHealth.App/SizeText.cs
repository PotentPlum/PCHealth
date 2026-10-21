using System.Globalization;
using PCHealth.Core;

namespace PCHealth.App;

/// <summary>Sizes for people. Measured and estimated sizes are labeled differently, never mixed (spec §5.1).</summary>
public static class SizeText
{
    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes} bytes")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }

    public static string Measured(MeasuredSize size) => $"{Bytes(size.Bytes)} (measured)";

    public static string Estimated(EstimatedSize? size) =>
        size is { } s ? $"about {Bytes(s.Bytes)} (estimated: {s.Basis})" : "no space estimate";
}

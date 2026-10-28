using System.Globalization;
using System.Runtime.InteropServices;

namespace PCHealth.Maintenance.Windows;

/// <summary>
/// The Application and System logs through the Event Log API, and Reliability Monitor's records through WMI
/// (<c>Win32_ReliabilityRecords</c>, read with the built-in scripting COM object, no extra package). Read-only (T-60).
/// </summary>
public sealed class WindowsTimelineSources : ITimelineSources
{
    public IReadOnlyList<string> Events(string channel, string query, int max) => WindowsEventLog.Query(channel, query, max);

    public IReadOnlyList<ReliabilityRecord> ReliabilityRecords(int max)
    {
        var records = new List<ReliabilityRecord>();
        try
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!)!;
            dynamic service = locator.ConnectServer(".", @"root\cimv2");
            dynamic results = service.ExecQuery("SELECT TimeGenerated, SourceName, EventIdentifier, ProductName, Message FROM Win32_ReliabilityRecords", "WQL", 0x30);
            foreach (var row in results)
            {
                if (records.Count >= max)
                {
                    break;
                }

                if (Time((string?)row.TimeGenerated) is { } time)
                {
                    records.Add(new ReliabilityRecord(time, (string?)row.SourceName ?? "Reliability Monitor", (int?)row.EventIdentifier ?? 0, (string?)row.ProductName, (string?)row.Message));
                }
            }
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or UnauthorizedAccessException)
        {
            throw new IOException($"Reliability Monitor could not be read: {ex.Message}", ex);
        }

        return records;
    }

    /// <summary>A CIM date such as <c>20261027075812.123456+060</c>: local time, then the offset in minutes.</summary>
    internal static DateTimeOffset? Time(string? cim)
    {
        if (cim is null || cim.Length < 25
            || !DateTime.TryParseExact(cim[..21], "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            || !int.TryParse(cim[21..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var minutes))
        {
            return null;
        }

        return new DateTimeOffset(local, TimeSpan.FromMinutes(minutes)).ToUniversalTime();
    }
}

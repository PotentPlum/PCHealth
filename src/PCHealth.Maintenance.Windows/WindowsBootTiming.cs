namespace PCHealth.Maintenance.Windows;

/// <summary>The last boot's timing from the Diagnostics-Performance log, which Windows lets only administrators read by default (T-58).</summary>
public static class WindowsBootTiming
{
    public const string Channel = "Microsoft-Windows-Diagnostics-Performance/Operational";

    public static BootTiming Read()
    {
        try
        {
            var events = WindowsEventLog.Query(Channel, "*[System[(EventID=100)]]", 1);
            return events.Count == 0 ? BootTiming.Unavailable("No boot has been recorded in the Diagnostics-Performance log.") : BootTiming.FromEventXml(events[0]);
        }
        catch (UnauthorizedAccessException)
        {
            return BootTiming.Unavailable("Boot timing is not available without administrator rights.");
        }
        catch (IOException ex)
        {
            return BootTiming.Unavailable($"Boot timing is not available: {ex.Message}");
        }
    }
}

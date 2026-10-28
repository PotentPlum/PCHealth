using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PCHealth.Core;

namespace PCHealth.Maintenance;

public enum TimelineKind
{
    AppCrash,
    AppHang,

    /// <summary>Windows Error Reporting recorded a problem report.</summary>
    ProblemReport,
    UnexpectedShutdown,
    ServiceFailure,
    DriverFailure,
    UpdateInstalled,
    UpdateFailed,
    AppInstalled,
    AppRemoved,

    /// <summary>A record from Reliability Monitor that no other source explains.</summary>
    ReliabilityRecord,
}

/// <summary>One thing that happened on this PC (T-60, spec §5.4): when, from which log, what kind, and the evidence.</summary>
public sealed record TimelineEvent(DateTimeOffset TimeUtc, string Source, TimelineKind Kind, string Summary, IReadOnlyList<Evidence> Evidence);

/// <summary>A Reliability Monitor record, as Windows keeps it.</summary>
public sealed record ReliabilityRecord(DateTimeOffset TimeUtc, string SourceName, int EventId, string? ProductName, string? Message);

public sealed record Timeline(IReadOnlyList<TimelineEvent> Events, IReadOnlyList<UnreadSource> NotRead)
{
    public bool IsComplete => NotRead.Count == 0;
}

/// <summary>Where timeline events come from. Windows reads the event logs and Reliability Monitor read-only; tests use recorded XML.</summary>
public interface ITimelineSources
{
    /// <summary>Events of <paramref name="channel"/> that match <paramref name="query"/>, newest first, as event XML.</summary>
    /// <exception cref="UnauthorizedAccessException">The log needs rights the user does not have.</exception>
    /// <exception cref="IOException">The log could not be read.</exception>
    IReadOnlyList<string> Events(string channel, string query, int max);

    /// <exception cref="IOException">Reliability Monitor could not be read.</exception>
    IReadOnlyList<ReliabilityRecord> ReliabilityRecords(int max);
}

/// <summary>Reads app crashes and hangs, unexpected shutdowns, service and driver failures, updates and installs into one timeline (T-60).</summary>
public sealed class TimelineReader(ITimelineSources sources)
{
    public const int MaxPerSource = 2000;

    public const string ApplicationQuery =
        "*[System[(Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Windows Error Reporting'] and EventID=1001)"
        + " or (Provider[@Name='Application Hang'] and EventID=1002) or (Provider[@Name='MsiInstaller'] and (EventID=1033 or EventID=1034 or EventID=11707 or EventID=11724))]]";

    public const string SystemQuery =
        "*[System[(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or (Provider[@Name='EventLog'] and EventID=6008)"
        + " or (Provider[@Name='Service Control Manager'] and EventID>=7000 and EventID<=7034)"
        + " or (Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=20))]]";

    private static readonly HashSet<string> LogSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "Application Error", "Application Hang", "Windows Error Reporting", "MsiInstaller", "Microsoft-Windows-WindowsUpdateClient",
        "EventLog", "Microsoft-Windows-Kernel-Power", "Service Control Manager",
    };

    public Timeline Read()
    {
        var events = new List<TimelineEvent>();
        var notRead = new List<UnreadSource>();
        foreach (var (channel, query) in new[] { ("Application", ApplicationQuery), ("System", SystemQuery) })
        {
            try
            {
                foreach (var xml in sources.Events(channel, query, MaxPerSource))
                {
                    try
                    {
                        if (FromEventXml(channel, xml) is { } item)
                        {
                            events.Add(item);
                        }
                    }
                    catch (XmlException ex)
                    {
                        notRead.Add(new UnreadSource($"An event in the {channel} log", ex.Message));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notRead.Add(new UnreadSource($"{channel} log", ex.Message));
            }
        }

        try
        {
            // Records that repeat an event read above (a crash, an install, an update) would show twice, so only the rest are kept.
            events.AddRange(sources.ReliabilityRecords(MaxPerSource).Where(r => !LogSources.Contains(r.SourceName)).Select(FromReliability));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notRead.Add(new UnreadSource("Reliability Monitor", ex.Message));
        }

        return new Timeline([.. events.OrderByDescending(e => e.TimeUtc)], notRead);
    }

    /// <summary>One event's XML as a timeline event; null for an event the timeline does not use.</summary>
    /// <exception cref="XmlException">The XML is not an event.</exception>
    public static TimelineEvent? FromEventXml(string channel, string xml)
    {
        var document = XDocument.Parse(xml);
        var system = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "System");
        var provider = system?.Elements().FirstOrDefault(e => e.Name.LocalName == "Provider")?.Attribute("Name")?.Value ?? "";
        var id = int.TryParse(system?.Elements().FirstOrDefault(e => e.Name.LocalName == "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : -1;
        var created = system?.Elements().FirstOrDefault(e => e.Name.LocalName == "TimeCreated")?.Attribute("SystemTime")?.Value;
        if (!DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
        {
            return null;
        }

        var record = system?.Elements().FirstOrDefault(e => e.Name.LocalName == "EventRecordID")?.Value;
        var data = document.Descendants().Where(e => e.Name.LocalName is "Data").Select(e => ((string?)e.Attribute("Name"), e.Value.Trim())).ToArray();
        string Named(string name, int position) =>
            data.FirstOrDefault(d => d.Item1 == name).Item2 ?? (position < data.Length && data[position].Item1 is null ? data[position].Item2 : "");

        (TimelineKind Kind, string Summary)? mapped = (provider, id) switch
        {
            ("Application Error", 1000) => (TimelineKind.AppCrash,
                $"{Or(Named("AppName", 0), "An app")} stopped working (faulting module {Or(Named("ModuleName", 3), "unknown")}, exception {Or(Named("ExceptionCode", 6), "unknown")})."),
            ("Application Hang", 1002) => (TimelineKind.AppHang, $"{Or(Named("AppName", 0), "An app")} stopped responding and was closed."),
            ("Windows Error Reporting", 1001) => (TimelineKind.ProblemReport,
                $"Windows recorded a problem report: {Or(Named("EventName", 2), "a problem")}{(Named("P1", 5) is { Length: > 0 } p1 ? $" for {p1}" : "")}."),
            ("Microsoft-Windows-Kernel-Power", 41) => (TimelineKind.UnexpectedShutdown,
                $"The PC restarted without shutting down cleanly{(StopCode(Named("BugcheckCode", 0)) is { } code ? $" (stop code {code})" : "")}."),
            ("EventLog", 6008) => (TimelineKind.UnexpectedShutdown, $"The previous shutdown at {Named("", 0)} {Named("", 1)} was unexpected.".Replace("  ", " ", StringComparison.Ordinal)),
            ("Service Control Manager", >= 7000 and <= 7034) => ServiceEvent(id, Named("param1", 0), Named("param2", 1)),
            ("Microsoft-Windows-WindowsUpdateClient", 19) => (TimelineKind.UpdateInstalled, $"Windows Update installed {Or(Named("updateTitle", 0), "an update")}."),
            ("Microsoft-Windows-WindowsUpdateClient", 20) => (TimelineKind.UpdateFailed,
                $"Windows Update could not install {Or(Named("updateTitle", 1), "an update")} (error {Or(Named("errorCode", 0), "unknown")})."),
            ("MsiInstaller", 1033 or 11707) => (TimelineKind.AppInstalled, $"Installed {Product(data, id)}."),
            ("MsiInstaller", 1034 or 11724) => (TimelineKind.AppRemoved, $"Removed {Product(data, id)}."),
            _ => null,
        };
        if (mapped is not { } m)
        {
            return null;
        }

        var evidence = new List<Evidence> { new CheckEvidence("event", $"{channel} log · {provider} · event {id}{(record is null ? "" : $" · record {record}")}") };
        if (Named("AppPath", 10) is { Length: > 2 } path && path[1] == ':')
        {
            evidence.Add(new PathEvidence(path, "The program that stopped."));
        }

        return new TimelineEvent(time.ToUniversalTime(), $"{channel} log", m.Kind, m.Summary, evidence);
    }

    private static TimelineEvent FromReliability(ReliabilityRecord record)
    {
        var message = record.Message?.Split('\n', 2)[0].Trim();
        var text = message is { Length: > 200 } ? message[..197] + "..." : message;
        return new TimelineEvent(
            record.TimeUtc,
            "Reliability Monitor",
            TimelineKind.ReliabilityRecord,
            $"{Or(record.ProductName ?? "", record.SourceName)}: {Or(text ?? "", $"event {record.EventId}")}",
            [new CheckEvidence("reliability record", $"Reliability Monitor · {record.SourceName} · event {record.EventId}")]);
    }

    private static (TimelineKind, string) ServiceEvent(int id, string name, string detail)
    {
        var service = Or(name, "A service");
        return id switch
        {
            7026 => (TimelineKind.DriverFailure, $"These drivers did not load at startup: {Or(name, "not named")}."),
            7000 => (TimelineKind.ServiceFailure, $"{service} did not start{(detail.Length > 0 ? $": {detail}" : ".")}"),
            7001 or 7003 => (TimelineKind.ServiceFailure, $"{service} did not start because a service it depends on did not."),
            7009 or 7011 or 7022 => (TimelineKind.ServiceFailure, $"{service} did not respond in time."),
            7023 or 7024 => (TimelineKind.ServiceFailure, $"{service} stopped with an error{(detail.Length > 0 ? $": {detail}" : ".")}"),
            7031 or 7034 => (TimelineKind.ServiceFailure, $"{service} stopped unexpectedly."),
            _ => (TimelineKind.ServiceFailure, $"{service} reported a problem (event {id})."),
        };
    }

    private static string Product((string? Name, string Value)[] data, int id)
    {
        if (id is 11707 or 11724)
        {
            var text = data.FirstOrDefault().Value ?? "";
            var start = text.IndexOf("Product:", StringComparison.OrdinalIgnoreCase);
            var end = text.IndexOf("--", StringComparison.Ordinal);
            return start >= 0 && end > start ? text[(start + 8)..end].Trim() : Or(text, "an app");
        }

        var name = data.Length > 0 ? data[0].Value : "";
        var version = data.Length > 1 ? data[1].Value : "";
        return Or($"{name} {version}".Trim(), "an app");
    }

    private static string? StopCode(string bugcheck) =>
        long.TryParse(bugcheck, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) && code != 0 ? $"0x{code:X}" : null;

    private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
}

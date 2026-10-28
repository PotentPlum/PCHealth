using PCHealth.Core;

namespace PCHealth.Maintenance.Tests;

/// <summary>Timeline events (T-60) parsed from recorded event XML, and sources that can't be read.</summary>
public sealed class TimelineTests
{
    [Theory]
    [InlineData("app-crash.xml", "Application", TimelineKind.AppCrash, "photoeditor.exe stopped working (faulting module gpudriver64.dll, exception c0000005).")]
    [InlineData("app-hang.xml", "Application", TimelineKind.AppHang, "notes.exe stopped responding and was closed.")]
    [InlineData("kernel-power.xml", "System", TimelineKind.UnexpectedShutdown, "The PC restarted without shutting down cleanly (stop code 0xD1).")]
    [InlineData("unexpected-shutdown.xml", "System", TimelineKind.UnexpectedShutdown, "The previous shutdown at 22:41:07 24/10/2026 was unexpected.")]
    [InlineData("service-failed.xml", "System", TimelineKind.ServiceFailure, "Vendor Update Service did not start: %%2")]
    [InlineData("driver-failed.xml", "System", TimelineKind.DriverFailure, "These drivers did not load at startup: oldvpn.")]
    [InlineData("update-installed.xml", "System", TimelineKind.UpdateInstalled, "Windows Update installed 2026-10 Cumulative Update for Windows 11 (KB5099999).")]
    [InlineData("update-failed.xml", "System", TimelineKind.UpdateFailed, "Windows Update could not install Security Intelligence Update (KB2267602) (error 0x80070643).")]
    [InlineData("msi-installed.xml", "Application", TimelineKind.AppInstalled, "Installed Old VPN Client 3.1.0.")]
    [InlineData("msi-removed.xml", "Application", TimelineKind.AppRemoved, "Removed Trial Suite.")]
    [InlineData("service-installed.xml", "System", TimelineKind.StartupChange, "A service was installed: Old VPN Service (\"C:\\Program Files\\Old VPN\\vpnsvc.exe\"), start type auto start.")]
    public void Recorded_events_become_timeline_events(string file, string channel, TimelineKind kind, string summary)
    {
        var item = TimelineReader.FromEventXml(channel, Fixture(file))!;

        Assert.Equal(kind, item.Kind);
        Assert.Equal(summary, item.Summary);
        Assert.Equal($"{channel} log", item.Source);
        Assert.Contains(item.Evidence, e => e is CheckEvidence c && c.Description.StartsWith($"{channel} log · ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_crash_keeps_its_time_record_and_program_path_as_evidence()
    {
        var crash = TimelineReader.FromEventXml("Application", Fixture("app-crash.xml"))!;

        Assert.Equal(new DateTimeOffset(2026, 10, 27, 8, 15, 2, TimeSpan.Zero), crash.TimeUtc);
        Assert.Equal("Application log · Application Error · event 1000 · record 48211", crash.Evidence[0].Description);
        Assert.Equal(@"C:\Program Files\PhotoEditor\photoeditor.exe", Assert.IsType<PathEvidence>(crash.Evidence[1]).Path);
    }

    [Fact]
    public void Events_the_timeline_does_not_use_are_left_out()
    {
        Assert.Null(TimelineReader.FromEventXml("System", Fixture("other.xml")));
    }

    [Fact]
    public void Every_source_is_read_newest_first_and_unreadable_ones_are_listed()
    {
        var sources = new FakeSources
        {
            Application = [Fixture("msi-installed.xml"), Fixture("app-crash.xml"), "<broken"],
            SystemDenied = true,
            Reliability =
            [
                new ReliabilityRecord(new DateTimeOffset(2026, 10, 28, 6, 0, 0, TimeSpan.Zero), "Microsoft-Windows-Defrag", 258, "Defragmenter", "The storage optimizer ran.\nMore text."),
                new ReliabilityRecord(new DateTimeOffset(2026, 10, 27, 8, 15, 3, TimeSpan.Zero), "Application Error", 1000, "photoeditor.exe", "Duplicate of the log event."),
            ],
        };

        var timeline = new TimelineReader(sources).Read();

        Assert.Equal(
            [TimelineKind.ReliabilityRecord, TimelineKind.AppCrash, TimelineKind.AppInstalled],
            timeline.Events.Select(e => e.Kind));
        Assert.Equal("Defragmenter: The storage optimizer ran.", timeline.Events[0].Summary);
        Assert.False(timeline.IsComplete);
        Assert.Contains(timeline.NotRead, n => n.Source == "System log" && n.Reason == "Reading System needs administrator rights.");
        Assert.Contains(timeline.NotRead, n => n.Source == "An event in the Application log");
        Assert.Equal(TimelineReader.ApplicationQuery, sources.Queries["Application"]);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Events", name));

    private sealed class FakeSources : ITimelineSources
    {
        public IReadOnlyList<string> Application { get; init; } = [];

        public bool SystemDenied { get; init; }

        public IReadOnlyList<ReliabilityRecord> Reliability { get; init; } = [];

        public Dictionary<string, string> Queries { get; } = [];

        public IReadOnlyList<string> Events(string channel, string query, int max)
        {
            Queries[channel] = query;
            return channel == "System" && SystemDenied ? throw new UnauthorizedAccessException("Reading System needs administrator rights.") : Application;
        }

        public IReadOnlyList<ReliabilityRecord> ReliabilityRecords(int max) => Reliability;
    }
}

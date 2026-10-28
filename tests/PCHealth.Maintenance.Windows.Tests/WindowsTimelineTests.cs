namespace PCHealth.Maintenance.Windows.Tests;

/// <summary>The runner's real event logs and Reliability Monitor, read-only (T-60).</summary>
public sealed class WindowsTimelineTests
{
    [Fact]
    public void The_runner_s_logs_are_read_into_a_timeline_or_listed_as_not_read()
    {
        var timeline = new TimelineReader(new WindowsTimelineSources()).Read();

        Assert.True(timeline.Events.Count > 0 || timeline.NotRead.Count > 0);
        Assert.Equal(timeline.Events.OrderByDescending(e => e.TimeUtc), timeline.Events);
        Assert.All(timeline.Events, e => Assert.False(string.IsNullOrWhiteSpace(e.Summary)));
        Assert.All(timeline.NotRead, n => Assert.False(string.IsNullOrWhiteSpace(n.Reason)));
    }

    [Fact]
    public void A_service_start_event_can_be_queried_from_the_system_log()
    {
        var events = WindowsEventLog.Query("System", "*[System[Provider[@Name='Service Control Manager']]]", 5);

        Assert.All(events, xml => Assert.Contains("Service Control Manager", xml, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("20261027075812.123456+060", "2026-10-27T06:58:12.123456Z")]
    [InlineData("20261027075812.000000-000", "2026-10-27T07:58:12Z")]
    [InlineData("nonsense", null)]
    public void Wmi_dates_are_read_with_their_offset(string cim, string? expected)
    {
        Assert.Equal(expected is null ? null : DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), WindowsTimelineSources.Time(cim));
    }
}

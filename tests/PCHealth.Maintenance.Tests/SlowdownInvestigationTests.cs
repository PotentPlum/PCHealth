using PCHealth.Core;

namespace PCHealth.Maintenance.Tests;

/// <summary>"When did it start?" (T-61): the window around the chosen date and the wording rules.</summary>
public sealed class SlowdownInvestigationTests
{
    private static readonly DateTimeOffset Chosen = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_events_in_the_window_are_shown_and_changes_before_the_date_are_highlighted()
    {
        var timeline = new Timeline(
        [
            Event(Chosen.AddDays(2), TimelineKind.UpdateInstalled, "after the window"),
            Event(Chosen.AddHours(6), TimelineKind.AppCrash, "a crash after the date"),
            Event(Chosen.AddHours(-3), TimelineKind.DriverFailure, "These drivers did not load at startup: oldvpn."),
            Event(Chosen.AddDays(-2), TimelineKind.AppInstalled, "Installed Old VPN Client 3.1.0."),
            Event(Chosen.AddDays(-3), TimelineKind.AppCrash, "a crash before the date"),
            Event(Chosen.AddDays(-6.5), TimelineKind.StartupChange, "A service was installed: Old VPN Service."),
            Event(Chosen.AddDays(-8), TimelineKind.UpdateInstalled, "before the window"),
        ],
        []);

        var result = SlowdownInvestigation.Around(timeline, Chosen);

        Assert.Equal(Chosen.AddDays(-7), result.From);
        Assert.Equal(Chosen.AddDays(1), result.To);
        Assert.Equal(5, result.Events.Count);
        Assert.DoesNotContain(result.Events, e => e.Summary is "after the window" or "before the window");
        Assert.Equal([TimelineKind.DriverFailure, TimelineKind.AppInstalled, TimelineKind.StartupChange], result.Explanations.Select(e => e.Event.Kind));
        Assert.StartsWith("Possibly related (3 hours before): These drivers did not load", result.Explanations[0].Text);
        Assert.Contains("it can be harmless", result.Explanations[0].Text);
        Assert.StartsWith("Possibly related (2 days before): Installed Old VPN Client", result.Explanations[1].Text);
        Assert.StartsWith("3 changes in the 7 days before", result.Summary);
        Assert.Contains("not proven causes", result.Summary);
    }

    [Fact]
    public void No_text_claims_a_cause_or_calls_a_driver_defective()
    {
        var timeline = new Timeline(
            [.. Enum.GetValues<TimelineKind>().Select((k, i) => Event(Chosen.AddHours(-1 - i), k, $"Something of kind {k}."))],
            [new UnreadSource("System log", "denied")]);

        var result = SlowdownInvestigation.Around(timeline, Chosen);
        var texts = result.Explanations.Select(e => e.Text).Append(result.Summary).ToArray();

        foreach (var text in texts)
        {
            foreach (var word in SlowdownInvestigation.Forbidden)
            {
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.All(result.Explanations, e => Assert.StartsWith("Possibly related", e.Text));
        Assert.EndsWith("Some logs could not be read, so this may be incomplete.", result.Summary);
    }

    [Fact]
    public void A_quiet_week_says_so_without_guessing()
    {
        var result = SlowdownInvestigation.Around(new Timeline([Event(Chosen.AddDays(-1), TimelineKind.AppCrash, "a crash")], []), Chosen);

        Assert.Empty(result.Explanations);
        Assert.StartsWith("Nothing was installed, updated or set to start in the 7 days before", result.Summary);
    }

    private static TimelineEvent Event(DateTimeOffset time, TimelineKind kind, string summary) =>
        new(time, "System log", kind, summary, [new CheckEvidence("event", "test")]);
}

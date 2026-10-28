using System.Globalization;

namespace PCHealth.Maintenance;

/// <summary>A change shortly before the chosen date, worded as something to look at, never as the cause.</summary>
public sealed record PossibleExplanation(TimelineEvent Event, string Text);

/// <param name="From">The start of the window: <see cref="SlowdownInvestigation.LookBack"/> before the chosen date.</param>
/// <param name="To">The end of the window: <see cref="SlowdownInvestigation.LookAhead"/> after the chosen date.</param>
/// <param name="Events">Everything in the window, newest first.</param>
/// <param name="Explanations">Updates, installs, driver events and startup changes before the chosen date, closest first.</param>
public sealed record Investigation(
    DateTimeOffset Chosen,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<TimelineEvent> Events,
    IReadOnlyList<PossibleExplanation> Explanations,
    string Summary);

/// <summary>
/// "When did it start?" (T-61, spec §5.4): the timeline around a date the user chose, with the changes that came shortly
/// before it highlighted. Correlation is not cause: every text says "possibly", and a driver is never called defective
/// for being old, unsigned or failing to load. Nothing here changes or downloads anything.
/// </summary>
public static class SlowdownInvestigation
{
    public static readonly TimeSpan LookBack = TimeSpan.FromDays(7);
    public static readonly TimeSpan LookAhead = TimeSpan.FromDays(1);

    /// <summary>The kinds of change that can come before a slowdown: what was installed, updated or set to start.</summary>
    public static readonly IReadOnlySet<TimelineKind> Changes = new HashSet<TimelineKind>
    {
        TimelineKind.UpdateInstalled, TimelineKind.UpdateFailed, TimelineKind.AppInstalled, TimelineKind.AppRemoved,
        TimelineKind.DriverFailure, TimelineKind.StartupChange,
    };

    /// <summary>Words that would claim more than a timeline can show; tests keep them out of every text.</summary>
    public static readonly IReadOnlyList<string> Forbidden = ["caused", "the cause", "root cause", "defective", "faulty", "bad driver", "broken driver", "culprit"];

    public static Investigation Around(Timeline timeline, DateTimeOffset chosen)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var from = chosen - LookBack;
        var to = chosen + LookAhead;
        var events = timeline.Events.Where(e => e.TimeUtc >= from && e.TimeUtc <= to).OrderByDescending(e => e.TimeUtc).ToArray();
        var explanations = events
            .Where(e => Changes.Contains(e.Kind) && e.TimeUtc <= chosen)
            .OrderByDescending(e => e.TimeUtc)
            .Select(e => new PossibleExplanation(e, Explain(e, chosen - e.TimeUtc)))
            .ToArray();
        var summary = explanations.Length == 0
            ? $"Nothing was installed, updated or set to start in the {LookBack.TotalDays:0} days before {Day(chosen)}, as far as the logs show. The slowdown may have another reason, such as a change in what you use the PC for."
            : $"{explanations.Length} {(explanations.Length == 1 ? "change" : "changes")} in the {LookBack.TotalDays:0} days before {Day(chosen)} may be related. "
                + "They are possible explanations to look at, not proven causes: something that happened just before can still be unrelated.";
        return new Investigation(chosen, from, to, events, explanations, summary + (timeline.IsComplete ? "" : " Some logs could not be read, so this may be incomplete."));
    }

    private static string Explain(TimelineEvent e, TimeSpan before)
    {
        var when = before.TotalHours < 24 ? $"{Math.Max(1, (int)before.TotalHours)} hours before" : $"{(int)before.TotalDays} days before";
        var what = e.Kind switch
        {
            TimelineKind.DriverFailure =>
                "A driver did not load. This can matter, or it can be harmless, for example for hardware that is not connected.",
            TimelineKind.UpdateFailed => "An update did not install, so Windows may try it again.",
            TimelineKind.StartupChange => "Something new starts with Windows since then; the Maintenance page shows what it uses.",
            TimelineKind.AppRemoved => "An app was removed around then.",
            _ => "This changed what is on the PC around then.",
        };
        return $"Possibly related ({when}): {e.Summary} {what}";
    }

    private static string Day(DateTimeOffset date) => date.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
}

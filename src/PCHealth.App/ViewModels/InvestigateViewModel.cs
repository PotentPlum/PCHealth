using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Maintenance;

namespace PCHealth.App.ViewModels;

/// <summary>One event in the window, with its time, where it was read and its evidence.</summary>
public sealed class TimelineEventViewModel(TimelineEvent item, string? explanation)
{
    public TimelineEvent Item { get; } = item;

    public string When => Item.TimeUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string Summary => Item.Summary;

    public string Source => $"{Item.Source} · {string.Join(" · ", Item.Evidence.Select(e => e.Description))}";

    /// <summary>For a change shortly before the chosen date: why it is shown; null otherwise.</summary>
    public string? Explanation { get; } = explanation;

    public bool IsHighlighted => Explanation is not null;
}

/// <summary>
/// "When did it start?" (T-61, spec §5.4): the user picks the day a slowdown started; the page shows the timeline
/// around it and highlights updates, installs, driver events and startup changes shortly before, as possible
/// explanations only. It reads the logs and changes or downloads nothing.
/// </summary>
public sealed class InvestigateViewModel : ObservableObject
{
    private readonly ITimelineSources sources;
    private readonly TimeProvider clock;
    private Timeline? timeline;
    private DateTime chosenDate;
    private string summary = "Choose the day the slowdown started, then look at what happened around it. Nothing is changed or downloaded.";
    private bool busy;

    public InvestigateViewModel(ITimelineSources sources, TimeProvider clock)
    {
        this.sources = sources;
        this.clock = clock;
        chosenDate = clock.GetLocalNow().Date;
        ShowCommand = new AsyncRelayCommand(ShowAsync, () => !busy);
    }

    public string Title => "When did it start?";

    /// <summary>The day the user says the slowdown started, in local time.</summary>
    public DateTime ChosenDate
    {
        get => chosenDate;
        set
        {
            if (SetProperty(ref chosenDate, value.Date) && timeline is not null)
            {
                Show();
            }
        }
    }

    public string Explanation =>
        $"The timeline covers {SlowdownInvestigation.LookBack.TotalDays:0} days before the day you choose and {SlowdownInvestigation.LookAhead.TotalDays:0} day after. "
        + "Changes just before it are highlighted as possible explanations. Something that happened just before a slowdown is not proof that it caused it, "
        + "and an old, unsigned or missing driver is not a sign that it is broken. Nothing is changed or downloaded.";

    public string Summary
    {
        get => summary;
        private set => SetProperty(ref summary, value);
    }

    public ObservableCollection<TimelineEventViewModel> Events { get; } = [];

    public ObservableCollection<string> NotRead { get; } = [];

    public IAsyncRelayCommand ShowCommand { get; }

    private async Task ShowAsync()
    {
        busy = true;
        ShowCommand.NotifyCanExecuteChanged();
        Summary = "Reading the event logs. Nothing is changed.";
        try
        {
            timeline = await Task.Run(() => new TimelineReader(sources).Read());
            NotRead.Clear();
            foreach (var source in timeline.NotRead)
            {
                NotRead.Add($"{source.Source}: {source.Reason}");
            }

            Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Summary = $"The logs could not be read: {ex.Message}";
        }
        finally
        {
            busy = false;
            ShowCommand.NotifyCanExecuteChanged();
        }
    }

    private void Show()
    {
        var local = DateTime.SpecifyKind(ChosenDate, DateTimeKind.Unspecified);
        var chosen = new DateTimeOffset(local, clock.LocalTimeZone.GetUtcOffset(local)).AddHours(12);
        var result = SlowdownInvestigation.Around(timeline!, chosen);
        var explained = result.Explanations.ToDictionary<PossibleExplanation, TimelineEvent, string>(e => e.Event, e => e.Text, ReferenceEqualityComparer.Instance);
        Events.Clear();
        foreach (var item in result.Events)
        {
            Events.Add(new TimelineEventViewModel(item, explained.GetValueOrDefault(item)));
        }

        Summary = result.Summary;
    }
}

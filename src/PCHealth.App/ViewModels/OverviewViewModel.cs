using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Policy.Recommendations;

namespace PCHealth.App.ViewModels;

/// <summary>
/// The home page (spec §3.1, §7): a few prioritized findings rather than a wall of metrics. Actions come first,
/// largest estimated benefit first; the rest are counted, not listed.
/// </summary>
public sealed class OverviewViewModel : PageViewModel
{
    public const int Shown = 3;

    private readonly FindingsStore findings;
    private string summary = "No discovery has run yet.";

    public OverviewViewModel(FindingsStore findings)
        : base("Overview")
    {
        this.findings = findings;
        PreviewCommand = new RelayCommand<RecommendationItemViewModel>(item => PreviewRequested?.Invoke(this, item!.Recommendation), item => item?.IsAction == true);
        findings.Updated += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Raised when the user asks to preview an action before approving it.</summary>
    public event EventHandler<Recommendation>? PreviewRequested;

    public ObservableCollection<RecommendationItemViewModel> Items { get; } = [];

    public string Summary
    {
        get => summary;
        private set => SetProperty(ref summary, value);
    }

    public IRelayCommand<RecommendationItemViewModel> PreviewCommand { get; }

    /// <summary>Recommendations the user declined are left out unless their evidence changed (T-31).</summary>
    public Func<Recommendation, bool> IsHidden { get; set; } = _ => false;

    public void Refresh()
    {
        Items.Clear();
        if (findings.Latest is not { } latest)
        {
            Summary = "No discovery has run yet.";
            return;
        }

        var visible = latest.Recommendations.Where(r => !IsHidden(r)).ToArray();
        foreach (var recommendation in visible.Take(Shown))
        {
            Items.Add(new RecommendationItemViewModel(recommendation));
        }

        var actions = visible.Count(r => r.Disposition == Disposition.Act);
        var reviews = visible.Length - actions;
        Summary = visible.Length == 0
            ? "Nothing needs your attention right now."
            : $"{actions} suggested {(actions == 1 ? "action" : "actions")} and {reviews} {(reviews == 1 ? "item" : "items")} to review. The most useful are shown first.";
    }
}

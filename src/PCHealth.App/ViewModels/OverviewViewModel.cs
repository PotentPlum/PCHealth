using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Policy.Recommendations;

namespace PCHealth.App.ViewModels;

/// <summary>
/// The home page (spec §3.1, §7): a few prioritized findings rather than a wall of metrics. Actions come first,
/// largest estimated benefit first; the rest are counted, and listed on request. Each suggested action can be cleaned
/// up on its own, or all of them together through one combined preview (T-67).
/// </summary>
public sealed class OverviewViewModel : PageViewModel
{
    public const int Shown = 3;

    private readonly FindingsStore findings;
    private string summary = "No discovery has run yet.";
    private bool showAll;
    private Recommendation[] actions = [];
    private int needChoice;

    public OverviewViewModel(FindingsStore findings, DeclinedRecommendations? declines = null)
        : base("Overview")
    {
        this.findings = findings;
        if (declines is not null)
        {
            IsHidden = declines.IsDeclined;
        }

        PreviewCommand = new RelayCommand<RecommendationItemViewModel>(item => PreviewRequested?.Invoke(this, item!.Recommendation), item => item?.IsAction == true);
        CleanUpAllCommand = new RelayCommand(() => CleanUpAllRequested?.Invoke(this, actions), () => actions.Length > 0);
        ShowAllCommand = new RelayCommand(() =>
        {
            showAll = !showAll;
            Refresh();
        });
        findings.Updated += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Raised when the user asks to preview an action before approving it.</summary>
    public event EventHandler<Recommendation>? PreviewRequested;

    /// <summary>Raised when the user asks to preview every suggested action together.</summary>
    public event EventHandler<IReadOnlyList<Recommendation>>? CleanUpAllRequested;

    public ObservableCollection<RecommendationItemViewModel> Items { get; } = [];

    public string Summary
    {
        get => summary;
        private set => SetProperty(ref summary, value);
    }

    public IRelayCommand<RecommendationItemViewModel> PreviewCommand { get; }

    public IRelayCommand CleanUpAllCommand { get; }

    public IRelayCommand ShowAllCommand { get; }

    public string CleanUpAllText => actions.Length == 0
        ? "Clean up all suggested"
        : $"Clean up all {actions.Length} suggested (about {SizeText.Bytes(actions.Sum(r => r.Operations.Sum(o => o.ExpectedSize.Bytes)))})…";

    /// <summary>Why some items are not part of "Clean up all": they need the user to choose, one by one.</summary>
    public string? NeedChoiceText => needChoice == 0
        ? null
        : $"{needChoice} {(needChoice == 1 ? "item needs" : "items need")} your choice and {(needChoice == 1 ? "is" : "are")} not part of \"Clean up all\". Open the details to review {(needChoice == 1 ? "it" : "them")}.";

    /// <summary>The overview lists only the first few unless the user asks for all.</summary>
    public string ShowAllText { get; private set; } = "";

    public bool CanShowAll { get; private set; }

    /// <summary>Recommendations the user declined are left out unless their evidence changed (T-31).</summary>
    public Func<Recommendation, bool> IsHidden { get; set; } = _ => false;

    public void Refresh()
    {
        Items.Clear();
        var visible = findings.Latest is { } latest ? latest.Recommendations.Where(r => !IsHidden(r)).ToArray() : [];
        actions = visible.Where(r => r.Disposition == Disposition.Act).ToArray();
        needChoice = visible.Length - actions.Length;
        CanShowAll = visible.Length > Shown;
        ShowAllText = showAll ? "Show fewer" : $"Show all {visible.Length}";
        foreach (var recommendation in showAll ? visible : visible.Take(Shown))
        {
            Items.Add(new RecommendationItemViewModel(recommendation));
        }

        Summary = findings.Latest is null
            ? "No discovery has run yet."
            : visible.Length == 0
                ? "Nothing needs your attention right now."
                : $"{actions.Length} suggested {(actions.Length == 1 ? "action" : "actions")} and {needChoice} {(needChoice == 1 ? "item" : "items")} to review. The most useful are shown first.";
        OnPropertyChanged(nameof(CleanUpAllText));
        OnPropertyChanged(nameof(NeedChoiceText));
        OnPropertyChanged(nameof(ShowAllText));
        OnPropertyChanged(nameof(CanShowAll));
        CleanUpAllCommand.NotifyCanExecuteChanged();
    }
}

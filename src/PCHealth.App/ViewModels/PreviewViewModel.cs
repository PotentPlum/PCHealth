using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Core;
using PCHealth.Execution;
using PCHealth.Policy.Recommendations;

namespace PCHealth.App.ViewModels;

/// <summary>One exact item the user can keep or leave out.</summary>
public sealed class PreviewItemViewModel(ProposedOperation operation) : ObservableObject
{
    private bool isSelected = true;

    public ProposedOperation Operation { get; } = operation;

    public string Path => Operation.Path;

    public string Size => SizeText.Measured(Operation.ExpectedSize);

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }
}

/// <summary>
/// Preview before approval (spec §4, §6.5): the exact items, what is left alone and why, the expected effect, and
/// the recovery and retention terms. The user can leave items out, approve, decline, or go back. Approval runs only
/// the selected items, through the execution gateway.
/// </summary>
public sealed class PreviewViewModel : PageViewModel
{
    private readonly ExecutionGateway gateway;
    private readonly DeclinedRecommendations declines;
    private readonly AppPaths paths;
    private readonly TimeProvider clock;
    private string? result;
    private bool decided;

    public PreviewViewModel(Recommendation recommendation, ExecutionGateway gateway, DeclinedRecommendations declines, AppPaths paths, TimeProvider clock)
        : base("Preview")
    {
        Recommendation = recommendation;
        this.gateway = gateway;
        this.declines = declines;
        this.paths = paths;
        this.clock = clock;
        Items = new ObservableCollection<PreviewItemViewModel>(recommendation.Operations.Select(o => new PreviewItemViewModel(o)));
        ApproveCommand = new RelayCommand(Approve, () => !decided && Items.Any(i => i.IsSelected));
        DeclineCommand = new RelayCommand(Decline, () => !decided);
        BackCommand = new RelayCommand(() => Finished?.Invoke(this, EventArgs.Empty));
        foreach (var item in Items)
        {
            item.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(ExpectedEffect));
                ApproveCommand.NotifyCanExecuteChanged();
            };
        }
    }

    /// <summary>Raised when the user is done here: after approving, declining or going back.</summary>
    public event EventHandler? Finished;

    public Recommendation Recommendation { get; }

    public string WhatWeFound => Recommendation.WhatWeFound;

    /// <summary>"Close X first." for actions on an app's own data (T-46); approval is refused while it runs.</summary>
    public string? CloseFirst => Recommendation.CloseFirst;

    public bool HasCloseFirst => CloseFirst is not null;

    public ObservableCollection<PreviewItemViewModel> Items { get; }

    /// <summary>Items taken out by protections or the user's exclusions, with the reason.</summary>
    public IReadOnlyList<string> LeftAlone => Recommendation.Blocked.Select(b => $"{b.Operation.Path}: {b.Reason}").ToArray();

    public string ExpectedEffect
    {
        get
        {
            var selected = Items.Where(i => i.IsSelected).ToArray();
            var bytes = selected.Sum(i => i.Operation.ExpectedSize.Bytes);
            return $"{selected.Length} of {Items.Count} items selected: about {SizeText.Bytes(bytes)} (estimated from sizes measured during the scan). "
                + Recommendation.Impact.WhatChanges;
        }
    }

    public string Afterward => Recommendation.WhatCouldHappenAfterward;

    public string RecoveryTerms => Recommendation.CanIRecover;

    public string? Result
    {
        get => result;
        private set => SetProperty(ref result, value);
    }

    public IRelayCommand ApproveCommand { get; }

    public IRelayCommand DeclineCommand { get; }

    public IRelayCommand BackCommand { get; }

    public ExecutionResult? Execution { get; private set; }

    private void Approve()
    {
        Execution = gateway.Execute(Recommendation, Items.Where(i => i.IsSelected).Select(i => i.Operation).ToArray());
        var leftOut = Execution.Items.Where(i => i.Status != Ledger.ItemStatus.Done).Select(i => $" {i.Operation.Path}: {i.Reason}");
        Result = Execution.Summary + string.Concat(leftOut);
        Decide();
    }

    private void Decline()
    {
        declines.Decline(Recommendation, clock.GetUtcNow());
        declines.Save(paths.Declines);
        Result = "Declined. It will not be suggested again unless something about it changes materially.";
        Decide();
    }

    private void Decide()
    {
        decided = true;
        ApproveCommand.NotifyCanExecuteChanged();
        DeclineCommand.NotifyCanExecuteChanged();
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Execution;
using PCHealth.Policy.Recommendations;

namespace PCHealth.App.ViewModels;

/// <summary>One suggested action in the combined preview: its exact items and terms, and whether to include it.</summary>
public sealed class BatchPreviewEntryViewModel(Recommendation recommendation) : ObservableObject
{
    private bool isSelected = true;
    private string? result;

    public Recommendation Recommendation { get; } = recommendation;

    public string WhatWeFound => Recommendation.WhatWeFound;

    public long Bytes => Recommendation.Operations.Sum(o => o.ExpectedSize.Bytes);

    public string Summary =>
        $"{Recommendation.Operations.Count} {(Recommendation.Operations.Count == 1 ? "item" : "items")} · about {SizeText.Bytes(Bytes)}";

    /// <summary>Exactly what would change, path by path.</summary>
    public IReadOnlyList<string> Paths => Recommendation.Operations.Select(o => $"{o.Path} · {SizeText.Measured(o.ExpectedSize)}").ToArray();

    public IReadOnlyList<string> LeftAlone => Recommendation.Blocked.Select(b => $"Left alone: {b.Operation.Path}: {b.Reason}").ToArray();

    public string Afterward => Recommendation.WhatCouldHappenAfterward;

    public string RecoveryTerms => Recommendation.CanIRecover;

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    public string? Result
    {
        get => result;
        set => SetProperty(ref result, value);
    }

    public ExecutionResult? Execution { get; set; }
}

/// <summary>
/// "Clean up all suggested" (T-67): every suggested action in one preview, with its exact items, effect and recovery
/// terms, and one approval. Each selected action still goes through the execution gateway on its own, as its own
/// ledger transaction, so each can be undone separately and one refusal doesn't stop the others. Items that need the
/// user's choice (duplicates, versions, uncertain leftovers) are never part of it.
/// </summary>
public sealed class BatchPreviewViewModel : PageViewModel
{
    private readonly ExecutionGateway gateway;
    private string? result;
    private bool decided;

    public BatchPreviewViewModel(IEnumerable<Recommendation> recommendations, ExecutionGateway gateway)
        : base("Clean up all")
    {
        ArgumentNullException.ThrowIfNull(recommendations);
        this.gateway = gateway;
        Entries = new ObservableCollection<BatchPreviewEntryViewModel>(
            recommendations.Where(r => r.Disposition == Disposition.Act).Select(r => new BatchPreviewEntryViewModel(r)));
        ApproveCommand = new RelayCommand(Approve, () => !decided && Entries.Any(e => e.IsSelected));
        BackCommand = new RelayCommand(() => Finished?.Invoke(this, EventArgs.Empty));
        foreach (var entry in Entries)
        {
            entry.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BatchPreviewEntryViewModel.IsSelected))
                {
                    OnPropertyChanged(nameof(ExpectedEffect));
                    ApproveCommand.NotifyCanExecuteChanged();
                }
            };
        }
    }

    /// <summary>Raised when the user is done here: after approving or going back.</summary>
    public event EventHandler? Finished;

    public ObservableCollection<BatchPreviewEntryViewModel> Entries { get; }

    public string ExpectedEffect
    {
        get
        {
            var selected = Entries.Where(e => e.IsSelected).ToArray();
            var items = selected.Sum(e => e.Recommendation.Operations.Count);
            return $"{selected.Length} of {Entries.Count} suggested actions selected, {items} {(items == 1 ? "item" : "items")}: "
                + $"about {SizeText.Bytes(selected.Sum(e => e.Bytes))} (estimated from sizes measured during the scan). Each action is recorded and can be undone on its own from Activity.";
        }
    }

    public string? Result
    {
        get => result;
        private set => SetProperty(ref result, value);
    }

    public IRelayCommand ApproveCommand { get; }

    public IRelayCommand BackCommand { get; }

    /// <summary>The actions that were attempted, with what the gateway did.</summary>
    public IEnumerable<BatchPreviewEntryViewModel> Executed => Entries.Where(e => e.Execution is not null);

    private void Approve()
    {
        decided = true;
        ApproveCommand.NotifyCanExecuteChanged();
        foreach (var entry in Entries.Where(e => e.IsSelected))
        {
            entry.Execution = gateway.Execute(entry.Recommendation, entry.Recommendation.Operations);
            var leftOut = entry.Execution.Items.Where(i => i.Status != Ledger.ItemStatus.Done).Select(i => $" {i.Operation.Path}: {i.Reason}");
            entry.Result = entry.Execution.Summary + string.Concat(leftOut);
        }

        var completed = Executed.Count(e => e.Execution!.Status == ExecutionStatus.Completed);
        var attempted = Executed.Count();
        Result = completed == attempted
            ? $"Done: all {attempted} actions completed. See Activity to undo any of them."
            : $"{completed} of {attempted} actions completed fully; the result of each is shown above. See Activity to undo any of them.";
    }
}

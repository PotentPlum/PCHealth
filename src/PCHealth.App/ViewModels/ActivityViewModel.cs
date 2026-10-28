using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Core;
using PCHealth.Execution;
using PCHealth.Ledger;

namespace PCHealth.App.ViewModels;

/// <summary>One recorded change, readable: what changed, why, what happened, and whether and until when it can be undone.</summary>
public sealed class ActivityEntryViewModel
{
    public ActivityEntryViewModel(LedgerTransaction transaction, RestoreSupport support, Action<ActivityEntryViewModel> undo)
    {
        Transaction = transaction;
        Support = support;
        UndoCommand = new RelayCommand(() => undo(this), () => support.CanRestore);
    }

    public LedgerTransaction Transaction { get; }

    public RestoreSupport Support { get; }

    public string When => Transaction.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string WhatChanged
    {
        get
        {
            var count = Transaction.Items.Count;
            var items = count == 1 ? "1 item" : $"{count} items";
            return Transaction.Operation switch
            {
                LedgerOperation.MoveToQuarantine => $"Moved {items} to quarantine",
                LedgerOperation.MoveToRecycleBin => $"Moved {items} to the Recycle Bin",
                LedgerOperation.MoveToFolder => $"Moved {items} to another folder",
                LedgerOperation.PurgeQuarantine => $"Deleted {items} from quarantine permanently",
                _ => $"Put {items} back",
            };
        }
    }

    public string Why => Transaction.Reason;

    public string Outcome => Transaction.Outcome ?? Transaction.State switch
    {
        TransactionState.Pending => "Not finished; this is resolved the next time the app starts.",
        TransactionState.Applied => "Changed, not yet checked.",
        TransactionState.Verified => "Changed and checked.",
        _ => "Failed; see the items.",
    };

    public IReadOnlyList<string> Items => Transaction.Items
        .Select(i => $"{i.OriginalPath}: {Status(i.Status)}{(i.Error is null ? "" : $" ({i.Error})")}")
        .ToArray();

    public bool CanUndo => Support.CanRestore;

    /// <summary>The honest limits, shown whether or not Undo is offered.</summary>
    public string UndoTerms => Support.Limits;

    public string? Deadline => Transaction.RetentionDeadlineUtc is { } deadline
        ? $"Kept in quarantine until {deadline.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}; then deleted permanently."
        : null;

    public IRelayCommand UndoCommand { get; }

    private static string Status(ItemStatus status) => status switch
    {
        ItemStatus.Done => "done",
        ItemStatus.Failed => "failed",
        ItemStatus.Skipped => "left alone",
        _ => "not finished",
    };
}

/// <summary>
/// The activity history (spec §3.6, §6.4): every recorded change, newest first, with Undo where the operation type
/// supports it. Undo runs through the execution gateway, so it respects the global Pause. Pausing never hides history.
/// </summary>
public sealed class ActivityViewModel : PageViewModel
{
    private readonly TransactionLedger ledger;
    private readonly Restorer restorer;
    private readonly ExecutionGateway gateway;
    private string? message;

    public ActivityViewModel(TransactionLedger ledger, Restorer restorer, ExecutionGateway gateway)
        : base("Activity")
    {
        this.ledger = ledger;
        this.restorer = restorer;
        this.gateway = gateway;
        Refresh();
    }

    public ObservableCollection<ActivityEntryViewModel> Entries { get; } = [];

    public string? Message
    {
        get => message;
        private set => SetProperty(ref message, value);
    }

    public string Empty => Entries.Count == 0 ? "Nothing has been changed yet." : "";

    public void Refresh()
    {
        Entries.Clear();
        foreach (var transaction in ledger.All())
        {
            Entries.Add(new ActivityEntryViewModel(transaction, restorer.SupportFor(transaction), Undo));
        }

        OnPropertyChanged(nameof(Empty));
    }

    private void Undo(ActivityEntryViewModel entry)
    {
        var result = gateway.Undo(entry.Transaction.Id);
        Message = result.Restore is { } restore
            ? $"Undo: {restore.Outcome}{string.Concat(restore.FailedSteps.Select(i => $" {i.OriginalPath}: {i.Error}"))}"
            : $"Not undone: {result.Support.Limits}";
        Refresh();
    }
}

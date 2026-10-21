using PCHealth.Core;

namespace PCHealth.Ledger;

/// <param name="Resolved">Transactions that were pending or applied and are now verified or failed.</param>
/// <param name="Unaccounted">Quarantine slots that no ledger transaction explains. They are reported, never deleted.</param>
public sealed record StartupReport(IReadOnlyList<LedgerTransaction> Resolved, IReadOnlyList<string> Unaccounted);

/// <summary>
/// Finds operations that were interrupted (a crash, power loss or forced exit left them pending or applied) and
/// resolves each item from what is actually on disk (spec §9). Nothing is moved or deleted here except empty slots of
/// moves that never happened; anything ambiguous is marked failed with an explanation for the user.
/// </summary>
public sealed class InterruptedOperations(TransactionLedger ledger, QuarantineStore quarantine)
{
    /// <summary>Run once at startup, before any new operation.</summary>
    public StartupReport Resolve()
    {
        var listing = quarantine.List();
        var resolved = new List<LedgerTransaction>();
        var open = ledger.InState(TransactionState.Pending).Concat(ledger.InState(TransactionState.Applied)).ToArray();
        foreach (var transaction in open)
        {
            var slots = listing.Entries.Where(e => e.TransactionId == transaction.Id).ToDictionary(e => e.Position);
            var items = transaction.Items.Select((item, position) => transaction.Operation switch
            {
                LedgerOperation.MoveToQuarantine => ResolveQuarantineMove(item, slots.GetValueOrDefault(position)),
                LedgerOperation.Restore => ResolveRestore(item),
                LedgerOperation.PurgeQuarantine => ResolvePurge(item),
                _ => ResolveRecycle(item),
            }).ToArray();

            var done = items.Count(i => i.Status == ItemStatus.Done);
            var verified = done == items.Length && transaction.Operation != LedgerOperation.MoveToRecycleBin;
            var outcome = transaction.Operation == LedgerOperation.MoveToRecycleBin && done > 0
                ? $"Interrupted. {done} of {items.Length} items are no longer at their original path and are probably in the Recycle Bin; this could not be verified."
                : $"Interrupted and resolved at startup: {done} of {items.Length} items completed.";
            var record = transaction with { Items = items };
            if (transaction.State == TransactionState.Pending && verified)
            {
                ledger.Save(record with { State = TransactionState.Applied });
            }

            record = record with { State = verified ? TransactionState.Verified : TransactionState.Failed, Outcome = outcome };
            ledger.Save(record);
            resolved.Add(record);
        }

        var known = ledger.All().Select(t => t.Id).ToHashSet();
        var unaccounted = quarantine.List().Entries
            .Where(e => !known.Contains(e.TransactionId))
            .Select(e => e.Folder)
            .Concat(listing.Unreadable)
            .ToArray();
        return new StartupReport(resolved, unaccounted);
    }

    private LedgerItem ResolveQuarantineMove(LedgerItem item, QuarantineEntry? slot)
    {
        // Items already marked done are checked again: the transaction never reached verified.
        if (item.Status is ItemStatus.Failed or ItemStatus.Skipped)
        {
            return item;
        }

        var inQuarantine = slot is not null && Restorer.Exists(slot.ContentPath);
        var atOriginal = Restorer.Exists(item.OriginalPath);
        switch (inQuarantine, atOriginal)
        {
            case (true, false):
                quarantine.MarkStored(slot!);
                return item with { Status = ItemStatus.Done, RecoveryLocation = slot!.ContentPath };
            case (false, true):
                if (slot is not null)
                {
                    quarantine.Release(slot);
                }

                return item with { Status = ItemStatus.Skipped, Error = "Interrupted before it moved; it was left where it was." };
            case (true, true):
                return item with { Status = ItemStatus.Failed, Error = $"Found both at the original path and in quarantine ('{slot!.ContentPath}'); both were left for you to check." };
            default:
                return item with { Status = ItemStatus.Failed, Error = "Found neither at the original path nor in quarantine." };
        }
    }

    private static LedgerItem ResolveRestore(LedgerItem item)
    {
        if (item.Status is ItemStatus.Done or ItemStatus.Failed or ItemStatus.Skipped)
        {
            return item;
        }

        var inQuarantine = item.RecoveryLocation is { } location && Restorer.Exists(location);
        var atOriginal = Restorer.Exists(item.OriginalPath);
        return (inQuarantine, atOriginal) switch
        {
            (false, true) => item with { Status = ItemStatus.Done, RecoveryLocation = null, After = Restorer.Observe(item.OriginalPath, item.IsDirectory) },
            (true, false) => item with { Status = ItemStatus.Skipped, Error = "Interrupted before it moved back; it is still in quarantine." },
            (true, true) => item with { Status = ItemStatus.Failed, Error = "Found both at the original path and in quarantine; both were left for you to check." },
            _ => item with { Status = ItemStatus.Failed, Error = "Found neither at the original path nor in quarantine." },
        };
    }

    private static LedgerItem ResolvePurge(LedgerItem item)
    {
        if (item.Status is ItemStatus.Done or ItemStatus.Failed or ItemStatus.Skipped)
        {
            return item;
        }

        var slot = item.RecoveryLocation is { } content ? Path.GetDirectoryName(content) : null;
        return slot is not null && Directory.Exists(slot)
            ? item with { Status = ItemStatus.Skipped, Error = "Interrupted before it was deleted; it stays in quarantine and is purged next time." }
            : item with { Status = ItemStatus.Done, RecoveryLocation = null };
    }

    private static LedgerItem ResolveRecycle(LedgerItem item)
    {
        if (item.Status is ItemStatus.Done or ItemStatus.Failed or ItemStatus.Skipped)
        {
            return item;
        }

        return Restorer.Exists(item.OriginalPath)
            ? item with { Status = ItemStatus.Skipped, Error = "Interrupted before it moved; it was left where it was." }
            : item with { Status = ItemStatus.Done, Error = "Not at its original path; presumably in the Recycle Bin, but this could not be verified." };
    }
}

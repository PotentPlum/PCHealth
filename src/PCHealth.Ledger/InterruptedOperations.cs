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
public sealed class InterruptedOperations(TransactionLedger ledger, QuarantineStore quarantine, IFileCompression? compression = null)
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
                LedgerOperation.MoveToFolder => ResolveMove(item),
                LedgerOperation.CompressInPlace => ResolveCompression(item),
                LedgerOperation.Restore when transaction.Checks.FirstOrDefault() == Restorer.DecompressCheck => ResolveDecompression(item),
                LedgerOperation.Restore => ResolveRestore(item, Restorer.PlaceOf(transaction)),
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

    /// <param name="place">Where the restored items came from, e.g. "in quarantine".</param>
    private static LedgerItem ResolveRestore(LedgerItem item, string place)
    {
        if (item.Status is ItemStatus.Done or ItemStatus.Failed or ItemStatus.Skipped)
        {
            return item;
        }

        var notBack = item.RecoveryLocation is { } location && Restorer.Exists(location);
        var atOriginal = Restorer.Exists(item.OriginalPath);
        return (notBack, atOriginal) switch
        {
            (false, true) => item with { Status = ItemStatus.Done, RecoveryLocation = null, After = Restorer.Observe(item.OriginalPath, item.IsDirectory) },
            (true, false) => item with { Status = ItemStatus.Skipped, Error = $"Interrupted before it moved back; it is still {place}." },
            (true, true) => item with { Status = ItemStatus.Failed, Error = $"Found both at the original path and {place} ('{item.RecoveryLocation}'); both were left for you to check." },
            _ => item with { Status = ItemStatus.Failed, Error = $"Found neither at the original path nor {place}." },
        };
    }

    /// <summary>Compression never changes a file's contents, so an interrupted one is settled by asking whether it is compressed.</summary>
    private LedgerItem ResolveCompression(LedgerItem item)
    {
        if (item.Status is ItemStatus.Failed or ItemStatus.Skipped)
        {
            return item;
        }

        if (!File.Exists(item.OriginalPath))
        {
            return item with { Status = ItemStatus.Failed, Error = "It is no longer at its path; it was moved or deleted after the interruption." };
        }

        try
        {
            return compression is not null && compression.IsCompressed(item.OriginalPath)
                ? item with { Status = ItemStatus.Done, After = item.Before with { SizeOnDiskBytes = compression.SizeOnDisk(item.OriginalPath) } }
                : item with { Status = ItemStatus.Skipped, Error = compression is null
                    ? "Interrupted; whether it was compressed could not be checked here. Its contents are unchanged either way."
                    : "Interrupted before it was compressed; it was left as it was." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return item with { Status = ItemStatus.Failed, Error = $"Interrupted, and its state could not be checked: {ex.Message}. Its contents are unchanged either way." };
        }
    }

    private LedgerItem ResolveDecompression(LedgerItem item)
    {
        if (item.Status is ItemStatus.Done or ItemStatus.Failed or ItemStatus.Skipped)
        {
            return item;
        }

        try
        {
            return compression is null
                ? item with { Status = ItemStatus.Failed, Error = "Interrupted; whether it was decompressed could not be checked here. Its contents are unchanged either way." }
                : compression.IsCompressed(item.OriginalPath)
                    ? item with { Status = ItemStatus.Skipped, Error = "Interrupted before it was decompressed; it is still compressed." }
                    : item with { Status = ItemStatus.Done };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return item with { Status = ItemStatus.Failed, Error = $"Interrupted, and its state could not be checked: {ex.Message}" };
        }
    }

    /// <summary>A move to a folder records its destination before acting, so the item is found at one of two paths.</summary>
    private static LedgerItem ResolveMove(LedgerItem item)
    {
        if (item.Status is ItemStatus.Failed or ItemStatus.Skipped)
        {
            return item;
        }

        if (item.RecoveryLocation is null)
        {
            return item with { Status = ItemStatus.Failed, Error = "The destination was not recorded, so where it is could not be checked." };
        }

        var atDestination = Restorer.Exists(item.RecoveryLocation);
        var atOriginal = Restorer.Exists(item.OriginalPath);
        return (atDestination, atOriginal) switch
        {
            (true, false) => item with { Status = ItemStatus.Done, After = Restorer.Observe(item.RecoveryLocation, item.IsDirectory) },
            (false, true) => item with { Status = ItemStatus.Skipped, Error = "Interrupted before it moved; it was left where it was." },
            (true, true) => item with { Status = ItemStatus.Failed, Error = $"Found both at the original path and at '{item.RecoveryLocation}'; both were left for you to check." },
            _ => item with { Status = ItemStatus.Failed, Error = $"Found neither at the original path nor at '{item.RecoveryLocation}'." },
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

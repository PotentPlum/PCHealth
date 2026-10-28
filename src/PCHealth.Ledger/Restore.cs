using PCHealth.Core;

namespace PCHealth.Ledger;

/// <summary>
/// Moves an item from one path to another on the same volume, never overwriting and never following a link at the
/// source. Windows implements it by handle (T-24); <see cref="PlainFileMover"/> is the portable fallback.
/// </summary>
public interface IFileMover
{
    /// <exception cref="IOException">The destination exists or the move failed; nothing was moved.</exception>
    void Move(string source, string destination, bool isDirectory);
}

/// <summary>A move with the BCL's rename calls, which never overwrite.</summary>
public sealed class PlainFileMover : IFileMover
{
    public void Move(string source, string destination, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite: false);
        }
    }
}

/// <summary>
/// The platform's Recycle Bin, for putting back items this app moved there (T-40). Windows implements it on the
/// user's own <c>$Recycle.Bin</c> folder; tests use a fixture folder.
/// </summary>
public interface IRecycleBin
{
    /// <summary>True while <paramref name="location"/> is still in the Recycle Bin as the item deleted from <paramref name="originalPath"/>.</summary>
    bool Holds(string location, string originalPath);

    /// <summary>Moves the item back to <paramref name="originalPath"/>, never overwriting, and removes its Recycle Bin record.</summary>
    /// <exception cref="IOException">It could not be put back; nothing was moved.</exception>
    void Restore(string location, string originalPath, bool isDirectory);
}

/// <summary>Whether the product can undo a transaction itself, and the honest limits either way (spec §3.6, §6.4).</summary>
public sealed record RestoreSupport(bool CanRestore, string Limits);

public sealed record RestoreResult(LedgerTransaction? Restore, RestoreSupport Support)
{
    public bool Attempted => Restore is not null;
}

/// <summary>
/// Undoes transactions by operation type (spec §6.4). Quarantine moves and moves to a folder are restored by the
/// product, and so are Recycle Bin moves while the items are still in the Recycle Bin and a Recycle Bin is given. Each
/// item goes back to its original path unless something now exists there, in which case it stays where it is. Every
/// restore is itself a ledger transaction that follows the pending, applied, verified/failed states.
/// </summary>
public sealed class Restorer(TransactionLedger ledger, QuarantineStore quarantine, IFileMover? mover = null, TimeProvider? clock = null, IRecycleBin? recycleBin = null)
{
    private readonly IFileMover mover = mover ?? new PlainFileMover();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public RestoreSupport SupportFor(LedgerTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        switch (transaction.Operation)
        {
            case LedgerOperation.MoveToRecycleBin when recycleBin is null:
                return new(false, "Open the Recycle Bin and choose Restore. Items stay there until the Recycle Bin is emptied; this app cannot restore them for you.");
            case LedgerOperation.PurgeQuarantine:
                return new(false, "These items were deleted permanently when their retention period ended. They cannot be restored.");
            case LedgerOperation.Restore:
                return new(false, "This was itself a restore. To remove the items again, act on the recommendation again.");
        }

        if (transaction.State == TransactionState.Pending)
        {
            return new(false, "This change has not finished. It is resolved when the app next starts.");
        }

        if (transaction.Operation == LedgerOperation.MoveToFolder)
        {
            var moved = MovedEntries(transaction).Count;
            return moved == 0
                ? new(false, "Nothing from this change is still where it was moved to: it was moved, renamed or deleted since.")
                : new(true, $"{moved} items can be moved back where they were. An item is not moved back if something now exists at its original path.");
        }

        if (transaction.Operation == LedgerOperation.MoveToRecycleBin)
        {
            var recycled = RecycledEntries(transaction).Count;
            return recycled == 0
                ? new(false, "Nothing from this change is left in the Recycle Bin: it was emptied, or the items were restored or deleted there.")
                : new(true, $"{recycled} items can be put back where they were while they are still in the Recycle Bin. An item is not restored if something now exists at its original path.");
        }

        var stored = StoredEntries(transaction).Count;
        if (stored == 0)
        {
            return new(false, ledger.RelatedTo(transaction.Id).Any(t => t.Operation == LedgerOperation.PurgeQuarantine)
                ? "The retention period ended and the items were deleted permanently."
                : "Nothing from this change is left in quarantine.");
        }

        var deadline = transaction.RetentionDeadlineUtc is { } d ? $" until {d:u}" : "";
        return new(true, $"{stored} items can be put back where they were{deadline}. An item is not restored if something now exists at its original path.");
    }

    public RestoreResult Restore(Guid transactionId)
    {
        var original = ledger.Find(transactionId)
            ?? throw new InvalidOperationException($"Transaction {transactionId} is not in the ledger.");
        var support = SupportFor(original);
        if (!support.CanRestore)
        {
            return new RestoreResult(null, support);
        }

        var entries = original.Operation switch
        {
            LedgerOperation.MoveToRecycleBin => RecycledEntries(original),
            LedgerOperation.MoveToFolder => MovedEntries(original),
            _ => StoredEntries(original).Select(e => new Source(e.OriginalPath, e.IsDirectory, e.Before, e.ContentPath, e)).ToArray(),
        };
        var place = Place(original.Operation);
        var record = new LedgerTransaction(
            Guid.NewGuid(),
            clock.GetUtcNow(),
            LedgerOperation.Restore,
            TransactionState.Pending,
            $"You asked to undo the change made on {original.CreatedUtc:u}.",
            [$"the item is still {place}", "nothing exists at the original path"],
            entries.Select(e => new LedgerItem(e.OriginalPath, e.IsDirectory, e.Before, RecoveryLocation: e.Location)).ToArray(),
            expectedBytes: null,
            observedBytes: null,
            RecoveryMethod.None,
            "A restore is not undone automatically. To remove the items again, act on the recommendation again.")
        {
            RelatesTo = original.Id,
        };
        ledger.Save(record);

        var items = record.Items.Zip(entries, (item, entry) => original.Operation switch
        {
            LedgerOperation.MoveToRecycleBin => RestoreRecycled(item, entry),
            LedgerOperation.MoveToFolder => MoveBack(item, entry),
            _ => RestoreOne(item, entry),
        }).ToArray();
        record = record with { State = TransactionState.Applied, Items = items };
        ledger.Save(record);
        return new RestoreResult(Complete(record), support);
    }

    /// <summary>Finishes a restore record from what is now on disk: verified when every item is back.</summary>
    internal LedgerTransaction Complete(LedgerTransaction record)
    {
        var done = record.Items.Count(i => i.Status == ItemStatus.Done);
        var finished = record with
        {
            State = done == record.Items.Count ? TransactionState.Verified : TransactionState.Failed,
            Outcome = done == record.Items.Count
                ? $"{done} items are back at their original paths."
                : $"{done} of {record.Items.Count} items are back; the others stay {PlaceOf(record)}.",
        };
        ledger.Save(finished);
        return finished;
    }

    private LedgerItem RestoreOne(LedgerItem item, Source source)
    {
        var entry = source.Slot!;
        if (Exists(entry.OriginalPath))
        {
            return item with { Status = ItemStatus.Skipped, Error = "Something now exists at the original path, so the item stays in quarantine." };
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
            mover.Move(entry.ContentPath, entry.OriginalPath, entry.IsDirectory);
            ApplyMetadata(entry);
            quarantine.Release(entry);
            return item with { Status = ItemStatus.Done, RecoveryLocation = null, After = Observe(entry.OriginalPath, entry.IsDirectory) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return item with { Status = ItemStatus.Failed, Error = ex.Message };
        }
    }

    private LedgerItem RestoreRecycled(LedgerItem item, Source entry)
    {
        if (Exists(entry.OriginalPath))
        {
            return item with { Status = ItemStatus.Skipped, Error = "Something now exists at the original path, so the item stays in the Recycle Bin." };
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
            recycleBin!.Restore(entry.Location, entry.OriginalPath, entry.IsDirectory);
            return item with { Status = ItemStatus.Done, RecoveryLocation = null, After = Observe(entry.OriginalPath, entry.IsDirectory) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return item with { Status = ItemStatus.Failed, Error = ex.Message };
        }
    }

    /// <summary>Moves an item back from the folder it was moved to; it stays there when something now exists at its original path.</summary>
    private LedgerItem MoveBack(LedgerItem item, Source entry)
    {
        if (Exists(entry.OriginalPath))
        {
            return item with { Status = ItemStatus.Skipped, Error = "Something now exists at the original path, so the item stays where it was moved to." };
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
            mover.Move(entry.Location, entry.OriginalPath, entry.IsDirectory);
            return item with { Status = ItemStatus.Done, RecoveryLocation = null, After = Observe(entry.OriginalPath, entry.IsDirectory) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return item with { Status = ItemStatus.Failed, Error = ex.Message };
        }
    }

    /// <summary>The items of a move to a folder that are still where they went, as the same kind of item.</summary>
    private static IReadOnlyList<Source> MovedEntries(LedgerTransaction transaction) =>
        transaction.Items
            .Where(i => i.Status == ItemStatus.Done && i.RecoveryLocation is { } location
                && (i.IsDirectory ? Directory.Exists(location) : File.Exists(location)) && new FileInfo(location).LinkTarget is null)
            .Select(i => new Source(i.OriginalPath, i.IsDirectory, i.Before, i.RecoveryLocation!, null))
            .ToArray();

    /// <summary>Where the items of an operation wait to be put back, as words for the user.</summary>
    internal static string Place(LedgerOperation operation) => operation switch
    {
        LedgerOperation.MoveToRecycleBin => "in the Recycle Bin",
        LedgerOperation.MoveToFolder => "where it was moved to",
        _ => "in quarantine",
    };

    /// <summary>The place a restore record's items come from, read from its first check.</summary>
    internal static string PlaceOf(LedgerTransaction restore)
    {
        var check = restore.Checks.FirstOrDefault() ?? "";
        return check.Contains("Recycle Bin", StringComparison.Ordinal) ? "in the Recycle Bin"
            : check.Contains("moved to", StringComparison.Ordinal) ? "where they were moved to"
            : "in quarantine";
    }

    /// <summary>The items of a Recycle Bin move that are still there.</summary>
    private IReadOnlyList<Source> RecycledEntries(LedgerTransaction transaction) =>
        recycleBin is null
            ? []
            : transaction.Items
                .Where(i => i.Status == ItemStatus.Done && i.RecoveryLocation is { } location && recycleBin.Holds(location, i.OriginalPath))
                .Select(i => new Source(i.OriginalPath, i.IsDirectory, i.Before, i.RecoveryLocation!, null))
                .ToArray();

    private IReadOnlyList<QuarantineEntry> StoredEntries(LedgerTransaction transaction) =>
        quarantine.List().Entries
            .Where(e => e.TransactionId == transaction.Id && e.State == SlotState.Stored)
            .Where(e => e.Position < transaction.Items.Count && transaction.Items[e.Position].OriginalPath == e.OriginalPath)
            .OrderBy(e => e.Position)
            .ToArray();

    private static void ApplyMetadata(QuarantineEntry entry)
    {
        if (entry.IsDirectory)
        {
            return;
        }

        if (entry.Before.LastWriteTimeUtc is { } written)
        {
            File.SetLastWriteTimeUtc(entry.OriginalPath, written.UtcDateTime);
        }

        if (entry.Before.CreationTimeUtc is { } created)
        {
            File.SetCreationTimeUtc(entry.OriginalPath, created.UtcDateTime);
        }

        if (entry.Before.Attributes is { } attributes)
        {
            File.SetAttributes(entry.OriginalPath, attributes);
        }
    }

    internal static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    internal static ItemMetadata? Observe(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return Directory.Exists(path) ? new ItemMetadata(0, Directory.GetLastWriteTimeUtc(path), Directory.GetCreationTimeUtc(path), File.GetAttributes(path)) : null;
        }

        var info = new FileInfo(path);
        return info.Exists ? new ItemMetadata(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, info.Attributes) : null;
    }

    /// <summary>Where an item to restore is now: a quarantine slot, or the Recycle Bin or a moved-to folder when <paramref name="Slot"/> is null.</summary>
    private sealed record Source(string OriginalPath, bool IsDirectory, ItemMetadata Before, string Location, QuarantineEntry? Slot);
}

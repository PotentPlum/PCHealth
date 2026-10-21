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

/// <summary>Whether the product can undo a transaction itself, and the honest limits either way (spec §3.6, §6.4).</summary>
public sealed record RestoreSupport(bool CanRestore, string Limits);

public sealed record RestoreResult(LedgerTransaction? Restore, RestoreSupport Support)
{
    public bool Attempted => Restore is not null;
}

/// <summary>
/// Undoes transactions by operation type (spec §6.4). Only quarantine moves are restored by the product: each item
/// goes back to its original path unless something now exists there, in which case it stays in quarantine. Every
/// restore is itself a ledger transaction that follows the pending, applied, verified/failed states.
/// </summary>
public sealed class Restorer(TransactionLedger ledger, QuarantineStore quarantine, IFileMover? mover = null, TimeProvider? clock = null)
{
    private readonly IFileMover mover = mover ?? new PlainFileMover();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public RestoreSupport SupportFor(LedgerTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        switch (transaction.Operation)
        {
            case LedgerOperation.MoveToRecycleBin:
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

        var entries = StoredEntries(original);
        var record = new LedgerTransaction(
            Guid.NewGuid(),
            clock.GetUtcNow(),
            LedgerOperation.Restore,
            TransactionState.Pending,
            $"You asked to undo the change made on {original.CreatedUtc:u}.",
            ["the item is still in quarantine", "nothing exists at the original path"],
            entries.Select(e => new LedgerItem(e.OriginalPath, e.IsDirectory, e.Before, RecoveryLocation: e.ContentPath)).ToArray(),
            expectedBytes: null,
            observedBytes: null,
            RecoveryMethod.None,
            "A restore is not undone automatically. To remove the items again, act on the recommendation again.")
        {
            RelatesTo = original.Id,
        };
        ledger.Save(record);

        var items = record.Items.Zip(entries, (item, entry) => RestoreOne(item, entry)).ToArray();
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
                : $"{done} of {record.Items.Count} items are back; the others stay in quarantine.",
        };
        ledger.Save(finished);
        return finished;
    }

    private LedgerItem RestoreOne(LedgerItem item, QuarantineEntry entry)
    {
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
}

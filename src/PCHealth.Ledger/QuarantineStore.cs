using System.Text.Json;
using System.Text.Json.Serialization;
using PCHealth.Core;

namespace PCHealth.Ledger;

public enum SlotState
{
    /// <summary>The slot and its metadata exist, but the item has not been moved in yet.</summary>
    Prepared,

    /// <summary>The item is in the slot's <c>content</c> path.</summary>
    Stored,
}

/// <summary>One quarantined item and the metadata kept beside it.</summary>
public sealed record QuarantineEntry(
    Guid TransactionId,
    int Position,
    string OriginalPath,
    bool IsDirectory,
    ItemMetadata Before,
    DateTimeOffset QuarantinedUtc,
    DateTimeOffset DeadlineUtc,
    SlotState State,
    string Folder)
{
    /// <summary>Where the item itself lives inside the slot.</summary>
    public string ContentPath => Path.Combine(Folder, QuarantineStore.ContentName);
}

/// <param name="Unreadable">Slots whose metadata is missing or unreadable. They are never purged.</param>
public sealed record QuarantineListing(IReadOnlyList<QuarantineEntry> Entries, IReadOnlyList<string> Unreadable);

public sealed record PurgeFailure(QuarantineEntry Entry, string Error);

/// <param name="Transactions">The ledger transactions that recorded the purges, one per original transaction.</param>
public sealed record PurgeResult(
    IReadOnlyList<QuarantineEntry> Purged,
    IReadOnlyList<PurgeFailure> Failed,
    IReadOnlyList<Guid> Transactions);

/// <summary>
/// The quarantine area (spec §6.4, D-016). Each item gets its own slot, <c>&lt;root&gt;/&lt;transaction&gt;/&lt;position&gt;/</c>,
/// holding <c>item.json</c> (original path, metadata before the change, time quarantined and deadline) and
/// <c>content</c> (the item). The move itself is done by the platform layer; this store reserves slots, keeps the
/// metadata and purges items whose retention period has ended, recording every purge in the ledger.
/// </summary>
public sealed class QuarantineStore
{
    public const string ContentName = "content";
    public const string MetadataName = "item.json";
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(14);

    private const int MetadataVersion = 1;
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() }, WriteIndented = true };
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly TimeProvider clock;
    private TimeSpan retention;

    public QuarantineStore(string root, TimeProvider? clock = null, TimeSpan? retention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException($"The quarantine needs a full path: '{root}'.", nameof(root));
        }

        Retention = retention ?? DefaultRetention;
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        this.clock = clock ?? TimeProvider.System;
    }

    public string Root { get; }

    /// <summary>
    /// How long an item stays restorable. Shown to the user before they approve (spec §6.4). A change applies to items
    /// quarantined afterwards; items already in quarantine keep the deadline they were given.
    /// </summary>
    public TimeSpan Retention
    {
        get => retention;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero, nameof(Retention));
            retention = value;
        }
    }

    public DateTimeOffset DeadlineFor(DateTimeOffset quarantinedUtc) => quarantinedUtc + Retention;

    /// <summary>Reserves a slot and writes its metadata before anything moves, so an interrupted move can be found later.</summary>
    public QuarantineEntry Prepare(Guid transactionId, int position, string originalPath, bool isDirectory, ItemMetadata before)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        ArgumentNullException.ThrowIfNull(before);
        if (!Path.IsPathFullyQualified(originalPath))
        {
            throw new ArgumentException($"A quarantined item needs its full original path: '{originalPath}'.", nameof(originalPath));
        }

        var folder = Path.Combine(Root, transactionId.ToString("N"), position.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Directory.Exists(folder) || File.Exists(folder))
        {
            throw new InvalidOperationException($"The quarantine slot '{folder}' is already in use.");
        }

        Directory.CreateDirectory(folder);
        var now = clock.GetUtcNow();
        var entry = new QuarantineEntry(transactionId, position, originalPath, isDirectory, before, now, DeadlineFor(now), SlotState.Prepared, folder);
        Write(entry);
        return entry;
    }

    /// <summary>Records that the item is now in the slot.</summary>
    public QuarantineEntry MarkStored(QuarantineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureInside(entry.Folder);
        var present = entry.IsDirectory ? Directory.Exists(entry.ContentPath) : File.Exists(entry.ContentPath);
        if (!present)
        {
            throw new InvalidOperationException($"Nothing was moved into '{entry.ContentPath}'.");
        }

        var stored = entry with { State = SlotState.Stored };
        Write(stored);
        return stored;
    }

    /// <summary>Removes an empty or restored slot. The content must already be gone.</summary>
    public void Release(QuarantineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureInside(entry.Folder);
        if (File.Exists(entry.ContentPath) || Directory.Exists(entry.ContentPath))
        {
            throw new InvalidOperationException($"'{entry.ContentPath}' still holds the item; it cannot be released.");
        }

        DeleteTree(entry.Folder);
        var transactionFolder = Path.GetDirectoryName(entry.Folder)!;
        if (Directory.Exists(transactionFolder) && !Directory.EnumerateFileSystemEntries(transactionFolder).Any())
        {
            Directory.Delete(transactionFolder);
        }
    }

    public QuarantineListing List()
    {
        var entries = new List<QuarantineEntry>();
        var unreadable = new List<string>();
        if (!Directory.Exists(Root))
        {
            return new QuarantineListing(entries, unreadable);
        }

        foreach (var transactionFolder in Directory.EnumerateDirectories(Root).Order(StringComparer.Ordinal))
        {
            foreach (var slot in Directory.EnumerateDirectories(transactionFolder).Order(StringComparer.Ordinal))
            {
                if (Read(slot) is { } entry)
                {
                    entries.Add(entry);
                }
                else
                {
                    unreadable.Add(slot);
                }
            }
        }

        return new QuarantineListing(entries, unreadable);
    }

    /// <summary>
    /// Permanently deletes stored items whose deadline has passed, and only those. The deadline is the later of the
    /// slot's and the ledger's, so an extension recorded in the ledger is honored. Each purge is recorded in the
    /// ledger as pending before anything is deleted, then completed with the outcome per item.
    /// </summary>
    public PurgeResult PurgeExpired(TransactionLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var now = clock.GetUtcNow();
        var purged = new List<QuarantineEntry>();
        var failed = new List<PurgeFailure>();
        var transactions = new List<Guid>();

        var expired = List().Entries
            .Where(e => e.State == SlotState.Stored)
            .Select(e => (Entry: e, Original: ledger.Find(e.TransactionId)))
            .Where(x => Later(x.Entry.DeadlineUtc, x.Original?.RetentionDeadlineUtc) <= now)
            .GroupBy(x => x.Entry.TransactionId);

        foreach (var group in expired)
        {
            var entries = group.Select(x => x.Entry).OrderBy(e => e.Position).ToArray();
            var original = group.First().Original;
            var deadline = entries.Max(e => Later(e.DeadlineUtc, original?.RetentionDeadlineUtc));
            var record = new LedgerTransaction(
                Guid.NewGuid(),
                now,
                LedgerOperation.PurgeQuarantine,
                TransactionState.Pending,
                original is null
                    ? $"The retention period of quarantined items from transaction {group.Key} ended on {deadline:u}; that transaction is not in the ledger."
                    : $"The retention period ended on {deadline:u}.",
                ["retention deadline passed", "slot is inside the quarantine folder", "links inside are removed, never followed"],
                entries.Select(e => new LedgerItem(e.OriginalPath, e.IsDirectory, e.Before, RecoveryLocation: e.ContentPath)).ToArray(),
                expectedBytes: entries.Sum(e => e.Before.SizeBytes),
                observedBytes: null,
                RecoveryMethod.None,
                "Deleted permanently; these items can no longer be restored.")
            {
                RelatesTo = original?.Id,
            };
            ledger.Save(record);
            transactions.Add(record.Id);

            var items = new List<LedgerItem>();
            long freed = 0;
            foreach (var (entry, item) in entries.Zip(record.Items))
            {
                try
                {
                    EnsureInside(entry.Folder);
                    DeleteTree(entry.Folder);
                    purged.Add(entry);
                    freed += entry.Before.SizeBytes;
                    items.Add(item with { Status = ItemStatus.Done, RecoveryLocation = null });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    failed.Add(new PurgeFailure(entry, ex.Message));
                    items.Add(item with { Status = ItemStatus.Failed, Error = ex.Message });
                }
            }

            var transactionFolder = Path.GetDirectoryName(entries[0].Folder)!;
            if (Directory.Exists(transactionFolder) && !Directory.EnumerateFileSystemEntries(transactionFolder).Any())
            {
                Directory.Delete(transactionFolder);
            }

            var allDone = items.All(i => i.Status == ItemStatus.Done);
            record = record with { State = TransactionState.Applied, Items = items, ObservedBytes = freed };
            ledger.Save(record);
            ledger.Save(record with
            {
                State = allDone ? TransactionState.Verified : TransactionState.Failed,
                Items = items,
                ObservedBytes = freed,
                Outcome = allDone
                    ? $"{items.Count} quarantined items deleted permanently."
                    : $"{items.Count(i => i.Status == ItemStatus.Done)} of {items.Count} quarantined items deleted; the rest could not be deleted and stay in quarantine.",
            });
        }

        return new PurgeResult(purged, failed, transactions);
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset? b) => b is { } other && other > a ? other : a;

    private void Write(QuarantineEntry entry)
    {
        EnsureInside(entry.Folder);
        var path = Path.Combine(entry.Folder, MetadataName);
        var temporary = path + ".tmp";
        var document = new Metadata(
            MetadataVersion, entry.TransactionId, entry.Position, entry.OriginalPath, entry.IsDirectory, entry.Before,
            entry.QuarantinedUtc, entry.DeadlineUtc, entry.State);
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private static QuarantineEntry? Read(string slot)
    {
        try
        {
            var document = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(Path.Combine(slot, MetadataName)), Json);
            if (document is null || document.Version != MetadataVersion || document.Before is null
                || string.IsNullOrWhiteSpace(document.OriginalPath) || !Enum.IsDefined(document.State)
                || document.Position.ToString(System.Globalization.CultureInfo.InvariantCulture) != Path.GetFileName(slot)
                || document.TransactionId.ToString("N") != Path.GetFileName(Path.GetDirectoryName(slot)))
            {
                return null;
            }

            return new QuarantineEntry(
                document.TransactionId, document.Position, document.OriginalPath, document.IsDirectory, document.Before,
                document.QuarantinedUtc, document.DeadlineUtc, document.State, slot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void EnsureInside(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidOperationException($"'{path}' is outside the quarantine folder '{Root}'.");
        }
    }

    /// <summary>Deletes a tree without following links: a link is removed itself, never what it points to.</summary>
    private static void DeleteTree(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (attributes.HasFlag(FileAttributes.Directory) && OperatingSystem.IsWindows())
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }
        }
        else if (attributes.HasFlag(FileAttributes.Directory))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                DeleteTree(child);
            }

            Directory.Delete(path);
        }
        else
        {
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            File.Delete(path);
        }
    }

    private sealed record Metadata(
        int Version,
        Guid TransactionId,
        int Position,
        string OriginalPath,
        bool IsDirectory,
        ItemMetadata Before,
        DateTimeOffset QuarantinedUtc,
        DateTimeOffset DeadlineUtc,
        SlotState State);
}

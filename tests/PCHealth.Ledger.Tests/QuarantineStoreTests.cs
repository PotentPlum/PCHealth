using System.Text.Json;
using PCHealth.Core;
using PCHealth.Testing;

namespace PCHealth.Ledger.Tests;

/// <summary>Quarantine layout, retention and expiry against a temp root and a hand-set clock.</summary>
public class QuarantineStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Each_item_gets_a_slot_with_its_metadata_beside_it()
    {
        using var fixture = new Fixture();
        var original = fixture.Root.CreateFile("data/report.log", "12345");
        var before = new ItemMetadata(5, Start.AddDays(-40), Start.AddDays(-50), FileAttributes.Archive);
        var transaction = Guid.NewGuid();

        var prepared = fixture.Store.Prepare(transaction, 0, original, isDirectory: false, before);

        Assert.Equal(Path.Combine(fixture.Store.Root, transaction.ToString("N"), "0"), prepared.Folder);
        Assert.Equal(SlotState.Prepared, prepared.State);
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(prepared.Folder, QuarantineStore.MetadataName))))
        {
            var doc = json.RootElement;
            Assert.Equal(original, doc.GetProperty("OriginalPath").GetString());
            Assert.Equal(5, doc.GetProperty("Before").GetProperty("SizeBytes").GetInt64());
            Assert.Equal("Prepared", doc.GetProperty("State").GetString());
            Assert.Equal(Start.AddDays(14), doc.GetProperty("DeadlineUtc").GetDateTimeOffset());
        }

        File.Move(original, prepared.ContentPath);
        var stored = fixture.Store.MarkStored(prepared);

        var listed = Assert.Single(fixture.Store.List().Entries);
        Assert.Equal(stored, listed);
        Assert.Equal(before, listed.Before);
        Assert.Equal(SlotState.Stored, listed.State);
    }

    [Fact]
    public void Retention_defaults_to_14_days_and_is_configurable()
    {
        using var fixture = new Fixture();

        Assert.Equal(TimeSpan.FromDays(14), fixture.Store.Retention);
        Assert.Equal(Start.AddDays(30), new QuarantineStore(fixture.Root.Resolve("q2"), fixture.Clock, TimeSpan.FromDays(30)).DeadlineFor(Start));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuarantineStore(fixture.Root.Resolve("q3"), fixture.Clock, TimeSpan.Zero));
    }

    [Fact]
    public void Expiry_purges_only_items_past_their_deadline_and_records_each_purge()
    {
        using var fixture = new Fixture();
        var first = fixture.Quarantine("old/a.txt", "aaaa");
        fixture.Clock.Advance(TimeSpan.FromDays(5));
        var second = fixture.Quarantine("new/b.txt", "bb");

        fixture.Clock.Now = Start.AddDays(14).AddSeconds(-1);
        var early = fixture.Store.PurgeExpired(fixture.Ledger);
        Assert.Empty(early.Purged);
        Assert.Empty(fixture.Ledger.RelatedTo(first.TransactionId));

        fixture.Clock.Now = Start.AddDays(14);
        var due = fixture.Store.PurgeExpired(fixture.Ledger);

        Assert.Equal(first.TransactionId, Assert.Single(due.Purged).TransactionId);
        Assert.False(Directory.Exists(first.Folder));
        Assert.True(File.Exists(second.ContentPath));
        var record = Assert.Single(fixture.Ledger.RelatedTo(first.TransactionId));
        Assert.Equal(Assert.Single(due.Transactions), record.Id);
        Assert.Equal(LedgerOperation.PurgeQuarantine, record.Operation);
        Assert.Equal(TransactionState.Verified, record.State);
        Assert.Equal(RecoveryMethod.None, record.RecoveryMethod);
        Assert.Equal(4, record.ObservedBytes);
        Assert.Equal(first.OriginalPath, Assert.Single(record.Items).OriginalPath);
        Assert.Equal(ItemStatus.Done, record.Items[0].Status);
        Assert.Equal(TransactionState.Verified, fixture.Ledger.Find(first.TransactionId)!.State);

        fixture.Clock.Now = Start.AddDays(19);
        Assert.Equal(second.TransactionId, Assert.Single(fixture.Store.PurgeExpired(fixture.Ledger).Purged).TransactionId);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Store.Root));
    }

    [Fact]
    public void A_later_deadline_in_the_ledger_is_honored()
    {
        using var fixture = new Fixture();
        var entry = fixture.Quarantine("a.txt", "a");
        var original = fixture.Ledger.Find(entry.TransactionId)!;
        fixture.Ledger.Save(original with { RetentionDeadlineUtc = Start.AddDays(30) });

        fixture.Clock.Now = Start.AddDays(20);

        Assert.Empty(fixture.Store.PurgeExpired(fixture.Ledger).Purged);
        Assert.True(File.Exists(entry.ContentPath));
    }

    [Fact]
    public void Prepared_slots_and_unreadable_metadata_are_never_purged()
    {
        using var fixture = new Fixture();
        var prepared = fixture.Store.Prepare(Guid.NewGuid(), 0, fixture.Root.Resolve("x.txt"), isDirectory: false, new ItemMetadata(1));
        var stored = fixture.Quarantine("y.txt", "y");
        File.WriteAllText(Path.Combine(stored.Folder, QuarantineStore.MetadataName), "{ not json");

        fixture.Clock.Advance(TimeSpan.FromDays(100));
        var result = fixture.Store.PurgeExpired(fixture.Ledger);

        Assert.Empty(result.Purged);
        Assert.True(Directory.Exists(prepared.Folder));
        Assert.True(File.Exists(stored.ContentPath));
        Assert.Equal(stored.Folder, Assert.Single(fixture.Store.List().Unreadable));
    }

    [Fact]
    public void Purging_a_folder_removes_links_inside_it_but_not_what_they_point_to()
    {
        using var fixture = new Fixture();
        var outside = fixture.Root.CreateFile("outside/keep.txt", "keep");
        var folder = fixture.Root.CreateDirectory("app");
        fixture.Root.CreateFile("app/data.bin", "d");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(folder, "link"), Path.GetDirectoryName(outside)!);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Creating a symbolic link needs a privilege this account does not have.");
        }

        var entry = fixture.Quarantine("app", null);
        fixture.Clock.Advance(TimeSpan.FromDays(15));

        Assert.Single(fixture.Store.PurgeExpired(fixture.Ledger).Purged);
        Assert.False(Directory.Exists(entry.Folder));
        Assert.Equal("keep", File.ReadAllText(outside));
    }

    [Fact]
    public void Slots_are_never_reused_and_paths_must_be_full()
    {
        using var fixture = new Fixture();
        var transaction = Guid.NewGuid();
        fixture.Store.Prepare(transaction, 0, fixture.Root.Resolve("a"), isDirectory: false, new ItemMetadata(1));

        Assert.Throws<InvalidOperationException>(() => fixture.Store.Prepare(transaction, 0, fixture.Root.Resolve("b"), isDirectory: false, new ItemMetadata(1)));
        Assert.Throws<ArgumentException>(() => fixture.Store.Prepare(transaction, 1, "relative.txt", isDirectory: false, new ItemMetadata(1)));
        Assert.Throws<ArgumentException>(() => new QuarantineStore("quarantine"));
    }

    [Fact]
    public void Marking_a_slot_stored_needs_the_item_to_be_there()
    {
        using var fixture = new Fixture();
        var prepared = fixture.Store.Prepare(Guid.NewGuid(), 0, fixture.Root.Resolve("a.txt"), isDirectory: false, new ItemMetadata(1));

        Assert.Throws<InvalidOperationException>(() => fixture.Store.MarkStored(prepared));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Store = new QuarantineStore(Root.Resolve("quarantine"), Clock);
            Ledger = TransactionLedger.Open(Root.Resolve("ledger.db"));
        }

        public TempRoot Root { get; } = new();

        public ManualClock Clock { get; } = new(Start);

        public QuarantineStore Store { get; }

        public TransactionLedger Ledger { get; }

        /// <summary>Records a quarantine move in the ledger and moves the fixture item into its slot, as the gateway will.</summary>
        public QuarantineEntry Quarantine(string relative, string? contents)
        {
            var path = contents is null ? Root.Resolve(relative) : Root.CreateFile(relative, contents);
            var isDirectory = contents is null;
            var before = new ItemMetadata(contents?.Length ?? 1);
            var id = Guid.NewGuid();
            var entry = Store.Prepare(id, 0, path, isDirectory, before);
            Ledger.Save(new LedgerTransaction(
                id, Clock.Now, LedgerOperation.MoveToQuarantine, TransactionState.Verified, "Test move.", [],
                [new LedgerItem(path, isDirectory, before, RecoveryLocation: entry.ContentPath, Status: ItemStatus.Done)],
                before.SizeBytes, before.SizeBytes, RecoveryMethod.Quarantine, "Restorable while in quarantine.", entry.DeadlineUtc));
            if (isDirectory)
            {
                Directory.Move(path, entry.ContentPath);
            }
            else
            {
                File.Move(path, entry.ContentPath);
            }

            return Store.MarkStored(entry);
        }

        public void Dispose()
        {
            Ledger.Dispose();
            Root.Dispose();
        }
    }
}

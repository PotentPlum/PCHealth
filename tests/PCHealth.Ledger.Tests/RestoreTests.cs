using PCHealth.Core;
using PCHealth.Testing;

namespace PCHealth.Ledger.Tests;

/// <summary>Restore and interrupted-operation recovery against fixture files, a temp ledger and a hand-set clock.</summary>
public class RestoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_quarantine_move_is_restored_to_the_original_paths_with_their_timestamps()
    {
        using var fixture = new Fixture();
        var move = fixture.QuarantineMove(("docs/a.txt", "aaa"), ("docs/sub/b.txt", "bb"));
        Directory.Delete(fixture.Root.Resolve("docs"), recursive: true);

        var support = fixture.Restorer.SupportFor(move);
        var result = fixture.Restorer.Restore(move.Id);

        Assert.True(support.CanRestore);
        Assert.Contains("2 items can be put back", support.Limits);
        var restore = result.Restore!;
        Assert.Equal(TransactionState.Verified, restore.State);
        Assert.Equal(LedgerOperation.Restore, restore.Operation);
        Assert.Equal(move.Id, restore.RelatesTo);
        Assert.Equal("aaa", File.ReadAllText(fixture.Root.Resolve("docs/a.txt")));
        Assert.Equal("bb", File.ReadAllText(fixture.Root.Resolve("docs/sub/b.txt")));
        Assert.Equal(Start.AddDays(-30).UtcDateTime, File.GetLastWriteTimeUtc(fixture.Root.Resolve("docs/a.txt")));
        Assert.Empty(fixture.Store.List().Entries);
        Assert.False(fixture.Restorer.SupportFor(move).CanRestore);
        Assert.Equal(restore, fixture.Ledger.Find(restore.Id));
    }

    [Fact]
    public void A_conflict_at_the_original_path_fails_safely_and_the_item_stays_in_quarantine()
    {
        using var fixture = new Fixture();
        var move = fixture.QuarantineMove(("a.txt", "original a"), ("b.txt", "original b"));
        fixture.Root.CreateFile("b.txt", "something new");

        var restore = fixture.Restorer.Restore(move.Id).Restore!;

        Assert.Equal(TransactionState.Failed, restore.State);
        Assert.Equal(ItemStatus.Done, restore.Items[0].Status);
        Assert.Equal(ItemStatus.Skipped, restore.Items[1].Status);
        Assert.Contains("Something now exists at the original path", restore.Items[1].Error);
        Assert.Equal("something new", File.ReadAllText(fixture.Root.Resolve("b.txt")));
        Assert.Contains("1 of 2 items are back", restore.Outcome);
        var remaining = Assert.Single(fixture.Store.List().Entries);
        Assert.Equal("original b", File.ReadAllText(remaining.ContentPath));

        File.Delete(fixture.Root.Resolve("b.txt"));
        Assert.Equal(TransactionState.Verified, fixture.Restorer.Restore(move.Id).Restore!.State);
        Assert.Equal("original b", File.ReadAllText(fixture.Root.Resolve("b.txt")));
    }

    [Fact]
    public void Each_operation_type_states_its_own_restore_limits()
    {
        using var fixture = new Fixture();
        var recycled = fixture.Recorded(LedgerOperation.MoveToRecycleBin, RecoveryMethod.RecycleBin);
        var move = fixture.QuarantineMove(("a.txt", "a"));

        Assert.Contains("Open the Recycle Bin", fixture.Restorer.SupportFor(recycled).Limits);
        Assert.False(fixture.Restorer.Restore(recycled.Id).Attempted);

        fixture.Clock.Advance(TimeSpan.FromDays(15));
        var purge = fixture.Ledger.Find(Assert.Single(fixture.Store.PurgeExpired(fixture.Ledger).Transactions))!;

        var afterPurge = fixture.Restorer.SupportFor(move);
        Assert.False(afterPurge.CanRestore);
        Assert.Contains("deleted permanently", afterPurge.Limits);
        Assert.Contains("cannot be restored", fixture.Restorer.SupportFor(purge).Limits);
        Assert.Contains("itself a restore", fixture.Restorer.SupportFor(fixture.Recorded(LedgerOperation.Restore, RecoveryMethod.None)).Limits);
    }

    [Fact]
    public void An_interrupted_quarantine_move_is_resolved_from_what_is_on_disk()
    {
        using var fixture = new Fixture();
        var moved = fixture.Root.CreateFile("moved.txt", "m");
        var notMoved = fixture.Root.CreateFile("not-moved.txt", "n");
        var id = Guid.NewGuid();
        fixture.Ledger.Save(fixture.Transaction(id, LedgerOperation.MoveToQuarantine, RecoveryMethod.Quarantine, moved, notMoved));
        var slot0 = fixture.Store.Prepare(id, 0, moved, isDirectory: false, new ItemMetadata(1));
        var slot1 = fixture.Store.Prepare(id, 1, notMoved, isDirectory: false, new ItemMetadata(1));
        File.Move(moved, slot0.ContentPath);
        // The process stops here: the ledger still says pending and slot 0 was never marked stored.

        var report = new InterruptedOperations(fixture.Ledger, fixture.Store).Resolve();

        var resolved = Assert.Single(report.Resolved);
        Assert.Equal(TransactionState.Failed, resolved.State);
        Assert.Equal(ItemStatus.Done, resolved.Items[0].Status);
        Assert.Equal(slot0.ContentPath, resolved.Items[0].RecoveryLocation);
        Assert.Equal(ItemStatus.Skipped, resolved.Items[1].Status);
        Assert.True(File.Exists(notMoved));
        Assert.False(Directory.Exists(slot1.Folder));
        Assert.Equal(SlotState.Stored, Assert.Single(fixture.Store.List().Entries).State);
        Assert.Equal(resolved, fixture.Ledger.Find(id));
        Assert.True(fixture.Restorer.SupportFor(resolved).CanRestore);
        Assert.Empty(new InterruptedOperations(fixture.Ledger, fixture.Store).Resolve().Resolved);
    }

    [Fact]
    public void A_fully_completed_but_unverified_move_is_verified_at_startup()
    {
        using var fixture = new Fixture();
        var file = fixture.Root.CreateFile("a.txt", "a");
        var id = Guid.NewGuid();
        fixture.Ledger.Save(fixture.Transaction(id, LedgerOperation.MoveToQuarantine, RecoveryMethod.Quarantine, file));
        var slot = fixture.Store.Prepare(id, 0, file, isDirectory: false, new ItemMetadata(1));
        File.Move(file, slot.ContentPath);

        var resolved = Assert.Single(new InterruptedOperations(fixture.Ledger, fixture.Store).Resolve().Resolved);

        Assert.Equal(TransactionState.Verified, resolved.State);
    }

    [Fact]
    public void A_restore_interrupted_part_way_is_resolved_and_the_rest_can_still_be_restored()
    {
        using var fixture = new Fixture();
        var move = fixture.QuarantineMove(("a.txt", "a"), ("b.txt", "b"));
        var crashing = new Restorer(fixture.Ledger, fixture.Store, new CrashAfter(1), fixture.Clock);

        Assert.Throws<SimulatedCrash>(() => crashing.Restore(move.Id));
        var interrupted = Assert.Single(fixture.Ledger.RelatedTo(move.Id));
        Assert.Equal(TransactionState.Pending, interrupted.State);

        var resolved = Assert.Single(new InterruptedOperations(fixture.Ledger, fixture.Store).Resolve().Resolved);

        Assert.Equal(interrupted.Id, resolved.Id);
        Assert.Equal(TransactionState.Failed, resolved.State);
        Assert.Equal(ItemStatus.Done, resolved.Items[0].Status);
        Assert.Equal(ItemStatus.Skipped, resolved.Items[1].Status);
        Assert.True(File.Exists(fixture.Root.Resolve("a.txt")));
        Assert.Equal(TransactionState.Verified, fixture.Restorer.Restore(move.Id).Restore!.State);
        Assert.Equal("b", File.ReadAllText(fixture.Root.Resolve("b.txt")));
    }

    [Fact]
    public void Interrupted_purges_and_recycle_bin_moves_are_resolved_honestly()
    {
        using var fixture = new Fixture();
        var gone = fixture.Root.Resolve("gone.txt");
        var still = fixture.Root.CreateFile("still.txt", "s");
        var recycle = fixture.Transaction(Guid.NewGuid(), LedgerOperation.MoveToRecycleBin, RecoveryMethod.RecycleBin, gone, still);
        fixture.Ledger.Save(recycle);
        var leftSlot = fixture.Root.CreateFile("quarantine/x/0/content", "x");
        var purge = fixture.Transaction(Guid.NewGuid(), LedgerOperation.PurgeQuarantine, RecoveryMethod.None, fixture.Root.Resolve("p1"), fixture.Root.Resolve("p2"));
        purge = purge with
        {
            Items = [purge.Items[0] with { RecoveryLocation = leftSlot }, purge.Items[1] with { RecoveryLocation = fixture.Root.Resolve("quarantine/y/0/content") }],
        };
        fixture.Ledger.Save(purge);

        var resolved = new InterruptedOperations(fixture.Ledger, fixture.Store).Resolve().Resolved;

        var r = resolved.Single(t => t.Id == recycle.Id);
        Assert.Equal(TransactionState.Failed, r.State);
        Assert.Contains("could not be verified", r.Outcome);
        Assert.Equal(new[] { ItemStatus.Done, ItemStatus.Skipped }, r.Items.Select(i => i.Status));
        var p = resolved.Single(t => t.Id == purge.Id);
        Assert.Equal(new[] { ItemStatus.Skipped, ItemStatus.Done }, p.Items.Select(i => i.Status));
        Assert.True(File.Exists(leftSlot));
    }

    [Fact]
    public void Quarantine_slots_no_transaction_explains_are_reported_and_kept()
    {
        using var fixture = new Fixture();
        var stray = fixture.Store.Prepare(Guid.NewGuid(), 0, fixture.Root.Resolve("x.txt"), isDirectory: false, new ItemMetadata(1));

        var report = new InterruptedOperations(fixture.Ledger, fixture.Store).Resolve();

        Assert.Equal(stray.Folder, Assert.Single(report.Unaccounted));
        Assert.True(Directory.Exists(stray.Folder));
    }

    private sealed class SimulatedCrash : Exception;

    /// <summary>Moves the first <paramref name="moves"/> items, then stops the "process".</summary>
    private sealed class CrashAfter(int moves) : IFileMover
    {
        private int remaining = moves;

        public void Move(string source, string destination, bool isDirectory)
        {
            if (remaining-- == 0)
            {
                throw new SimulatedCrash();
            }

            new PlainFileMover().Move(source, destination, isDirectory);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Store = new QuarantineStore(Root.Resolve("quarantine"), Clock);
            Ledger = TransactionLedger.Open(Root.Resolve("ledger.db"));
            Restorer = new Restorer(Ledger, Store, clock: Clock);
        }

        public TempRoot Root { get; } = new();

        public ManualClock Clock { get; } = new(Start);

        public QuarantineStore Store { get; }

        public TransactionLedger Ledger { get; }

        public Restorer Restorer { get; }

        public LedgerTransaction Transaction(Guid id, LedgerOperation operation, RecoveryMethod recovery, params string[] paths) => new(
            id, Clock.Now, operation, TransactionState.Pending, "Test.", [],
            paths.Select(p => new LedgerItem(p, IsDirectory: false, new ItemMetadata(1))).ToArray(),
            paths.Length, null, recovery, "Test limits.");

        public LedgerTransaction Recorded(LedgerOperation operation, RecoveryMethod recovery)
        {
            var transaction = Transaction(Guid.NewGuid(), operation, recovery, Root.Resolve("r.txt")) with { State = TransactionState.Verified };
            Ledger.Record(transaction);
            return transaction;
        }

        /// <summary>Quarantines fixture files the way the gateway will: pending record, slots, moves, verified record.</summary>
        public LedgerTransaction QuarantineMove(params (string Relative, string Contents)[] files)
        {
            var paths = files.Select(f => Root.CreateFile(f.Relative, f.Contents)).ToArray();
            foreach (var path in paths)
            {
                File.SetLastWriteTimeUtc(path, Start.AddDays(-30).UtcDateTime);
            }

            var id = Guid.NewGuid();
            var before = paths.Select(p => new ItemMetadata(new FileInfo(p).Length, Start.AddDays(-30))).ToArray();
            var pending = new LedgerTransaction(
                id, Clock.Now, LedgerOperation.MoveToQuarantine, TransactionState.Pending, "Test move.", [],
                paths.Select((p, i) => new LedgerItem(p, IsDirectory: false, before[i])).ToArray(),
                before.Sum(b => b.SizeBytes), null, RecoveryMethod.Quarantine, "Restorable while in quarantine.", Store.DeadlineFor(Clock.Now));
            Ledger.Save(pending);
            var items = paths.Select((p, i) =>
            {
                var slot = Store.Prepare(id, i, p, isDirectory: false, before[i]);
                File.Move(p, slot.ContentPath);
                Store.MarkStored(slot);
                return pending.Items[i] with { Status = ItemStatus.Done, RecoveryLocation = slot.ContentPath };
            }).ToArray();
            Ledger.Save(pending with { State = TransactionState.Applied, Items = items });
            var verified = pending with { State = TransactionState.Verified, Items = items, ObservedBytes = pending.ExpectedBytes };
            Ledger.Save(verified);
            return verified;
        }

        public void Dispose()
        {
            Ledger.Dispose();
            Root.Dispose();
        }
    }
}

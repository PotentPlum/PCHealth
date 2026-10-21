using Microsoft.Data.Sqlite;
using PCHealth.Core;
using PCHealth.Testing;

namespace PCHealth.Ledger.Tests;

/// <summary>Every test uses a ledger file in its own temp root.</summary>
public class TransactionLedgerTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 21, 9, 30, 15, 123, TimeSpan.FromHours(2));

    [Fact]
    public void Every_spec_field_survives_a_restart()
    {
        using var root = new TempRoot();
        var path = root.Resolve("ledger.db");
        var original = Sample() with
        {
            State = TransactionState.Failed,
            ObservedBytes = 700,
            RetentionDeadlineUtc = Created.AddDays(14),
            Outcome = "1 of 2 items moved; 1 was in use.",
            Items =
            [
                new LedgerItem(
                    root.Resolve("data/a.log"),
                    IsDirectory: false,
                    new ItemMetadata(700, Created.AddDays(-30), Created.AddDays(-60), FileAttributes.Archive | FileAttributes.ReadOnly),
                    After: new ItemMetadata(700, Created.AddDays(-30)),
                    RecoveryLocation: root.Resolve("quarantine/1/a.log"),
                    Status: ItemStatus.Done),
                new LedgerItem(root.Resolve("data/b"), IsDirectory: true, new ItemMetadata(300), Status: ItemStatus.Failed, Error: "In use by another process."),
            ],
        };

        using (var ledger = TransactionLedger.Open(path))
        {
            ledger.Save(original);
        }

        using var reopened = TransactionLedger.Open(path);
        var loaded = reopened.Find(original.Id)!;

        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(original.CreatedUtc, loaded.CreatedUtc);
        Assert.Equal(OperationKind.MoveToQuarantine, loaded.Operation);
        Assert.Equal(TransactionState.Failed, loaded.State);
        Assert.Equal(original.Reason, loaded.Reason);
        Assert.Equal(original.Checks, loaded.Checks);
        Assert.Equal(original.Items, loaded.Items);
        Assert.Equal(1000, loaded.ExpectedBytes);
        Assert.Equal(700, loaded.ObservedBytes);
        Assert.Equal(RecoveryMethod.Quarantine, loaded.RecoveryMethod);
        Assert.Equal(original.RecoveryLimitations, loaded.RecoveryLimitations);
        Assert.Equal(original.RetentionDeadlineUtc, loaded.RetentionDeadlineUtc);
        Assert.Equal(original.Outcome, loaded.Outcome);
        Assert.Equal("In use by another process.", Assert.Single(loaded.FailedSteps).Error);
    }

    [Fact]
    public void Progress_can_be_updated_but_terms_cannot_be_rewritten()
    {
        using var root = new TempRoot();
        using var ledger = TransactionLedger.Open(root.Resolve("ledger.db"));
        var pending = Sample();
        ledger.Save(pending);

        ledger.Save(pending with { State = TransactionState.Verified, ObservedBytes = 1000, Outcome = "Moved." });

        var loaded = ledger.Find(pending.Id)!;
        Assert.Equal(TransactionState.Verified, loaded.State);
        Assert.Equal(1000, loaded.ObservedBytes);
        Assert.Single(ledger.All());

        var rewritten = new LedgerTransaction(
            pending.Id, pending.CreatedUtc, pending.Operation, pending.State, "A different reason.", pending.Checks, pending.Items,
            pending.ExpectedBytes, null, pending.RecoveryMethod, pending.RecoveryLimitations);
        Assert.Throws<InvalidOperationException>(() => ledger.Save(rewritten));
        Assert.Equal(pending.Reason, ledger.Find(pending.Id)!.Reason);
    }

    [Fact]
    public void Transactions_are_listed_newest_first_and_by_state()
    {
        using var root = new TempRoot();
        using var ledger = TransactionLedger.Open(root.Resolve("ledger.db"));
        var older = Sample(Created.AddHours(-1));
        var newer = Sample(Created) with { State = TransactionState.Verified };
        ledger.Save(older);
        ledger.Save(newer);

        Assert.Equal(new[] { newer.Id, older.Id }, ledger.All().Select(t => t.Id));
        Assert.Equal(older.Id, Assert.Single(ledger.InState(TransactionState.Pending)).Id);
        Assert.Null(ledger.Find(Guid.NewGuid()));
    }

    [Fact]
    public void Invalid_transactions_are_never_saved()
    {
        using var root = new TempRoot();
        using var ledger = TransactionLedger.Open(root.Resolve("ledger.db"));

        Assert.Throws<ArgumentException>(() => ledger.Save(Sample() with { Items = [] }));
        Assert.Throws<ArgumentException>(() => ledger.Save(Sample() with { Checks = [" "] }));
        Assert.Throws<ArgumentException>(() => new LedgerTransaction(
            Guid.Empty, Created, OperationKind.MoveToRecycleBin, TransactionState.Pending, "r", [], Sample().Items, null, null, RecoveryMethod.RecycleBin, "l"));
        Assert.Throws<ArgumentException>(() => new LedgerTransaction(
            Guid.NewGuid(), Created, OperationKind.MoveToRecycleBin, TransactionState.Pending, "r", [], Sample().Items, null, null, RecoveryMethod.None, "l", Created));
        Assert.Empty(ledger.All());
    }

    [Fact]
    public void A_file_that_is_not_a_ledger_is_refused_and_left_alone()
    {
        using var root = new TempRoot();
        var path = root.CreateFile("notes.db", "these are not database pages, just text");

        Assert.Throws<InvalidDataException>(() => TransactionLedger.Open(path));
        Assert.Equal("these are not database pages, just text", File.ReadAllText(path));
    }

    [Fact]
    public void A_ledger_from_a_newer_version_is_refused()
    {
        using var root = new TempRoot();
        var path = root.Resolve("ledger.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {TransactionLedger.SchemaVersion + 1};";
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<InvalidDataException>(() => TransactionLedger.Open(path));
        Assert.Contains("newer version", error.Message);
    }

    [Fact]
    public void The_ledger_needs_a_full_path()
    {
        Assert.Throws<ArgumentException>(() => TransactionLedger.Open("ledger.db"));
    }

    private static LedgerTransaction Sample(DateTimeOffset? created = null) => new(
        Guid.NewGuid(),
        created ?? Created,
        OperationKind.MoveToQuarantine,
        TransactionState.Pending,
        "Possible leftover data: OldEditor. No installed application claims it.",
        ["path resolved by handle", "not protected", "not excluded"],
        [new LedgerItem(Path.Combine(Path.GetTempPath(), "PCHealth.Tests", "OldEditor"), IsDirectory: true, new ItemMetadata(1000))],
        expectedBytes: 1000,
        observedBytes: null,
        RecoveryMethod.Quarantine,
        "Restorable to its original path while in quarantine.");
}

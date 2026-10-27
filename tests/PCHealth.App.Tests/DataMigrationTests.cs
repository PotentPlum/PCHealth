using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Core;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Testing;

namespace PCHealth.App.Tests;

/// <summary>Moving the data out of the 0.1.0 folder (D-021), with both folders inside a temp root.</summary>
public sealed class DataMigrationTests : IDisposable
{
    private readonly TempRoot root = new();

    [Fact]
    public void Data_and_quarantine_move_and_the_ledger_follows()
    {
        var legacy = new AppPaths(root.Resolve("PCHealth"));
        var paths = new AppPaths(root.Resolve("PCHealthData"));
        var slotContent = root.CreateFile("PCHealth/Quarantine/slot1/content", "quarantined");
        root.CreateFile("PCHealth/settings.json", "{}");
        root.CreateFile("PCHealth/first-run-complete", "done");
        root.CreateFile("PCHealth/current/PCHealth.App.exe", "the installed app");
        var transaction = Transaction(slotContent);
        using (var ledger = TransactionLedger.Open(legacy.Ledger))
        {
            ledger.Save(transaction with { State = TransactionState.Pending });
            ledger.Save(transaction with { State = TransactionState.Applied });
            ledger.Save(transaction);
        }

        // The ledger, its log files if SQLite left any, the settings, the first-run marker and the quarantine slot.
        Assert.InRange(DataMigration.Run(legacy.DataRoot, paths), 4, 6);

        Assert.Equal("quarantined", File.ReadAllText(Path.Combine(paths.Quarantine, "slot1", "content")));
        Assert.True(File.Exists(paths.Settings));
        Assert.False(FirstRunViewModel.IsNeeded(paths));
        Assert.False(Directory.Exists(legacy.Quarantine));
        Assert.False(File.Exists(legacy.Ledger));
        Assert.True(File.Exists(root.Resolve("PCHealth/current/PCHealth.App.exe")));
        using (var ledger = TransactionLedger.Open(paths.Ledger))
        {
            Assert.Equal(Path.Combine(paths.Quarantine, "slot1", "content"), ledger.Find(transaction.Id)!.Items[0].RecoveryLocation);
        }

        Assert.Equal(0, DataMigration.Run(legacy.DataRoot, paths));
    }

    [Fact]
    public void Nothing_already_in_the_new_folder_is_overwritten()
    {
        var legacy = new AppPaths(root.Resolve("PCHealth"));
        var paths = new AppPaths(root.Resolve("PCHealthData"));
        root.CreateFile("PCHealth/settings.json", "old");
        root.CreateFile("PCHealth/exclusions.json", "[]");
        root.CreateFile("PCHealthData/settings.json", "new");

        Assert.Equal(1, DataMigration.Run(legacy.DataRoot, paths));

        Assert.Equal("new", File.ReadAllText(paths.Settings));
        Assert.Equal("old", File.ReadAllText(legacy.Settings));
        Assert.True(File.Exists(paths.Exclusions));
    }

    [Fact]
    public void Without_an_old_folder_nothing_happens()
    {
        Assert.Equal(0, DataMigration.Run(root.Resolve("PCHealth"), new AppPaths(root.Resolve("PCHealthData"))));
        Assert.False(Directory.Exists(root.Resolve("PCHealthData")));
    }

    [Fact]
    public void The_app_never_suggests_changing_its_own_data()
    {
        var paths = new AppPaths(root.Resolve("PCHealthData"));
        using var services = AppServices.Build(paths);
        var policy = services.GetRequiredService<ProtectionPolicy>();

        Assert.Equal(ProtectionKind.AppData, policy.Check(paths.Quarantine).Kind);
        Assert.Equal(ProtectionKind.AppData, policy.Check(root.Resolve("PCHealth/settings.json")).Kind);
        Assert.False(policy.Check(root.Resolve("Other")).IsBlocked);
    }

    public void Dispose() => root.Dispose();

    private LedgerTransaction Transaction(string recoveryLocation) => new(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        LedgerOperation.MoveToQuarantine,
        TransactionState.Verified,
        "Temporary files.",
        ["not protected"],
        [new LedgerItem(root.Resolve("data/a.tmp"), IsDirectory: false, new ItemMetadata(11), RecoveryLocation: recoveryLocation, Status: ItemStatus.Done)],
        expectedBytes: 11,
        observedBytes: 11,
        RecoveryMethod.Quarantine,
        "Restorable while in quarantine.");
}

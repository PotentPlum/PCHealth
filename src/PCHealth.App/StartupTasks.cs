using PCHealth.Ledger;

namespace PCHealth.App;

public sealed record StartupResult(StartupReport Interrupted, PurgeResult Purged);

/// <summary>
/// Runs before the user can start anything new (spec §6.4, §9): settles operations a crash or forced exit left
/// unfinished, then deletes quarantined items whose retention period has ended, recording each purge.
/// </summary>
public sealed class StartupTasks(InterruptedOperations interrupted, QuarantineStore quarantine, TransactionLedger ledger)
{
    public StartupResult Run() => new(interrupted.Resolve(), quarantine.PurgeExpired(ledger));
}

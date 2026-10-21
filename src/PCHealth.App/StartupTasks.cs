using PCHealth.Execution;
using PCHealth.Ledger;

namespace PCHealth.App;

/// <param name="Purged">Null when changes are paused: expiry is an automated change and waits until you resume.</param>
public sealed record StartupResult(StartupReport Interrupted, PurgeResult? Purged);

/// <summary>
/// Runs before the user can start anything new (spec §6.4, §9): settles operations a crash or forced exit left
/// unfinished, then, unless changes are paused, deletes quarantined items whose retention period has ended, recording
/// each purge.
/// </summary>
public sealed class StartupTasks(InterruptedOperations interrupted, ExecutionGateway gateway)
{
    public StartupResult Run() => new(interrupted.Resolve(), gateway.PurgeExpired());
}

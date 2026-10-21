using PCHealth.Core;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;

namespace PCHealth.Execution;

public enum ExecutionStatus
{
    /// <summary>Nothing was attempted; see the summary for why.</summary>
    Refused,
    Completed,

    /// <summary>Some items changed; others were refused or failed.</summary>
    PartiallyCompleted,

    /// <summary>Items were attempted and none changed.</summary>
    Failed,
}

public sealed record ItemOutcome(ProposedOperation Operation, ItemStatus Status, string? Reason);

public sealed record ExecutionResult(ExecutionStatus Status, string Summary, Guid? TransactionId, IReadOnlyList<ItemOutcome> Items);

/// <summary>
/// The one path through which the product changes anything (spec §6.3, CLAUDE.md rules 2–4). For each approved
/// operation it runs, in order: validate the request → protection and exclusion checks → revalidate right before
/// acting (final path by handle, links, scope, protection again, expected state, open handles, elevation, volume) →
/// execute → verify → ledger. Any check that fails or cannot be made refuses the item; nothing is assumed. The
/// ledger records the transaction as pending before the first change, so an interruption is found at startup.
/// The global Pause blocks everything, including items still queued in a running request.
/// </summary>
public sealed class ExecutionGateway(
    IPathResolver resolver,
    IPlatformOperations platform,
    ProtectionPolicy protection,
    ExclusionList exclusions,
    TransactionLedger ledger,
    QuarantineStore quarantine,
    GlobalPause pause,
    TimeProvider? clock = null)
{
    public const string NeedsElevationReason = "needs administrator rights, not supported yet.";

    private static readonly string[] Checks =
    [
        "request matches an approved recommendation",
        "not protected and not excluded",
        "final path resolved by handle and unchanged",
        "not a link and inside its folder",
        "size and last-write time as when found",
        "not in use by a running process",
        "no administrator rights needed",
        "quarantine on the same volume",
        "Recycle Bin suitable: local drive, turned on, large enough",
    ];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ScopeValidator scope = new(resolver);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <param name="approved">The operations the user approved: some or all of the recommendation's operations.</param>
    public ExecutionResult Execute(Recommendation recommendation, IReadOnlyList<ProposedOperation> approved)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(approved);

        // 1. Validate the request.
        if (pause.IsPaused)
        {
            return Refuse(approved, "Changes are paused. Resume to allow them again.");
        }

        if (recommendation.Disposition != Disposition.Act)
        {
            return Refuse(approved, $"This recommendation is for review only: {recommendation.ReviewReason}");
        }

        if (approved.Count == 0)
        {
            return Refuse(approved, "Nothing was approved.");
        }

        if (approved.Distinct().Count() != approved.Count || approved.Any(o => !recommendation.Operations.Contains(o)))
        {
            return Refuse(approved, "The approved items do not match the recommendation; nothing was changed.");
        }

        var kinds = approved.Select(o => o.Kind).Distinct().ToArray();
        if (kinds.Length != 1 || ProposedOperation.RecoveryFor(kinds[0]) != recommendation.Recovery.Method)
        {
            return Refuse(approved, "The approved items do not all provide the recovery the recommendation promises.");
        }

        // 2. Protection and exclusion checks, before anything is recorded.
        var outcomes = new ItemOutcome?[approved.Count];
        for (var i = 0; i < approved.Count; i++)
        {
            if (PolicyBlock(approved[i].Path) is { } reason)
            {
                outcomes[i] = new ItemOutcome(approved[i], ItemStatus.Skipped, reason);
            }
        }

        if (outcomes.All(o => o is not null))
        {
            return new ExecutionResult(ExecutionStatus.Refused, "Every approved item is protected or excluded; nothing was changed.", null, outcomes!);
        }

        var operation = LedgerOperations.From(kinds[0]);
        var now = clock.GetUtcNow();
        var record = new LedgerTransaction(
            Guid.NewGuid(),
            now,
            operation,
            TransactionState.Pending,
            $"{recommendation.WhatWeFound}: {recommendation.WhyItMatters}",
            Checks,
            approved.Select(o => new LedgerItem(o.Path, o.IsDirectory, Before(o))).ToArray(),
            expectedBytes: approved.Sum(o => o.ExpectedSize.Bytes),
            observedBytes: null,
            recommendation.Recovery.Method,
            recommendation.Recovery.Limitations,
            operation == LedgerOperation.MoveToQuarantine ? quarantine.DeadlineFor(now) : null);
        ledger.Save(record);

        var items = record.Items.ToArray();
        long freed = 0;
        for (var i = 0; i < approved.Count; i++)
        {
            if (outcomes[i] is { } refused)
            {
                items[i] = items[i] with { Status = ItemStatus.Skipped, Error = refused.Reason };
                continue;
            }

            var (item, outcome) = Run(record.Id, i, approved[i], items[i]);
            items[i] = item;
            outcomes[i] = outcome;
            if (outcome.Status == ItemStatus.Done)
            {
                freed += item.Before.SizeBytes;
            }

            // Progress is saved after every item, so the ledger never lags far behind the disk.
            ledger.Save(record with { Items = items.ToArray() });
        }

        var done = items.Count(i => i.Status == ItemStatus.Done);
        record = record with { Items = items, ObservedBytes = freed };
        if (done > 0)
        {
            record = record with { State = TransactionState.Applied };
            ledger.Save(record);
        }

        var status = done == items.Length ? ExecutionStatus.Completed : done == 0 ? ExecutionStatus.Failed : ExecutionStatus.PartiallyCompleted;
        var summary = status switch
        {
            ExecutionStatus.Completed => $"{done} items changed and checked.",
            ExecutionStatus.PartiallyCompleted => $"{done} of {items.Length} items changed; the others were left alone.",
            _ => "No item was changed.",
        };
        ledger.Save(record with
        {
            State = status == ExecutionStatus.Completed ? TransactionState.Verified : TransactionState.Failed,
            Outcome = summary,
        });
        return new ExecutionResult(status, summary, record.Id, outcomes!);
    }

    private (LedgerItem Item, ItemOutcome Outcome) Run(Guid transactionId, int position, ProposedOperation operation, LedgerItem item)
    {
        // 3. Revalidate right before acting.
        if (Revalidate(operation) is { } refusal)
        {
            return (item with { Status = ItemStatus.Skipped, Error = refusal }, new ItemOutcome(operation, ItemStatus.Skipped, refusal));
        }

        // 4. Execute.
        QuarantineEntry? slot = null;
        string? recycledTo = null;
        try
        {
            if (operation.Kind == OperationKind.MoveToQuarantine)
            {
                slot = quarantine.Prepare(transactionId, position, operation.Path, operation.IsDirectory, item.Before);
                platform.MoveToQuarantine(operation.Path, slot.ContentPath, operation.IsDirectory);
            }
            else
            {
                recycledTo = platform.MoveToRecycleBin(operation.Path, operation.IsDirectory);
            }
        }
        catch (PermanentlyDeletedException ex)
        {
            return Failed(operation, item with { RecoveryLocation = null }, $"It was deleted permanently instead of going to the Recycle Bin, so it cannot be restored. {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (slot is not null && platform.Inspect(slot.ContentPath) is null)
            {
                quarantine.Release(slot);
            }

            return Failed(operation, item, $"It could not be moved: {ex.Message}");
        }

        // 5. Verify.
        if (platform.Inspect(operation.Path) is not null)
        {
            return Failed(operation, item, "It is still at its original path after the move.");
        }

        if (slot is not null)
        {
            if (platform.Inspect(slot.ContentPath) is null)
            {
                return Failed(operation, item, "It is not in quarantine after the move.");
            }

            quarantine.MarkStored(slot);
            item = item with { RecoveryLocation = slot.ContentPath };
        }
        else
        {
            item = item with { RecoveryLocation = recycledTo };
        }

        return (item with { Status = ItemStatus.Done }, new ItemOutcome(operation, ItemStatus.Done, null));
    }

    /// <summary>The reason the item must be left alone, or null when every check passed.</summary>
    private string? Revalidate(ProposedOperation operation)
    {
        if (pause.IsPaused)
        {
            return "Changes were paused before this item.";
        }

        var parent = Path.GetDirectoryName(operation.Path);
        if (parent is null)
        {
            return "A drive root is never changed.";
        }

        var resolved = resolver.Resolve(operation.Path);
        if (!resolved.IsResolved)
        {
            return $"Its path could not be confirmed: {resolved.Message}";
        }

        if (resolved.IsReparsePoint)
        {
            return "It is a link; links are never changed.";
        }

        if (!string.Equals(Path.TrimEndingDirectorySeparator(resolved.FinalPath!), Path.TrimEndingDirectorySeparator(operation.Path), PathComparison))
        {
            return $"Its path now leads to '{resolved.FinalPath}' instead.";
        }

        var decision = scope.Check(operation.Path, parent);
        if (!decision.IsAllowed)
        {
            return $"Its path could not be confirmed: {decision.Message}";
        }

        if (!string.Equals(Path.TrimEndingDirectorySeparator(decision.FinalPath!), Path.TrimEndingDirectorySeparator(operation.Path), PathComparison))
        {
            return $"Its path now leads to '{decision.FinalPath}' instead.";
        }

        if (resolved.IsDirectory != operation.IsDirectory)
        {
            return "It changed between a file and a folder since it was found.";
        }

        if (PolicyBlock(resolved.FinalPath!) is { } blocked)
        {
            return blocked;
        }

        if (platform.Inspect(resolved.FinalPath!) is not { } state)
        {
            return "It no longer exists.";
        }

        if (!operation.IsDirectory
            && (state.SizeBytes != operation.ExpectedSize.Bytes || (operation.ExpectedLastWriteTimeUtc is { } written && state.LastWriteTimeUtc != written)))
        {
            return "It changed since it was found, so it was left alone.";
        }

        if (platform.ProcessesUsing(resolved.FinalPath!) is { Count: > 0 } processes)
        {
            return $"It is in use by {string.Join(", ", processes)}.";
        }

        if (platform.NeedsElevation(resolved.FinalPath!))
        {
            return $"Changing it {NeedsElevationReason}";
        }

        if (operation.Kind == OperationKind.MoveToRecycleBin
            && platform.CannotRecycle(resolved.FinalPath!, operation.IsDirectory ? operation.ExpectedSize.Bytes : state.SizeBytes) is { } unsuitable)
        {
            return $"The Recycle Bin is not suitable for it: {unsuitable}";
        }

        if (operation.Kind == OperationKind.MoveToQuarantine && !platform.SameVolume(resolved.FinalPath!, quarantine.Root))
        {
            return "It is on a different drive from the quarantine folder, so it cannot be quarantined.";
        }

        return null;
    }

    /// <summary>The item's metadata as recorded before anything changes; a folder keeps the size measured when it was found.</summary>
    private ItemMetadata Before(ProposedOperation operation) =>
        platform.Inspect(operation.Path) is { } state
            ? new ItemMetadata(operation.IsDirectory ? operation.ExpectedSize.Bytes : state.SizeBytes, state.LastWriteTimeUtc, state.CreationTimeUtc, state.Attributes)
            : new ItemMetadata(operation.ExpectedSize.Bytes, operation.ExpectedLastWriteTimeUtc);

    private string? PolicyBlock(string path)
    {
        if (protection.Check(path) is { IsBlocked: true } blocked)
        {
            return blocked.Reason;
        }

        return exclusions.MatchPath(path) is { } exclusion ? $"You excluded '{exclusion.Value}'." : null;
    }

    private static (LedgerItem, ItemOutcome) Failed(ProposedOperation operation, LedgerItem item, string reason) =>
        (item with { Status = ItemStatus.Failed, Error = reason }, new ItemOutcome(operation, ItemStatus.Failed, reason));

    private static ExecutionResult Refuse(IReadOnlyList<ProposedOperation> approved, string reason) =>
        new(ExecutionStatus.Refused, reason, null, approved.Select(o => new ItemOutcome(o, ItemStatus.Skipped, reason)).ToArray());
}

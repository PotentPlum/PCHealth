using PCHealth.Core;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;
using PCHealth.Testing;

namespace PCHealth.Execution.Windows.Tests;

/// <summary>Quarantine, Undo and interruption recovery through the real Windows platform layer, on fixture files only.</summary>
public sealed class UndoEndToEndTests : IDisposable
{
    private static readonly DateTime Written = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    private static readonly DateTime Created = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly TempRoot root = new();
    private readonly string canonical;
    private readonly TransactionLedger ledger;
    private readonly QuarantineStore quarantine;

    public UndoEndToEndTests()
    {
        canonical = new WindowsPathResolver().Resolve(root.Path, LinkHandling.Follow).FinalPath!;
        ledger = TransactionLedger.Open(Path.Combine(canonical, "ledger.db"));
        quarantine = new QuarantineStore(Path.Combine(canonical, "quarantine"));
    }

    [Fact]
    public void Undo_puts_a_quarantined_file_back_with_its_timestamps_and_attributes()
    {
        var file = Create("LocalAppData/OldEditor/settings.json", "{ \"theme\": \"dark\" }");
        File.SetCreationTimeUtc(file, Created);
        File.SetLastWriteTimeUtc(file, Written);
        File.SetAttributes(file, FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.Archive);
        var recommendation = Recommend(file);
        var move = Gateway().Execute(recommendation, recommendation.Operations);
        Assert.Equal(ExecutionStatus.Completed, move.Status);
        Assert.False(File.Exists(file));

        var undo = Restorer().Restore(move.TransactionId!.Value);

        Assert.Equal(TransactionState.Verified, undo.Restore!.State);
        Assert.Equal("{ \"theme\": \"dark\" }", File.ReadAllText(file));
        Assert.Equal(Written, File.GetLastWriteTimeUtc(file));
        Assert.Equal(Created, File.GetCreationTimeUtc(file));
        Assert.Equal(FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.Archive, File.GetAttributes(file));
        Assert.Empty(quarantine.List().Entries);
        Assert.Equal(move.TransactionId, Assert.Single(ledger.RelatedTo(move.TransactionId!.Value)).RelatesTo);
    }

    [Fact]
    public void Undo_puts_a_quarantined_folder_back_with_everything_in_it()
    {
        var folder = Path.Combine(canonical, "LocalAppData", "OldEditor");
        Create("LocalAppData/OldEditor/a.txt", "a");
        Create("LocalAppData/OldEditor/sub/b.txt", "b");
        var recommendation = Recommend(folder, isDirectory: true);
        Assert.Equal(ExecutionStatus.Completed, Gateway().Execute(recommendation, recommendation.Operations).Status);

        var undo = Restorer().Restore(ledger.All().Single().Id);

        Assert.Equal(TransactionState.Verified, undo.Restore!.State);
        Assert.Equal("b", File.ReadAllText(Path.Combine(folder, "sub", "b.txt")));
    }

    [Fact]
    public void A_conflict_at_the_original_path_fails_safely()
    {
        var file = Create("LocalAppData/OldEditor/settings.json", "old");
        var recommendation = Recommend(file);
        var move = Gateway().Execute(recommendation, recommendation.Operations);
        Create("LocalAppData/OldEditor/settings.json", "new, written after the move");

        var undo = Restorer().Restore(move.TransactionId!.Value);

        Assert.Equal(TransactionState.Failed, undo.Restore!.State);
        Assert.Equal(ItemStatus.Skipped, Assert.Single(undo.Restore.Items).Status);
        Assert.Equal("new, written after the move", File.ReadAllText(file));
        Assert.Equal("old", File.ReadAllText(Assert.Single(quarantine.List().Entries).ContentPath));
        Assert.True(Restorer().SupportFor(ledger.Find(move.TransactionId!.Value)!).CanRestore);
    }

    [Fact]
    public void An_interrupted_quarantine_run_is_recovered_at_startup_and_can_be_undone()
    {
        var first = Create("LocalAppData/One/a.txt", "a");
        var second = Create("LocalAppData/Two/b.txt", "b");
        var recommendation = Recommend(first, second);
        var crashing = new CrashingPlatform(new WindowsPlatformOperations(), crashOnMove: 2);
        var gateway = new ExecutionGateway(new WindowsPathResolver(), crashing, new ProtectionPolicy([]), new ExclusionList(), ledger, quarantine, new GlobalPause());

        Assert.Throws<SimulatedCrash>(() => gateway.Execute(recommendation, recommendation.Operations));
        var report = new InterruptedOperations(ledger, quarantine).Resolve();

        var resolved = Assert.Single(report.Resolved);
        Assert.Equal(TransactionState.Failed, resolved.State);
        Assert.Equal(new[] { ItemStatus.Done, ItemStatus.Skipped }, resolved.Items.Select(i => i.Status));
        Assert.True(File.Exists(second));
        Assert.Empty(report.Unaccounted);

        Assert.Equal(TransactionState.Verified, Restorer().Restore(resolved.Id).Restore!.State);
        Assert.Equal("a", File.ReadAllText(first));
    }

    [Fact]
    public void An_interrupted_undo_is_recovered_at_startup_and_the_rest_can_be_undone()
    {
        var first = Create("LocalAppData/One/a.txt", "a");
        var second = Create("LocalAppData/Two/b.txt", "b");
        var recommendation = Recommend(first, second);
        var move = Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Throws<SimulatedCrash>(() => new Restorer(ledger, quarantine, new CrashingMover(crashOnMove: 2)).Restore(move.TransactionId!.Value));
        var resolved = Assert.Single(new InterruptedOperations(ledger, quarantine).Resolve().Resolved);

        Assert.Equal(LedgerOperation.Restore, resolved.Operation);
        Assert.Equal(new[] { ItemStatus.Done, ItemStatus.Skipped }, resolved.Items.Select(i => i.Status));
        Assert.Equal("a", File.ReadAllText(first));
        Assert.False(File.Exists(second));

        Assert.Equal(TransactionState.Verified, Restorer().Restore(move.TransactionId!.Value).Restore!.State);
        Assert.Equal("b", File.ReadAllText(second));
    }

    public void Dispose()
    {
        ledger.Dispose();
        root.Dispose();
    }

    private ExecutionGateway Gateway() =>
        new(new WindowsPathResolver(), new WindowsPlatformOperations(), new ProtectionPolicy([]), new ExclusionList(), ledger, quarantine, new GlobalPause());

    private Restorer Restorer() => new(ledger, quarantine, new HandleMove());

    private static Recommendation Recommend(params string[] files) => Recommend(files, isDirectory: false);

    private static Recommendation Recommend(string path, bool isDirectory) => Recommend([path], isDirectory);

    private static Recommendation Recommend(string[] paths, bool isDirectory) =>
        new RecommendationBuilder(new ProtectionPolicy([]), new ExclusionList()).Build(
        [
            new Finding(
                FindingCategory.AppResidue, "Possible leftover data", "Nothing claims it.",
                paths.Select(p => (Evidence)new PathEvidence(p, "Application data.")).ToArray(),
                new Confidence(ConfidenceLevel.Medium, "No registration claims it."),
                new Impact("Moves it to quarantine."),
                new Recovery(RecoveryMethod.Quarantine, "Restorable while in quarantine."),
                operations: paths.Select(p =>
                {
                    var info = new FileInfo(p);
                    return isDirectory
                        ? new ProposedOperation(OperationKind.MoveToQuarantine, p, isDirectory: true, new MeasuredSize(2))
                        : new ProposedOperation(OperationKind.MoveToQuarantine, p, isDirectory: false, new MeasuredSize(info.Length), info.LastWriteTimeUtc);
                }).ToArray()),
        ]).Recommendations.Single();

    private string Create(string relative, string contents)
    {
        var path = Path.GetFullPath(Path.Combine(canonical, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        File.WriteAllText(path, contents);
        return path;
    }

    private sealed class SimulatedCrash : Exception;

    /// <summary>Stops the "process" on the given move, after the earlier moves really happened.</summary>
    private sealed class CrashingPlatform(IPlatformOperations inner, int crashOnMove) : IPlatformOperations
    {
        private int moves;

        public ItemState? Inspect(string finalPath) => inner.Inspect(finalPath);

        public IReadOnlyList<string> ProcessesUsing(string finalPath) => inner.ProcessesUsing(finalPath);

        public bool NeedsElevation(string finalPath) => inner.NeedsElevation(finalPath);

        public bool SameVolume(string path, string otherPath) => inner.SameVolume(path, otherPath);

        public IReadOnlyList<string> RunningProcesses(IReadOnlyCollection<string> processNames) => inner.RunningProcesses(processNames);

        public string? CannotRecycle(string finalPath, long sizeBytes) => inner.CannotRecycle(finalPath, sizeBytes);

        public string MoveToRecycleBin(string finalPath, bool isDirectory) => inner.MoveToRecycleBin(finalPath, isDirectory);

        public void MoveToQuarantine(string finalPath, string destination, bool isDirectory)
        {
            if (++moves == crashOnMove)
            {
                throw new SimulatedCrash();
            }

            inner.MoveToQuarantine(finalPath, destination, isDirectory);
        }
    }

    private sealed class CrashingMover(int crashOnMove) : IFileMover
    {
        private int moves;

        public void Move(string source, string destination, bool isDirectory)
        {
            if (++moves == crashOnMove)
            {
                throw new SimulatedCrash();
            }

            HandleMove.MoveByHandle(source, destination, isDirectory);
        }
    }
}

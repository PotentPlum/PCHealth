using PCHealth.Core;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;
using PCHealth.Testing;

namespace PCHealth.Execution.Tests;

/// <summary>The gateway pipeline against fixture files, a fake platform layer and a fake path resolver.</summary>
public class ExecutionGatewayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_quarantine_move_runs_every_step_and_is_recorded_and_restorable()
    {
        using var fixture = new Fixture();
        var folder = fixture.Folder("LocalAppData/OldEditor", ("settings.json", "{}"), ("cache.bin", "12345"));
        var recommendation = fixture.Quarantine(folder);

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.False(Directory.Exists(folder));
        var record = fixture.Ledger.Find(result.TransactionId!.Value)!;
        Assert.Equal(TransactionState.Verified, record.State);
        Assert.Equal(LedgerOperation.MoveToQuarantine, record.Operation);
        Assert.Equal(Now.AddDays(14), record.RetentionDeadlineUtc);
        Assert.Contains("not in use by a running process", record.Checks);
        var item = Assert.Single(record.Items);
        Assert.Equal(ItemStatus.Done, item.Status);
        Assert.True(Directory.Exists(item.RecoveryLocation));
        Assert.Equal(SlotState.Stored, Assert.Single(fixture.Store.List().Entries).State);

        var restored = new Restorer(fixture.Ledger, fixture.Store, clock: fixture.Clock).Restore(record.Id);
        Assert.Equal(TransactionState.Verified, restored.Restore!.State);
        Assert.Equal("12345", File.ReadAllText(Path.Combine(folder, "cache.bin")));
    }

    [Fact]
    public void Recycle_bin_moves_are_recorded_per_item()
    {
        using var fixture = new Fixture();
        var a = fixture.File("Temp/a.tmp", "aa");
        var b = fixture.File("Temp/b.tmp", "bbb");
        var recommendation = fixture.Recycle(a, b);

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(2, fixture.Platform.Recycled.Count);
        var record = fixture.Ledger.Find(result.TransactionId!.Value)!;
        Assert.Equal(5, record.ObservedBytes);
        Assert.All(record.Items, i => Assert.True(File.Exists(i.RecoveryLocation)));
        Assert.Null(record.RetentionDeadlineUtc);
    }

    [Fact]
    public void The_global_pause_blocks_all_execution()
    {
        using var fixture = new Fixture();
        var a = fixture.File("Temp/a.tmp", "a");
        var recommendation = fixture.Recycle(a);
        fixture.Pause.Pause();

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Refused, result.Status);
        Assert.Contains("paused", result.Summary);
        Assert.True(File.Exists(a));
        Assert.Empty(fixture.Ledger.All());
    }

    [Fact]
    public void Pausing_during_a_run_stops_the_items_still_queued()
    {
        using var fixture = new Fixture();
        var a = fixture.File("Temp/a.tmp", "a");
        var b = fixture.File("Temp/b.tmp", "b");
        var recommendation = fixture.Recycle(a, b);
        fixture.Platform.AfterMove = fixture.Pause.Pause;

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.PartiallyCompleted, result.Status);
        Assert.Contains("paused", result.Items[1].Reason);
        Assert.True(File.Exists(b));
        Assert.Equal(TransactionState.Failed, fixture.Ledger.Find(result.TransactionId!.Value)!.State);
    }

    [Fact]
    public void Review_recommendations_and_mismatched_approvals_are_refused()
    {
        using var fixture = new Fixture();
        var a = fixture.File("Temp/a.tmp", "a");
        var other = fixture.File("Temp/other.tmp", "o");
        var recommendation = fixture.Recycle(a);
        var review = fixture.Quarantine(fixture.Folder("LocalAppData/Odd", ("x", "x")), reviewOnly: true);
        var gateway = fixture.Gateway();

        Assert.Equal(ExecutionStatus.Refused, gateway.Execute(review, review.Operations).Status);
        Assert.Equal(ExecutionStatus.Refused, gateway.Execute(recommendation, [Fixture.Op(other)]).Status);
        Assert.Equal(ExecutionStatus.Refused, gateway.Execute(recommendation, []).Status);
        Assert.Equal(ExecutionStatus.Refused, gateway.Execute(recommendation, [recommendation.Operations[0], recommendation.Operations[0]]).Status);
        Assert.True(File.Exists(a));
        Assert.Empty(fixture.Ledger.All());
    }

    [Fact]
    public void Only_the_approved_subset_is_changed()
    {
        using var fixture = new Fixture();
        var a = fixture.File("Temp/a.tmp", "a");
        var b = fixture.File("Temp/b.tmp", "b");
        var recommendation = fixture.Recycle(a, b);

        var result = fixture.Gateway().Execute(recommendation, [recommendation.Operations[1]]);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.True(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void Protections_and_exclusions_added_since_the_recommendation_are_enforced()
    {
        using var fixture = new Fixture();
        var a = fixture.File("Temp/keep/a.tmp", "a");
        var b = fixture.File("Temp/b.tmp", "b");
        var recommendation = fixture.Recycle(a, b);
        fixture.Exclusions.ExcludeFolder(fixture.Root.Resolve("Temp/keep"), Now);

        var partly = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.PartiallyCompleted, partly.Status);
        Assert.StartsWith("You excluded", partly.Items[0].Reason);
        Assert.True(File.Exists(a));

        var c = fixture.File("Temp/c.tmp", "c");
        var protectedAll = fixture.Recycle(c);
        var protection = new ProtectionPolicy([new(fixture.Root.Resolve("Temp"), ProtectionKind.PersonalFolder, "test protection.")]);
        var refused = fixture.Gateway(protection).Execute(protectedAll, protectedAll.Operations);

        Assert.Equal(ExecutionStatus.Refused, refused.Status);
        Assert.Null(refused.TransactionId);
        Assert.True(File.Exists(c));
    }

    [Fact]
    public void An_item_that_changed_since_it_was_found_is_left_alone()
    {
        using var fixture = new Fixture();
        var a = fixture.File("Temp/a.tmp", "a");
        var recommendation = fixture.Recycle(a);
        File.WriteAllText(a, "grown since the scan");

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("changed since it was found", result.Items[0].Reason);
        Assert.Equal("grown since the scan", File.ReadAllText(a));
    }

    [Fact]
    public void A_path_that_now_resolves_elsewhere_is_refused()
    {
        using var fixture = new Fixture();
        var a = fixture.File("Temp/a.tmp", "a");
        var recommendation = fixture.Recycle(a);
        fixture.Resolver.Redirects[a] = fixture.File("Documents/a.tmp", "a");

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("now leads to", result.Items[0].Reason);
        Assert.Empty(fixture.Platform.Recycled);
    }

    [Fact]
    public void Links_are_never_changed()
    {
        using var fixture = new Fixture();
        var target = fixture.File("elsewhere/real.tmp", "r");
        var link = Path.Combine(fixture.Root.CreateDirectory("Temp"), "link.tmp");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Creating a symbolic link needs a privilege this account does not have.");
        }

        var recommendation = fixture.Recycle(link);

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("link", result.Items[0].Reason);
        Assert.True(File.Exists(target));
    }

    [Theory]
    [InlineData("in use", "It is in use by Editor.exe.")]
    [InlineData("elevation", "Changing it needs administrator rights, not supported yet.")]
    [InlineData("volume", "It is on a different drive from the quarantine folder, so it cannot be quarantined.")]
    public void State_checks_right_before_acting_refuse_the_item(string condition, string reason)
    {
        using var fixture = new Fixture();
        var folder = fixture.Folder("LocalAppData/OldEditor", ("a", "a"));
        switch (condition)
        {
            case "in use":
                fixture.Platform.InUse[folder] = ["Editor.exe"];
                break;
            case "elevation":
                fixture.Platform.Elevated.Add(folder);
                break;
            default:
                fixture.Platform.SameVolumeResult = false;
                break;
        }

        var recommendation = fixture.Quarantine(folder);
        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(reason, result.Items[0].Reason);
        Assert.True(Directory.Exists(folder));
        Assert.Empty(fixture.Store.List().Entries);
    }

    [Fact]
    public void An_item_the_recycle_bin_cannot_take_is_refused_before_acting()
    {
        using var fixture = new Fixture();
        var a = fixture.File("Temp/a.tmp", "a");
        fixture.Platform.Unrecyclable = "this drive has no Recycle Bin.";
        var recommendation = fixture.Recycle(a);

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("The Recycle Bin is not suitable for it: this drive has no Recycle Bin.", result.Items[0].Reason);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void A_permanent_deletion_instead_of_recycling_is_reported_as_such()
    {
        using var fixture = new Fixture();
        var a = fixture.File("Temp/a.tmp", "a");
        fixture.Platform.NukeInstead = true;
        var recommendation = fixture.Recycle(a);

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.StartsWith("It was deleted permanently", result.Items[0].Reason);
        var item = Assert.Single(fixture.Ledger.Find(result.TransactionId!.Value)!.Items);
        Assert.Equal(ItemStatus.Failed, item.Status);
        Assert.Null(item.RecoveryLocation);
    }

    [Fact]
    public void A_check_that_cannot_be_made_refuses_the_item()
    {
        using var fixture = new Fixture();
        var a = fixture.File("Temp/a.tmp", "a");
        fixture.Platform.CannotCheckUse = true;
        var recommendation = fixture.Recycle(a);

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("It could not be checked, so it was left alone: The Restart Manager is unavailable.", result.Items[0].Reason);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void A_failed_move_changes_nothing_and_frees_its_slot()
    {
        using var fixture = new Fixture();
        var folder = fixture.Folder("LocalAppData/OldEditor", ("a", "a"));
        fixture.Platform.FailMoves = true;
        var recommendation = fixture.Quarantine(folder);

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.StartsWith("It could not be moved", result.Items[0].Reason);
        Assert.True(Directory.Exists(folder));
        Assert.Empty(fixture.Store.List().Entries);
        Assert.Equal(TransactionState.Failed, fixture.Ledger.Find(result.TransactionId!.Value)!.State);
    }

    [Fact]
    public void A_move_that_reports_success_but_did_nothing_fails_verification()
    {
        using var fixture = new Fixture();
        var a = fixture.File("Temp/a.tmp", "a");
        fixture.Platform.PretendMoves = true;
        var recommendation = fixture.Recycle(a);

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("It is still at its original path after the move.", result.Items[0].Reason);
    }

    [Fact]
    public void A_run_interrupted_mid_way_is_found_pending_and_resolved_at_startup()
    {
        using var fixture = new Fixture();
        var first = fixture.Folder("LocalAppData/One", ("a", "a"));
        var second = fixture.Folder("LocalAppData/Two", ("b", "b"));
        var recommendation = fixture.QuarantineMany(first, second);
        fixture.Platform.CrashOnMove = 2;

        Assert.Throws<SimulatedCrash>(() => fixture.Gateway().Execute(recommendation, recommendation.Operations));
        var pending = Assert.Single(fixture.Ledger.InState(TransactionState.Pending));
        Assert.Equal(ItemStatus.Done, pending.Items[0].Status);

        var resolved = Assert.Single(new InterruptedOperations(fixture.Ledger, fixture.Store).Resolve().Resolved);

        Assert.Equal(TransactionState.Failed, resolved.State);
        Assert.Equal(ItemStatus.Done, resolved.Items[0].Status);
        Assert.Equal(ItemStatus.Skipped, resolved.Items[1].Status);
        Assert.True(Directory.Exists(second));
    }

    [Fact]
    public void The_pause_survives_a_restart_and_reports_changes()
    {
        using var root = new TempRoot();
        var path = root.Resolve("state/paused");
        var pause = new GlobalPause(path);
        var changes = 0;
        pause.Changed += (_, _) => changes++;

        pause.Pause();
        pause.Pause();

        Assert.True(new GlobalPause(path).IsPaused);
        pause.Resume();
        Assert.False(new GlobalPause(path).IsPaused);
        Assert.Equal(2, changes);
    }

    private sealed class SimulatedCrash : Exception;

    private sealed class FakeResolver : IPathResolver
    {
        public Dictionary<string, string> Redirects { get; } = [];

        public PathResolution Resolve(string path, LinkHandling links = LinkHandling.DoNotFollow)
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return PathResolution.Rejected(PathRejectionReason.NotFullyQualified, "Not a full path.");
            }

            var final = Redirects.GetValueOrDefault(path, path);
            var info = new FileInfo(final);
            if (!info.Exists && !Directory.Exists(final) && info.LinkTarget is null)
            {
                return PathResolution.Rejected(PathRejectionReason.NotFound, "Not found.");
            }

            if (links == LinkHandling.Follow && info.LinkTarget is not null)
            {
                final = info.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
            }

            return PathResolution.Resolved(final, File.GetAttributes(final));
        }
    }

    /// <summary>Acts on the fixture files with plain moves; the Recycle Bin is a folder in the fixture.</summary>
    private sealed class FakePlatform(TempRoot root) : IPlatformOperations
    {
        private int moves;

        public Dictionary<string, IReadOnlyList<string>> InUse { get; } = [];

        public HashSet<string> Elevated { get; } = [];

        public List<string> Recycled { get; } = [];

        public bool SameVolumeResult { get; set; } = true;

        public bool FailMoves { get; set; }

        public bool PretendMoves { get; set; }

        public int? CrashOnMove { get; set; }

        public Action? AfterMove { get; set; }

        public ItemState? Inspect(string finalPath)
        {
            if (Directory.Exists(finalPath))
            {
                var d = new DirectoryInfo(finalPath);
                return new ItemState(true, 0, d.LastWriteTimeUtc, d.CreationTimeUtc, d.Attributes);
            }

            var f = new FileInfo(finalPath);
            return f.Exists ? new ItemState(false, f.Length, f.LastWriteTimeUtc, f.CreationTimeUtc, f.Attributes) : null;
        }

        public bool CannotCheckUse { get; set; }

        public IReadOnlyList<string> ProcessesUsing(string finalPath) =>
            CannotCheckUse ? throw new IOException("The Restart Manager is unavailable.") : InUse.GetValueOrDefault(finalPath, []);

        public bool NeedsElevation(string finalPath) => Elevated.Contains(finalPath);

        public bool SameVolume(string path, string otherPath) => SameVolumeResult;

        public string? Unrecyclable { get; set; }

        public bool NukeInstead { get; set; }

        public string? CannotRecycle(string finalPath, long sizeBytes) => Unrecyclable;

        public string MoveToRecycleBin(string finalPath, bool isDirectory)
        {
            var destination = root.Resolve($"recycle/{Guid.NewGuid():N}");
            if (NukeInstead)
            {
                System.IO.File.Delete(finalPath);
                throw new PermanentlyDeletedException("The drive has no Recycle Bin.");
            }

            Move(finalPath, destination, isDirectory, Recycled);
            return destination;
        }

        public void MoveToQuarantine(string finalPath, string destination, bool isDirectory) => Move(finalPath, destination, isDirectory, null);

        private void Move(string source, string destination, bool isDirectory, List<string>? log)
        {
            if (++moves == CrashOnMove)
            {
                throw new SimulatedCrash();
            }

            if (FailMoves)
            {
                throw new IOException("The device is not ready.");
            }

            if (!PretendMoves)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                new PlainFileMover().Move(source, destination, isDirectory);
                log?.Add(source);
            }

            AfterMove?.Invoke();
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Platform = new FakePlatform(Root);
            Store = new QuarantineStore(Root.Resolve("quarantine"), Clock);
            Ledger = TransactionLedger.Open(Root.Resolve("ledger.db"));
            Pause = new GlobalPause();
        }

        public TempRoot Root { get; } = new();

        public ManualClock Clock { get; } = new(Now);

        public FakeResolver Resolver { get; } = new();

        public FakePlatform Platform { get; }

        public QuarantineStore Store { get; }

        public TransactionLedger Ledger { get; }

        public GlobalPause Pause { get; }

        public ExclusionList Exclusions { get; } = new();

        public static ProposedOperation Op(string path)
        {
            var info = new FileInfo(path);
            return new ProposedOperation(OperationKind.MoveToRecycleBin, path, isDirectory: false, new MeasuredSize(info.Exists ? info.Length : 0), info.Exists ? info.LastWriteTimeUtc : null);
        }

        public ExecutionGateway Gateway(ProtectionPolicy? protection = null) =>
            new(Resolver, Platform, protection ?? new ProtectionPolicy([]), Exclusions, Ledger, Store, Pause, Clock);

        public string File(string relative, string contents) => Root.CreateFile(relative, contents);

        public string Folder(string relative, params (string Name, string Contents)[] files)
        {
            var folder = Root.CreateDirectory(relative);
            foreach (var (name, contents) in files)
            {
                Root.CreateFile($"{relative}/{name}", contents);
            }

            return folder;
        }

        public Recommendation Recycle(params string[] paths) => Build(new Finding(
            FindingCategory.Cache, "Temporary files", "They are regenerable.",
            [new PathEvidence(Path.GetDirectoryName(paths[0])!, "Known location.")],
            new Confidence(ConfidenceLevel.High, "Known location."),
            new Impact($"Moves {paths.Length} files to the Recycle Bin."),
            new Recovery(RecoveryMethod.RecycleBin, "Restorable until the Recycle Bin is emptied."),
            operations: paths.Select(Op).ToArray()));

        public Recommendation Quarantine(string folder, bool reviewOnly = false) => QuarantineMany([folder], reviewOnly);

        public Recommendation QuarantineMany(string[] folders, bool reviewOnly = false) => Build(new Finding(
            FindingCategory.AppResidue, "Possible leftover data", "Nothing claims it.",
            folders.Select(f => (Evidence)new PathEvidence(f, "Application data folder.")).ToArray(),
            new Confidence(reviewOnly ? ConfidenceLevel.Low : ConfidenceLevel.Medium, "No registration claims it."),
            new Impact("Moves the folders to quarantine."),
            new Recovery(RecoveryMethod.Quarantine, "Restorable while in quarantine."),
            reviewOnly,
            folders.Select(f => new ProposedOperation(OperationKind.MoveToQuarantine, f, isDirectory: true, new MeasuredSize(10))).ToArray()));

        public Recommendation QuarantineMany(params string[] folders) => QuarantineMany(folders, false);

        public void Dispose()
        {
            Ledger.Dispose();
            Root.Dispose();
        }

        private static Recommendation Build(Finding finding) =>
            new RecommendationBuilder(new ProtectionPolicy([]), new ExclusionList()).Build([finding]).Recommendations.Single();
    }
}

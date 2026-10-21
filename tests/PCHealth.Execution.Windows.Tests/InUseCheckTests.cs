using PCHealth.Core;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;
using PCHealth.Testing;

namespace PCHealth.Execution.Windows.Tests;

/// <summary>Holds fixture files open from this test process and checks that changes to them are blocked.</summary>
public sealed class InUseCheckTests : IDisposable
{
    private readonly TempRoot root = new();
    private readonly string canonical;

    public InUseCheckTests()
    {
        canonical = new WindowsPathResolver().Resolve(root.Path, LinkHandling.Follow).FinalPath!;
    }

    [Fact]
    public void A_file_held_open_is_reported_with_the_process_holding_it()
    {
        var file = Create("Temp/held.tmp", "x");

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.NotEmpty(InUseCheck.ProcessesUsing(file, isDirectory: false));
        }

        Assert.Empty(InUseCheck.ProcessesUsing(file, isDirectory: false));
    }

    [Fact]
    public void A_folder_is_in_use_when_any_file_inside_is_held_open()
    {
        var file = Create("Temp/cache/deep/held.bin", "x");
        Create("Temp/cache/free.bin", "y");
        var folder = Path.Combine(canonical, "Temp", "cache");

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.NotEmpty(InUseCheck.ProcessesUsing(folder, isDirectory: true));
        }

        Assert.Empty(InUseCheck.ProcessesUsing(folder, isDirectory: true));
    }

    [Fact]
    public void The_gateway_blocks_a_change_to_a_file_held_open_by_a_running_process()
    {
        var file = Create("Temp/held.tmp", "keep me");
        using var ledger = TransactionLedger.Open(Path.Combine(canonical, "ledger.db"));
        var gateway = new ExecutionGateway(
            new WindowsPathResolver(), new WindowsPlatformOperations(), new ProtectionPolicy([]), new ExclusionList(), ledger,
            new QuarantineStore(Path.Combine(canonical, "quarantine")), new GlobalPause());
        var info = new FileInfo(file);
        var recommendation = new RecommendationBuilder(new ProtectionPolicy([]), new ExclusionList()).Build(
        [
            new Finding(
                FindingCategory.Cache, "Temporary files", "They are regenerable.",
                [new PathEvidence(Path.GetDirectoryName(file)!, "Known location.")],
                new Confidence(ConfidenceLevel.High, "Known location."),
                new Impact("Moves 1 file to quarantine."),
                new Recovery(RecoveryMethod.Quarantine, "Restorable while in quarantine."),
                operations: [new ProposedOperation(OperationKind.MoveToQuarantine, file, isDirectory: false, new MeasuredSize(info.Length), info.LastWriteTimeUtc)]),
        ]).Recommendations.Single();

        ExecutionResult result;
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            result = gateway.Execute(recommendation, recommendation.Operations);
        }

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.StartsWith("It is in use by", result.Items[0].Reason);
        Assert.Equal("keep me", File.ReadAllText(file));
        Assert.Equal(ItemStatus.Skipped, Assert.Single(ledger.Find(result.TransactionId!.Value)!.Items).Status);
    }

    [Fact]
    public void Fixture_files_need_no_elevation_and_share_the_quarantine_volume()
    {
        var file = Create("Temp/a.tmp", "a");
        var platform = new WindowsPlatformOperations();

        Assert.False(platform.NeedsElevation(file));
        Assert.True(platform.SameVolume(file, Path.Combine(canonical, "quarantine", "not-yet-created")));
        Assert.Null(platform.Inspect(Path.Combine(canonical, "missing.tmp")));
        Assert.Equal(1, platform.Inspect(file)!.SizeBytes);
    }

    public void Dispose() => root.Dispose();

    private string Create(string relative, string contents)
    {
        var path = Path.GetFullPath(Path.Combine(canonical, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }
}

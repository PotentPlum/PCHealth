using PCHealth.Core;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;
using PCHealth.Testing;

namespace PCHealth.Execution.Windows.Tests;

/// <summary>
/// Sends fixture files to the real Recycle Bin, so these run only where <c>PCHEALTH_RECYCLE_BIN_TESTS=1</c> is set:
/// the CI Windows job (D-017). Each test removes its own items from the Recycle Bin afterwards.
/// </summary>
public sealed class WindowsRecycleBinTests : IDisposable
{
    private readonly TempRoot root = new();
    private readonly List<string> recycled = [];

    [Fact]
    public void A_fixture_file_goes_to_the_recycle_bin_and_is_found_there()
    {
        RequireOptIn();
        var file = root.CreateFile("Temp/old.tmp", "recycle me");

        var location = Recycle(file, isDirectory: false);

        Assert.False(File.Exists(file));
        Assert.Equal("recycle me", File.ReadAllText(location));
        Assert.StartsWith(WindowsRecycleBin.RecycleFolder(file)!, location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_fixture_folder_goes_to_the_recycle_bin()
    {
        RequireOptIn();
        var folder = root.CreateDirectory("Temp/cache");
        root.CreateFile("Temp/cache/a.bin", "a");

        var location = Recycle(folder, isDirectory: true);

        Assert.False(Directory.Exists(folder));
        Assert.Equal("a", File.ReadAllText(Path.Combine(location, "a.bin")));
    }

    [Fact]
    public void The_gateway_records_a_recycle_bin_move_in_the_ledger()
    {
        RequireOptIn();
        var file = root.CreateFile("Temp/old.tmp", "abc");
        using var ledger = TransactionLedger.Open(root.Resolve("ledger.db"));
        var gateway = new ExecutionGateway(
            new WindowsPathResolver(), new RecycleOnlyPlatform(), new ProtectionPolicy([]), new ExclusionList(), ledger,
            new QuarantineStore(root.Resolve("quarantine")), new GlobalPause());
        var final = new WindowsPathResolver().Resolve(file).FinalPath!;
        var info = new FileInfo(final);
        var recommendation = new RecommendationBuilder(new ProtectionPolicy([]), new ExclusionList()).Build(
        [
            new Finding(
                FindingCategory.Cache, "Temporary files", "They are regenerable.",
                [new PathEvidence(Path.GetDirectoryName(final)!, "Known location.")],
                new Confidence(ConfidenceLevel.High, "Known location."),
                new Impact("Moves 1 file to the Recycle Bin."),
                new Recovery(RecoveryMethod.RecycleBin, "Restorable until the Recycle Bin is emptied."),
                operations: [new ProposedOperation(OperationKind.MoveToRecycleBin, final, isDirectory: false, new MeasuredSize(info.Length), info.LastWriteTimeUtc)]),
        ]).Recommendations.Single();

        var result = gateway.Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        var record = ledger.Find(result.TransactionId!.Value)!;
        Assert.Equal(LedgerOperation.MoveToRecycleBin, record.Operation);
        Assert.Equal(TransactionState.Verified, record.State);
        var item = Assert.Single(record.Items);
        Assert.Equal(ItemStatus.Done, item.Status);
        recycled.Add(item.RecoveryLocation!);
        Assert.Equal("abc", File.ReadAllText(item.RecoveryLocation!));
    }

    [Fact]
    public void A_recycled_file_is_put_back_and_its_record_removed()
    {
        RequireOptIn();
        var file = new WindowsPathResolver().Resolve(root.CreateFile("Temp/photo.jpg", "put me back"), LinkHandling.Follow).FinalPath!;
        var location = Recycle(file, isDirectory: false);
        var bin = new WindowsRecycleBinRestore();
        var record = Path.Combine(Path.GetDirectoryName(location)!, "$I" + Path.GetFileName(location)[2..]);

        Assert.True(bin.Holds(location, file));
        Assert.False(bin.Holds(location, root.Resolve("Temp/other.jpg")));
        bin.Restore(location, file, isDirectory: false);

        Assert.Equal("put me back", File.ReadAllText(file));
        Assert.False(File.Exists(location));
        Assert.False(File.Exists(record));
        Assert.False(bin.Holds(location, file));
        Assert.Throws<IOException>(() => bin.Restore(location, file, isDirectory: false));
    }

    [Fact]
    public void A_local_fixed_drive_is_suitable_and_other_places_are_not()
    {
        Assert.Null(WindowsRecycleBin.Unsuitable(root.Resolve("a.tmp"), 10));
        Assert.NotNull(WindowsRecycleBin.Unsuitable(@"\\server\share\a.tmp", 10));
        var unused = Enumerable.Range('D', 23).Select(c => $"{(char)c}:\\").FirstOrDefault(d => !Directory.Exists(d));
        if (unused is not null)
        {
            Assert.NotNull(WindowsRecycleBin.Unsuitable(Path.Combine(unused, "a.tmp"), 10));
        }
    }

    public void Dispose()
    {
        foreach (var location in recycled)
        {
            // Remove only this test's own item and its $I record from the Recycle Bin.
            var record = Path.Combine(Path.GetDirectoryName(location)!, "$I" + Path.GetFileName(location)[2..]);
            if (Directory.Exists(location))
            {
                Directory.Delete(location, recursive: true);
            }
            else if (File.Exists(location))
            {
                File.Delete(location);
            }

            if (File.Exists(record))
            {
                File.Delete(record);
            }
        }

        root.Dispose();
    }

    private static void RequireOptIn()
    {
        if (Environment.GetEnvironmentVariable("PCHEALTH_RECYCLE_BIN_TESTS") != "1")
        {
            Assert.Skip("Uses the real Recycle Bin; set PCHEALTH_RECYCLE_BIN_TESTS=1 to run (the CI Windows job does, D-017).");
        }
    }

    private string Recycle(string path, bool isDirectory)
    {
        var location = WindowsRecycleBin.Recycle(path, isDirectory);
        recycled.Add(location);
        return location;
    }

    private sealed class RecycleOnlyPlatform : IPlatformOperations
    {
        public ItemState? Inspect(string finalPath)
        {
            var f = new FileInfo(finalPath);
            return f.Exists ? new ItemState(false, f.Length, f.LastWriteTimeUtc, f.CreationTimeUtc, f.Attributes) : null;
        }

        public IReadOnlyList<string> ProcessesUsing(string finalPath) => [];

        public bool NeedsElevation(string finalPath) => false;

        public bool SameVolume(string path, string otherPath) => true;

        public string? CannotRecycle(string finalPath, long sizeBytes) => WindowsRecycleBin.Unsuitable(finalPath, sizeBytes);

        public string MoveToRecycleBin(string finalPath, bool isDirectory) => WindowsRecycleBin.Recycle(finalPath, isDirectory);

        public void MoveToQuarantine(string finalPath, string destination, bool isDirectory) => throw new IOException("Not used here.");
    }
}

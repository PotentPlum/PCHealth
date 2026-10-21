using System.Diagnostics;
using PCHealth.Core;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;
using PCHealth.Testing;

namespace PCHealth.Execution.Windows.Tests;

/// <summary>Moves by handle on fixture trees only, including targets swapped for junctions between check and act.</summary>
public class HandleMoveTests
{
    [Fact]
    public void A_file_moves_by_handle_with_its_contents_and_timestamps()
    {
        using var fixture = new Fixture();
        var source = fixture.File("data/a.log", "payload");
        var written = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, written);
        var destination = Path.Combine(fixture.Directory("quarantine/slot"), "content");

        HandleMove.MoveByHandle(source, destination, isDirectory: false);

        Assert.False(File.Exists(source));
        Assert.Equal("payload", File.ReadAllText(destination));
        Assert.Equal(written, File.GetLastWriteTimeUtc(destination));
    }

    [Fact]
    public void A_folder_moves_by_handle_with_everything_in_it()
    {
        using var fixture = new Fixture();
        var source = fixture.Directory("data/app");
        fixture.File("data/app/sub/x.txt", "x");
        var destination = Path.Combine(fixture.Directory("quarantine/slot"), "content");

        HandleMove.MoveByHandle(source, destination, isDirectory: true);

        Assert.False(Directory.Exists(source));
        Assert.Equal("x", File.ReadAllText(Path.Combine(destination, "sub", "x.txt")));
    }

    [Fact]
    public void A_target_swapped_for_a_junction_is_refused_and_nothing_moves()
    {
        using var fixture = new Fixture();
        var target = fixture.Directory("data/app");
        var elsewhere = fixture.Directory("personal");
        var precious = fixture.File("personal/precious.txt", "keep");
        Directory.Delete(target);
        Junction(target, elsewhere);
        var destination = Path.Combine(fixture.Directory("quarantine/slot"), "content");

        var error = Assert.Throws<IOException>(() => HandleMove.MoveByHandle(target, destination, isDirectory: true));

        Assert.Contains("link", error.Message);
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("keep", File.ReadAllText(precious));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void A_parent_swapped_for_a_junction_is_refused()
    {
        using var fixture = new Fixture();
        var parent = fixture.Directory("data/app");
        fixture.File("personal/report.txt", "keep");
        Directory.Delete(parent);
        Junction(parent, fixture.Resolve("personal"));
        var destination = Path.Combine(fixture.Directory("quarantine/slot"), "content");

        var error = Assert.Throws<IOException>(() => HandleMove.MoveByHandle(Path.Combine(parent, "report.txt"), destination, isDirectory: false));

        Assert.Contains("now leads to", error.Message);
        Assert.Equal("keep", File.ReadAllText(fixture.Resolve("personal/report.txt")));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void An_existing_destination_is_never_overwritten()
    {
        using var fixture = new Fixture();
        var source = fixture.File("data/a.txt", "new");
        var destination = fixture.File("quarantine/slot/content", "old");

        Assert.Throws<IOException>(() => HandleMove.MoveByHandle(source, destination, isDirectory: false));

        Assert.Equal("old", File.ReadAllText(destination));
        Assert.Equal("new", File.ReadAllText(source));
    }

    [Fact]
    public void The_wrong_kind_of_item_is_refused()
    {
        using var fixture = new Fixture();
        var folder = fixture.Directory("data/app");
        var destination = Path.Combine(fixture.Directory("quarantine/slot"), "content");

        Assert.Throws<IOException>(() => HandleMove.MoveByHandle(folder, destination, isDirectory: false));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void The_gateway_refuses_a_target_swapped_for_a_junction_between_check_and_act()
    {
        using var fixture = new Fixture();
        var target = fixture.Directory("LocalAppData/OldEditor");
        fixture.File("LocalAppData/OldEditor/settings.json", "{}");
        var elsewhere = fixture.Directory("personal");
        var precious = fixture.File("personal/precious.txt", "keep");
        var platform = new SwappingPlatform(() =>
        {
            // Runs after every gateway check has passed, right before the move: the classic TOCTOU race.
            Directory.Move(target, fixture.Resolve("moved-away"));
            Junction(target, elsewhere);
        });
        using var ledger = TransactionLedger.Open(fixture.Resolve("ledger.db"));
        var quarantine = new QuarantineStore(fixture.Resolve("quarantine"));
        var gateway = new ExecutionGateway(
            new WindowsPathResolver(), platform, new ProtectionPolicy([]), new ExclusionList(), ledger, quarantine, new GlobalPause());
        var recommendation = Recommend(target);

        var result = gateway.Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("link", result.Items[0].Reason);
        Assert.Equal("keep", File.ReadAllText(precious));
        Assert.Equal(precious, Assert.Single(Directory.EnumerateFileSystemEntries(fixture.Resolve("personal"))));
        Assert.Empty(quarantine.List().Entries);
        Assert.Equal(TransactionState.Failed, ledger.Find(result.TransactionId!.Value)!.State);
    }

    private static Recommendation Recommend(string folder) =>
        new RecommendationBuilder(new ProtectionPolicy([]), new ExclusionList()).Build(
        [
            new Finding(
                FindingCategory.AppResidue, "Possible leftover data", "Nothing claims it.",
                [new PathEvidence(folder, "Application data folder.")],
                new Confidence(ConfidenceLevel.Medium, "No registration claims it."),
                new Impact("Moves the folder to quarantine."),
                new Recovery(RecoveryMethod.Quarantine, "Restorable while in quarantine."),
                operations: [new ProposedOperation(OperationKind.MoveToQuarantine, folder, isDirectory: true, new MeasuredSize(2))]),
        ]).Recommendations.Single();

    private static void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /J failed: {error}");
    }

    /// <summary>A platform layer whose only real action is the handle move; it swaps the target first.</summary>
    private sealed class SwappingPlatform(Action swap) : IPlatformOperations
    {
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

        public IReadOnlyList<string> ProcessesUsing(string finalPath) => [];

        public bool NeedsElevation(string finalPath) => false;

        public bool SameVolume(string path, string otherPath) => true;

        public string? CannotRecycle(string finalPath, long sizeBytes) => "not used here.";

        public string MoveToRecycleBin(string finalPath, bool isDirectory) => throw new IOException("Not used here.");

        public void MoveToQuarantine(string finalPath, string destination, bool isDirectory)
        {
            swap();
            HandleMove.MoveByHandle(finalPath, destination, isDirectory);
        }
    }

    /// <summary>A temp root whose paths are canonical (no 8.3 names), as the gateway's final paths are.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly TempRoot root = new();
        private readonly string canonical;

        public Fixture()
        {
            canonical = new WindowsPathResolver().Resolve(root.Path, LinkHandling.Follow).FinalPath
                ?? throw new InvalidOperationException("The temp root could not be resolved.");
        }

        public string Resolve(string relative)
        {
            var path = Path.GetFullPath(Path.Combine(canonical, relative));
            if (!path.StartsWith(canonical + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Path '{path}' is outside the test root '{canonical}'.", nameof(relative));
            }

            return path;
        }

        public string Directory(string relative)
        {
            var path = Resolve(relative);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string File(string relative, string contents)
        {
            var path = Resolve(relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose() => root.Dispose();
    }
}

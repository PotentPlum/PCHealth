using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Core;
using PCHealth.Execution;
using PCHealth.Inventory.Compression;
using PCHealth.Ledger;
using PCHealth.Maintenance;
using PCHealth.Organization;
using PCHealth.Policy;
using PCHealth.Testing;

namespace PCHealth.App.Tests;

/// <summary>
/// The app's real composition with only the Windows file layer swapped for plain moves on fixture files: temp paths
/// may contain 8.3 names, which the handle-based Windows layer rightly refuses to treat as final paths. The Recycle
/// Bin is a folder in the fixture.
/// </summary>
internal static class TestServices
{
    public static ServiceProvider Build(TempRoot root) =>
        AppServices.Build(new AppPaths(root.Resolve("PCHealth")), s =>
        {
            s.AddSingleton<IPathResolver>(new PassThroughResolver());
            s.AddSingleton<IPlatformOperations>(new PlainPlatform(root));
            s.AddSingleton(new ProtectionPolicy([]));
            s.AddSingleton<IOrganizeFolders>(new FixtureFolders(root));
            s.AddSingleton<IFolderPicker>(new FixturePicker());
            s.AddSingleton<IChunkCompressor>(new DeflateChunks());
            s.AddSingleton<IFileCompression>(new FakeCompression());
            s.AddSingleton<IStartupSources>(new FakeStartupSources());
            s.AddSingleton<Func<ResourceObserver>>(sp => () =>
            {
                var clock = new ManualClock(DateTimeOffset.UtcNow);
                return new ResourceObserver(new FakeSampler(), clock, (step, _) => { clock.Advance(step); return Task.CompletedTask; });
            });
            s.AddSingleton<Func<BootTiming>>(() => BootTiming.Unavailable("Boot timing is not available without administrator rights."));
            s.AddSingleton(sp => new Restorer(
                sp.GetRequiredService<TransactionLedger>(),
                sp.GetRequiredService<QuarantineStore>(),
                new PlainFileMover(),
                sp.GetRequiredService<TimeProvider>(),
                new FixtureRecycleBin(),
                sp.GetRequiredService<IFileCompression>()));
        });

    internal sealed class PassThroughResolver : IPathResolver
    {
        public PathResolution Resolve(string path, LinkHandling links = LinkHandling.DoNotFollow) =>
            System.IO.File.Exists(path) || Directory.Exists(path)
                ? PathResolution.Resolved(path, System.IO.File.GetAttributes(path))
                : PathResolution.Rejected(PathRejectionReason.NotFound, "Not found.");
    }

    /// <summary>Deflate in 8 KB chunks, standing in for XPRESS so estimates don't depend on the runner.</summary>
    internal sealed class DeflateChunks : IChunkCompressor
    {
        public CompressionAlgorithm Algorithm => CompressionAlgorithm.Xpress8K;

        public int ChunkBytes => 8192;

        public int CompressedSize(ReadOnlySpan<byte> chunk)
        {
            using var output = new MemoryStream();
            using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                deflate.Write(chunk);
            }

            return (int)output.Length;
        }
    }

    /// <summary>Compression as bookkeeping: a compressed file takes a quarter of its size on disk.</summary>
    internal sealed class FakeCompression : IFileCompression
    {
        public HashSet<string> Compressed { get; } = [];

        public string? CannotCompress(string path) => null;

        public bool IsCompressed(string path) => Compressed.Contains(path);

        public long SizeOnDisk(string path) => new FileInfo(path).Length / (IsCompressed(path) ? 4 : 1);

        public long FreeSpace(string path) => long.MaxValue;

        public void Compress(string path) => Compressed.Add(path);

        public void Decompress(string path) => Compressed.Remove(path);
    }

    /// <summary>Startup items as plain data: a Run entry, a task and a service, and one place that can't be read.</summary>
    internal sealed class FakeStartupSources : IStartupSources
    {
        private const string UpdaterTask =
            "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Triggers><LogonTrigger /></Triggers>"
            + "<Actions><Exec><Command>C:\\Apps\\Vendor\\update.exe</Command></Exec></Actions></Task>";

        public IReadOnlyList<(string Name, string Command)> RunValues(RunKey key) =>
            key == new RunKey(Hive.CurrentUser, false, false) ? [("Chat", @"C:\Apps\Chat\chat.exe --minimized")]
            : key == new RunKey(Hive.LocalMachine, false, false) ? throw new UnauthorizedAccessException("Access is denied.")
            : [];

        public IReadOnlyDictionary<string, byte[]> Approved(Hive hive, string subkey) =>
            new Dictionary<string, byte[]> { ["Chat"] = [0x03, 0, 0, 0] };

        public IReadOnlyList<(string Folder, Hive Hive)> StartupFolders() => [];

        public IReadOnlyList<string> FilesIn(string folder) => [];

        public byte[] ReadAllBytes(string path) => [];

        public TaskList Tasks() => new([new TaskDefinition(@"\Vendor\Updater", UpdaterTask)], []);

        public ServiceList Services() => new([new ServiceDefinition("VendorSvc", "Vendor Service", @"C:\Apps\Vendor\svc.exe", 2, false, 0x10)], []);

        public string ExpandEnvironment(string text) => text;

        public bool Exists(string path) => path.Contains("Chat", StringComparison.Ordinal);

        public Signature SignatureOf(string path) => new(SignatureState.Valid, "Chat Inc");
    }

    /// <summary>The chat app uses 4 seconds of CPU over the window; the updater never runs.</summary>
    internal sealed class FakeSampler : IProcessSampler
    {
        private int calls;

        public IReadOnlyList<ProcessSample> Sample() =>
            [new ProcessSample(10, "chat", @"C:\Apps\Chat\chat.exe", TimeSpan.FromSeconds(calls++ == 0 ? 1 : 5), 200_000_000)];
    }

    /// <summary>Desktop, Downloads and Documents as folders in the fixture.</summary>
    internal sealed class FixtureFolders(TempRoot root) : IOrganizeFolders
    {
        public IReadOnlyList<OrganizeFolder> Find() =>
            [.. new[] { KnownFolder.Desktop, KnownFolder.Downloads, KnownFolder.Documents }.Select(k => new OrganizeFolder(k, root.CreateDirectory($"user/{k}")))];
    }

    /// <summary>Returns the folder a test sets, as if the user picked it.</summary>
    internal sealed class FixturePicker : IFolderPicker
    {
        public string? Next { get; set; }

        public string? Pick(string title, string? initialFolder) => Next;
    }

    internal sealed class FixtureRecycleBin : IRecycleBin
    {
        public bool Holds(string location, string originalPath) => System.IO.File.Exists(location) || Directory.Exists(location);

        public void Restore(string location, string originalPath, bool isDirectory) => new PlainFileMover().Move(location, originalPath, isDirectory);
    }

    internal sealed class PlainPlatform(TempRoot root) : IPlatformOperations
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

        public IReadOnlyList<string> RunningProcesses(IReadOnlyCollection<string> processNames) => [];

        public string? CannotRecycle(string finalPath, long sizeBytes) => null;

        public string MoveToRecycleBin(string finalPath, bool isDirectory)
        {
            var destination = root.Resolve($"RecycleBin/{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            new PlainFileMover().Move(finalPath, destination, isDirectory);
            return destination;
        }

        public void MoveToQuarantine(string finalPath, string destination, bool isDirectory) => System.IO.File.Move(finalPath, destination);

        public void MoveToFolder(string finalPath, string destination, bool isDirectory) => new PlainFileMover().Move(finalPath, destination, isDirectory);
    }
}

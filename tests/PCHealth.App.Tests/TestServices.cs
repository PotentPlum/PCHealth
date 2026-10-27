using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.Execution;
using PCHealth.Ledger;
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
            s.AddSingleton(sp => new Restorer(
                sp.GetRequiredService<TransactionLedger>(),
                sp.GetRequiredService<QuarantineStore>(),
                new PlainFileMover(),
                sp.GetRequiredService<TimeProvider>(),
                new FixtureRecycleBin()));
        });

    internal sealed class PassThroughResolver : IPathResolver
    {
        public PathResolution Resolve(string path, LinkHandling links = LinkHandling.DoNotFollow) =>
            System.IO.File.Exists(path) || Directory.Exists(path)
                ? PathResolution.Resolved(path, System.IO.File.GetAttributes(path))
                : PathResolution.Rejected(PathRejectionReason.NotFound, "Not found.");
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
    }
}

using PCHealth.Inventory;
using PCHealth.Testing;

namespace PCHealth.Organization.Tests;

public sealed class ProjectDetectorTests : IDisposable
{
    private static readonly DateTime Old = new(2024, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly TempRoot root = new();

    public void Dispose() => root.Dispose();

    [Fact]
    public void Roots_are_found_from_every_kind_of_manifest()
    {
        root.CreateFile("Code/App/.git/HEAD", "ref: refs/heads/main");
        root.CreateFile("Code/App/App.sln", "sln");
        root.CreateFile("Code/Lib/Lib.slnx", "<Solution />");
        root.CreateFile("Code/Tool/tool.py", "print()");
        root.CreateFile("Code/Tool/pyproject.toml", "[project]");
        root.CreateFile("Code/Crate/Cargo.toml", "[package]");
        root.CreateFile("Code/Site/package.json", "{}");
        root.CreateFile("Video/Trip/trip.prproj", "x");
        root.CreateFile("Video/Intro/intro.aep", "x");
        root.CreateFile("3D/Chair/chair.blend", "x");
        root.CreateFile("Music/Song/song.als", "x");
        root.CreateFile("Music/Beat/beat.flp", "x");
        root.CreateFile("Design/Logo/logo.psd", "x");
        root.CreateFile("Design/Logo/logo.png", "x");
        root.CreateFile("Pictures/single.psd", "x");
        root.CreateFile("Loose/notes.txt", "x");

        var projects = Detect();

        Assert.Equal(
            [
                "3D/Chair", "Code/App", "Code/Crate", "Code/Lib", "Code/Site", "Code/Tool", "Design/Logo",
                "Music/Beat", "Music/Song", "Video/Intro", "Video/Trip",
            ],
            projects.Select(Relative));
        var app = projects.Single(p => p.Name == "App");
        Assert.Equal(["Git repository (.git folder)", "Visual Studio solution (App.sln)"], app.Markers.Select(m => m.Description));
        Assert.Contains("Photoshop file with other files next to it", projects.Single(p => p.Name == "Logo").Markers.Single().Description);
    }

    [Fact]
    public void Nested_roots_know_their_parent_and_the_parent_counts_their_files()
    {
        root.CreateDirectory("Repo/.git");
        root.CreateFile("Repo/Repo.sln", "sln");
        Sized("Repo/src/Core/Core.csproj", 100);
        Sized("Repo/src/Core/Class1.cs", 50);
        Sized("Repo/web/package.json", 10);
        Sized("Repo/web/node_modules/left-pad/package.json", 20);
        Sized("Repo/src/Core/bin/Debug/Other.csproj", 5);
        Sized("Repo/src/Core/obj/project.assets.json", 5);

        var projects = Detect();

        Assert.Equal(["Repo", "Repo/src/Core", "Repo/web"], projects.Select(Relative));
        var repo = projects[0];
        Assert.Null(repo.ParentRoot);
        Assert.All(projects.Skip(1), p => Assert.Equal(repo.Root, p.ParentRoot));
        Assert.Equal(3 + 100 + 50 + 10 + 20 + 5 + 5, repo.TotalSize.Bytes);
        Assert.Equal(7, repo.Files.Count);
        Assert.Equal(30, projects.Single(p => p.Name == "web").TotalSize.Bytes);
    }

    [Fact]
    public void Last_activity_is_the_newest_modified_time_and_is_described_as_a_fact()
    {
        Dated("Old/Old.csproj", Old);
        Dated("Old/Program.cs", Old.AddDays(-30));
        Dated("Old/docs/readme.md", Old.AddDays(-400));

        var project = Detect().Single();

        Assert.Equal(new DateTimeOffset(Old), project.LastActivityUtc);
        Assert.Equal("Not changed since 1 March 2024.", project.Activity);
        Assert.True(project.IsInactive(new DateTimeOffset(Old).AddDays(366), TimeSpan.FromDays(365)));
        Assert.False(project.IsInactive(new DateTimeOffset(Old).AddDays(364), TimeSpan.FromDays(365)));
        foreach (var word in new[] { "expendable", "delete", "remove", "unused", "safe" })
        {
            Assert.DoesNotContain(word, project.Activity, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_project_in_a_cloud_sync_folder_says_which_service_syncs_it()
    {
        root.CreateFile("OneDrive - Contoso/Thesis/thesis.blend", "x");
        root.CreateFile("Local/Game/Cargo.toml", "x");

        var projects = Detect();

        Assert.Equal("OneDrive", projects.Single(p => p.Name == "Thesis").CloudSync);
        Assert.Null(projects.Single(p => p.Name == "Game").CloudSync);
    }

    [Theory]
    [InlineData("OneDrive", "OneDrive")]
    [InlineData("OneDrive - Contoso", "OneDrive")]
    [InlineData("Dropbox", "Dropbox")]
    [InlineData("My Drive", "Google Drive")]
    [InlineData("iCloudDrive", "iCloud Drive")]
    [InlineData("OneDriveBackup", null)]
    [InlineData("Projects", null)]
    public void Sync_services_are_recognised_by_folder_name(string folder, string? expected)
    {
        Assert.Equal(expected, ProjectDetector.SyncService(root.Resolve(Path.Combine(folder, "Work"))));
    }

    [Fact]
    public void Cloud_placeholders_count_as_online_only()
    {
        Assert.True(DetectedProject.IsOnlineOnly(FileAttributes.Archive | FileAttributes.Offline));
        Assert.True(DetectedProject.IsOnlineOnly((FileAttributes)0x400000));
        Assert.True(DetectedProject.IsOnlineOnly((FileAttributes)0x40000));
        Assert.False(DetectedProject.IsOnlineOnly(FileAttributes.Archive | FileAttributes.ReadOnly));
    }

    [Fact]
    public void A_folder_that_only_holds_manifests_of_dependencies_is_not_a_project()
    {
        root.CreateFile("Downloads/node_modules/pkg/package.json", "{}");
        root.CreateFile("Downloads/bin/Release/App.csproj", "x");

        Assert.Empty(Detect());
    }

    private IReadOnlyList<DetectedProject> Detect() => ProjectDetector.Find(new RecursiveScanner().Scan(root.Path));

    private string Relative(DetectedProject project) => Path.GetRelativePath(root.Path, project.Root).Replace('\\', '/');

    private void Sized(string relativePath, int bytes) => root.CreateFile(relativePath, new string('x', bytes));

    private void Dated(string relativePath, DateTime modifiedUtc) =>
        File.SetLastWriteTimeUtc(root.CreateFile(relativePath, "x"), modifiedUtc);
}

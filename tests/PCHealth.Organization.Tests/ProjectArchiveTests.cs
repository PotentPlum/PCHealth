using PCHealth.Inventory;
using PCHealth.Testing;

namespace PCHealth.Organization.Tests;

public sealed class ProjectArchiveTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly TempRoot root = new();

    public void Dispose() => root.Dispose();

    [Theory]
    [InlineData(12, 13, true)]
    [InlineData(12, 12, true)]
    [InlineData(12, 11, false)]
    [InlineData(3, 4, true)]
    [InlineData(24, 13, false)]
    public void A_project_is_inactive_once_its_newest_file_is_older_than_the_threshold(int months, int ageInMonths, bool inactive)
    {
        var project = Project(Now.AddMonths(-ageInMonths));

        Assert.Equal(inactive, ProjectArchive.IsInactive(project, Now, months));
    }

    [Fact]
    public void A_project_without_files_is_never_offered()
    {
        root.CreateDirectory("Empty/.git");
        var project = ProjectDetector.Find(new RecursiveScanner().Scan(root.Path, cancellationToken: TestContext.Current.CancellationToken)).Single();

        Assert.False(ProjectArchive.IsInactive(project, Now, 1));
    }

    [Fact]
    public void The_wording_states_facts_and_never_suggests_the_project_can_go()
    {
        var text = ProjectArchive.Describe(Project(Now.AddMonths(-20)), 12);

        Assert.StartsWith("Not changed since ", text);
        Assert.Contains("Nothing in it changed for 12 months or more.", text);
        Assert.Contains("Undo in Activity moves it back.", text);
        foreach (var word in new[] { "delete", "remove", "expendable", "unused", "no longer needed", "safe to", "clean" })
        {
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_threshold_must_be_at_least_one_month()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectArchive.IsInactive(Project(Now), Now, 0));
    }

    private DetectedProject Project(DateTimeOffset modified)
    {
        var name = Guid.NewGuid().ToString("N");
        var manifest = root.CreateFile($"{name}/Cargo.toml", "[package]");
        File.SetLastWriteTimeUtc(manifest, modified.UtcDateTime);
        return ProjectDetector.Find(new RecursiveScanner().Scan(root.Resolve(name))).Single();
    }
}

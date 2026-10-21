using PCHealth.Core;
using PCHealth.Testing;

namespace PCHealth.Policy.Tests;

public class ExclusionListTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void File_exclusions_match_only_that_file()
    {
        using var root = new TempRoot();
        var list = new ExclusionList();
        list.ExcludeFile(root.Resolve("keep/me.txt"), Now);

        Assert.True(list.IsExcluded(root.Resolve("keep/me.txt")));
        Assert.False(list.IsExcluded(root.Resolve("keep/me.txt.bak")));
        Assert.False(list.IsExcluded(root.Resolve("keep")));
    }

    [Fact]
    public void Folder_exclusions_cover_the_folder_and_everything_below_it()
    {
        using var root = new TempRoot();
        var list = new ExclusionList();
        list.ExcludeFolder(root.Resolve("Projects") + Path.DirectorySeparatorChar, Now);

        Assert.True(list.IsExcluded(root.Resolve("Projects")));
        Assert.True(list.IsExcluded(root.Resolve("Projects/app/bin/out.dll")));
        Assert.False(list.IsExcluded(root.Resolve("Projects-old/file.txt")));
        Assert.False(list.IsExcluded(root.Path));
    }

    [Fact]
    public void Paths_are_normalised_before_matching()
    {
        using var root = new TempRoot();
        var list = new ExclusionList();
        list.ExcludeFolder(root.Resolve("a"), Now);

        Assert.True(list.IsExcluded(root.Path + Path.DirectorySeparatorChar + "b" + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "a" + Path.DirectorySeparatorChar + "x.txt"));
    }

    [Fact]
    public void Path_case_follows_the_platform()
    {
        using var root = new TempRoot();
        var list = new ExclusionList();
        list.ExcludeFile(root.Resolve("Photo.JPG"), Now);

        Assert.Equal(OperatingSystem.IsWindows(), list.IsExcluded(root.Resolve("photo.jpg")));
    }

    [Fact]
    public void Relative_paths_are_refused()
    {
        var list = new ExclusionList();

        Assert.Throws<ArgumentException>(() => list.ExcludeFolder("relative/folder", Now));
    }

    [Fact]
    public void Category_exclusions_match_their_category()
    {
        var list = new ExclusionList();
        list.ExcludeCategory(FindingCategory.VersionCandidate, Now);

        Assert.True(list.IsExcluded(FindingCategory.VersionCandidate));
        Assert.False(list.IsExcluded(FindingCategory.Cache));
    }

    [Fact]
    public void Adding_the_same_exclusion_twice_keeps_one_and_remove_deletes_it()
    {
        using var root = new TempRoot();
        var list = new ExclusionList();
        var first = list.ExcludeFolder(root.Resolve("a"), Now, "first");
        var second = list.ExcludeFolder(root.Resolve("a") + Path.DirectorySeparatorChar, Now.AddDays(1), "second");

        Assert.Same(first, second);
        Assert.Single(list.Entries);
        Assert.True(list.Remove(first));
        Assert.Empty(list.Entries);
        Assert.False(list.IsExcluded(root.Resolve("a/x")));
    }

    [Fact]
    public void Apply_withholds_findings_in_excluded_categories()
    {
        var list = new ExclusionList();
        var exclusion = list.ExcludeCategory(FindingCategory.Cache, Now);
        var cache = MakeFinding(FindingCategory.Cache, new MeasurementEvidence(new MeasuredSize(1), "size"));
        var duplicate = MakeFinding(FindingCategory.VerifiedDuplicate, new MeasurementEvidence(new MeasuredSize(1), "size"));

        var result = list.Apply([cache, duplicate]);

        Assert.Equal([duplicate], result.Kept);
        var excluded = Assert.Single(result.Excluded);
        Assert.Same(cache, excluded.Finding);
        Assert.Same(exclusion, excluded.Exclusion);
    }

    [Fact]
    public void Apply_withholds_findings_whose_evidence_touches_an_excluded_path()
    {
        using var root = new TempRoot();
        var list = new ExclusionList();
        list.ExcludeFolder(root.Resolve("Photos"), Now);
        var touchesPhotos = MakeFinding(
            FindingCategory.VerifiedDuplicate,
            new FileContentEvidence(root.Resolve("Downloads/a.jpg"), new MeasuredSize(3), Now, "SHA-256:00"),
            new FileContentEvidence(root.Resolve("Photos/a.jpg"), new MeasuredSize(3), Now, "SHA-256:00"));
        var elsewhere = MakeFinding(FindingCategory.LargeFolder, new PathEvidence(root.Resolve("Videos"), "large"));
        var unreadable = MakeFinding(
            FindingCategory.LargeFolder,
            new InaccessibleEvidence(root.Resolve("Photos/locked"), InaccessibleReason.AccessDenied, "denied"));

        var result = list.Apply([touchesPhotos, elsewhere, unreadable]);

        Assert.Equal([elsewhere], result.Kept);
        Assert.Equal([touchesPhotos, unreadable], result.Excluded.Select(e => e.Finding));
    }

    [Fact]
    public void Exclusions_round_trip_through_a_local_file()
    {
        using var root = new TempRoot();
        var file = root.Resolve("settings/exclusions.json");
        var list = new ExclusionList();
        list.ExcludeFile(root.Resolve("a.txt"), Now, "keep this");
        list.ExcludeFolder(root.Resolve("Projects"), Now);
        list.ExcludeCategory(FindingCategory.AppResidue, Now);

        list.Save(file);
        var loaded = ExclusionList.Load(file);

        Assert.Equal(list.Entries, loaded.Entries);
        Assert.True(loaded.IsExcluded(root.Resolve("Projects/x")));
        Assert.True(loaded.IsExcluded(FindingCategory.AppResidue));
        Assert.Contains("\"Category\"", File.ReadAllText(file));
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void A_missing_file_means_no_exclusions()
    {
        using var root = new TempRoot();

        Assert.Empty(ExclusionList.Load(root.Resolve("none.json")).Entries);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Version\": 99, \"Exclusions\": []}")]
    [InlineData("{\"Version\": 1}")]
    [InlineData("{\"Version\": 1, \"Exclusions\": [{\"Kind\": \"Category\", \"Value\": \"Nope\", \"AddedUtc\": \"2026-01-01T00:00:00Z\"}]}")]
    [InlineData("{\"Version\": 1, \"Exclusions\": [{\"Kind\": \"Folder\", \"Value\": \"relative\", \"AddedUtc\": \"2026-01-01T00:00:00Z\"}]}")]
    public void A_corrupt_file_is_reported_not_silently_ignored(string content)
    {
        using var root = new TempRoot();
        var file = root.CreateFile("exclusions.json", content);

        Assert.Throws<InvalidDataException>(() => ExclusionList.Load(file));
    }

    private static Finding MakeFinding(FindingCategory category, params Evidence[] evidence) => new(
        category,
        "Something",
        "Because",
        evidence,
        new Confidence(ConfidenceLevel.High, "test"),
        new Impact("Nothing yet"),
        new Recovery(RecoveryMethod.RecycleBin, "Until emptied"));
}

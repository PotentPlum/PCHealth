using PCHealth.Core;
using PCHealth.Testing;
using static PCHealth.Duplicates.Tests.CandidateFinderTests;

namespace PCHealth.Duplicates.Tests;

public class VersionCandidatesTests
{
    [Theory]
    [InlineData("Report.docx", "report")]
    [InlineData("Report v2.docx", "report")]
    [InlineData("Report_v1.3.docx", "report")]
    [InlineData("Report - Copy.docx", "report")]
    [InlineData("Report - Copy (2).docx", "report")]
    [InlineData("Report (1).docx", "report")]
    [InlineData("Copy of Report.docx", "report")]
    [InlineData("Report FINAL.docx", "report")]
    [InlineData("Report final v3 draft.docx", "report")]
    [InlineData("Report 2026-01-31.docx", "report")]
    [InlineData("Report_20260131.docx", "report")]
    [InlineData("report_rev4.docx", "report")]
    [InlineData("notes_2.txt", "notes")]
    [InlineData("Annual Report 2025.pdf", "annual report 2025")]
    [InlineData("Chapter 1000.md", "chapter 1000")]
    [InlineData("vacation.jpg", "vacation")]
    [InlineData("v2.txt", "")]
    public void Stem_removes_version_copy_and_date_markers(string name, string expected)
    {
        Assert.Equal(expected, VersionCandidates.Stem(name));
    }

    [Fact]
    public void Groups_likely_versions_and_orders_newest_first()
    {
        using var root = new TempRoot();
        var v1 = Aged(root.CreateFile("Report v1.docx", "one"), minutesAgo: 30);
        var v2 = Aged(root.CreateFile("Report v2.docx", "two!"), minutesAgo: 10);
        var final = Aged(root.CreateFile("Report FINAL.docx", "three"), minutesAgo: 0);
        var other = Aged(root.CreateFile("Budget.xlsx", "x"), minutesAgo: 0);

        var group = Assert.Single(VersionCandidates.Find([v1, v2, final, other]));

        Assert.Equal("report", group.Stem);
        Assert.Equal(".docx", group.Extension);
        Assert.Equal(new[] { final.FullPath, v2.FullPath, v1.FullPath }, group.Files.Select(f => f.FullPath));
        Assert.True(group.SameFolder);
        Assert.Equal(ConfidenceLevel.Medium, group.Confidence.Level);
        Assert.Equal(3, group.Evidence.Count);
    }

    [Fact]
    public void Different_extensions_are_not_versions_of_each_other()
    {
        using var root = new TempRoot();
        var doc = Entry(root.CreateFile("Report v1.docx", "a"));
        var pdf = Entry(root.CreateFile("Report v2.pdf", "b"));

        Assert.Empty(VersionCandidates.Find([doc, pdf]));
    }

    [Fact]
    public void Identical_names_in_different_folders_are_not_a_version_signal()
    {
        using var root = new TempRoot();
        var a = Entry(root.CreateFile("projectA/notes.txt", "a"));
        var b = Entry(root.CreateFile("projectB/notes.txt", "b"));

        Assert.Empty(VersionCandidates.Find([a, b]));
    }

    [Fact]
    public void Versions_across_folders_get_low_confidence()
    {
        using var root = new TempRoot();
        var a = Entry(root.CreateFile("Documents/Thesis v4.pdf", "a"));
        var b = Entry(root.CreateFile("Downloads/Thesis v5.pdf", "b"));

        var group = Assert.Single(VersionCandidates.Find([a, b]));

        Assert.False(group.SameFolder);
        Assert.Equal(ConfidenceLevel.Low, group.Confidence.Level);
    }

    [Fact]
    public void Name_similarity_alone_never_makes_files_removable()
    {
        // Even byte-identical versions stay review-only here; only the verifier produces verified duplicates.
        using var root = new TempRoot();
        var a = Entry(root.CreateFile("Photo.jpg", "same bytes"));
        var b = Entry(root.CreateFile("Photo (1).jpg", "same bytes"));

        var group = Assert.Single(VersionCandidates.Find([a, b]));

        Assert.True(group.ReviewOnly);
        Assert.True(group.Confidence.Level < ConfidenceLevel.High);
        Assert.IsNotType<VerifiedDuplicateGroup>(group, exactMatch: false);
    }

    private static FileEntry Aged(string path, int minutesAgo)
    {
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
        return CandidateFinderTests.Entry(path);
    }
}

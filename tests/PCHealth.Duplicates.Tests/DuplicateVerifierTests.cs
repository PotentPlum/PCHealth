using System.Security.Cryptography;
using System.Text;
using PCHealth.Core;
using PCHealth.Testing;
using static PCHealth.Duplicates.Tests.CandidateFinderTests;

namespace PCHealth.Duplicates.Tests;

public class DuplicateVerifierTests
{
    [Fact]
    public void Identical_files_are_verified_with_their_full_sha256()
    {
        using var root = new TempRoot();
        const string content = "identical content";
        var files = new[] { Entry(root.CreateFile("a/1.bin", content)), Entry(root.CreateFile("b/2.bin", content)) };

        var result = Find(files);

        var group = Assert.Single(result.Groups);
        var expected = "SHA-256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        Assert.Equal(expected, group.ContentHash);
        Assert.Equal(content.Length, group.FileSize.Bytes);
        Assert.All(group.Files, f => Assert.Equal(expected, f.ContentHash));
        Assert.Equal(ConfidenceLevel.Verified, group.Confidence.Level);
    }

    [Fact]
    public void Candidates_that_differ_only_in_the_middle_are_not_verified()
    {
        using var root = new TempRoot();
        var files = new[] { Entry(root.CreateFile("a", "head-XXXXXX-tail")), Entry(root.CreateFile("b", "head-YYYYYY-tail")) };
        var candidates = new CandidateFinder(sampleBytes: 4).Find(files, TestContext.Current.CancellationToken);
        Assert.Single(candidates.Groups);

        var result = new DuplicateVerifier().Verify(candidates.Groups, TestContext.Current.CancellationToken);

        Assert.Empty(result.Groups);
    }

    [Fact]
    public void One_candidate_group_can_split_into_several_verified_groups()
    {
        using var root = new TempRoot();
        var files = new[]
        {
            Entry(root.CreateFile("a1", "head-AAAAAA-tail")),
            Entry(root.CreateFile("a2", "head-AAAAAA-tail")),
            Entry(root.CreateFile("b1", "head-BBBBBB-tail")),
            Entry(root.CreateFile("b2", "head-BBBBBB-tail")),
            Entry(root.CreateFile("c1", "head-CCCCCC-tail")),
        };
        var candidates = new CandidateFinder(sampleBytes: 4).Find(files, TestContext.Current.CancellationToken);

        var result = new DuplicateVerifier().Verify(candidates.Groups, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(
            new[] { "a1,a2", "b1,b2" },
            result.Groups.Select(g => string.Join(',', g.Files.Select(f => f.File.Name))).Order());
    }

    [Fact]
    public void Groups_carry_path_size_timestamp_and_hash_evidence()
    {
        using var root = new TempRoot();
        var files = new[] { Entry(root.CreateFile("1.txt", "dup")), Entry(root.CreateFile("2.txt", "dup")) };

        var group = Assert.Single(Find(files).Groups);

        var evidence = group.Evidence.Cast<FileContentEvidence>().ToArray();
        Assert.Equal(files.Select(f => f.FullPath), evidence.Select(e => e.Path));
        Assert.All(evidence, e =>
        {
            Assert.Equal(3, e.Size.Bytes);
            Assert.Equal(group.ContentHash, e.ContentHash);
            Assert.Contains(e.Path, e.Description);
            Assert.Contains(e.ContentHash, e.Description);
        });
        Assert.Equal(files.Select(f => f.LastWriteTimeUtc), evidence.Select(e => e.LastWriteTimeUtc));
    }

    [Fact]
    public void A_file_changed_after_the_candidate_stage_is_not_verified()
    {
        using var root = new TempRoot();
        var files = new[] { Entry(root.CreateFile("a", "same")), Entry(root.CreateFile("b", "same")) };
        var candidates = new CandidateFinder().Find(files, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(files[1].FullPath, files[1].LastWriteTimeUtc.UtcDateTime.AddMinutes(1));

        var result = new DuplicateVerifier().Verify(candidates.Groups, TestContext.Current.CancellationToken);

        Assert.Empty(result.Groups);
        Assert.Equal(files[1].FullPath, Assert.Single(result.ChangedSinceScan).FullPath);
    }

    [Fact]
    public void A_file_deleted_after_the_candidate_stage_is_recorded_as_unreadable()
    {
        using var root = new TempRoot();
        var files = new[] { Entry(root.CreateFile("a", "same")), Entry(root.CreateFile("b", "same")), Entry(root.CreateFile("c", "same")) };
        var candidates = new CandidateFinder().Find(files, TestContext.Current.CancellationToken);
        File.Delete(files[2].FullPath);

        var result = new DuplicateVerifier().Verify(candidates.Groups, TestContext.Current.CancellationToken);

        Assert.Equal(2, Assert.Single(result.Groups).Files.Count);
        Assert.Equal(InaccessibleReason.Vanished, Assert.Single(result.Unreadable).Reason);
    }

    [Fact]
    public void Pipeline_reports_problems_from_both_stages()
    {
        using var root = new TempRoot();
        var a = Entry(root.CreateFile("a", "same"));
        var b = Entry(root.CreateFile("b", "same"));
        var gone = Entry(root.CreateFile("gone", "same"));
        File.Delete(gone.FullPath);

        var result = Find([a, b, gone]);

        Assert.Single(result.Groups);
        Assert.Equal(gone.FullPath, Assert.Single(result.Unreadable).Path);
    }

    [Fact]
    public void Large_files_hash_across_buffer_boundaries()
    {
        using var root = new TempRoot();
        var bytes = new byte[3 * 1024 * 1024 + 17];
        new Random(42).NextBytes(bytes);
        var first = root.Resolve("big1.bin");
        var second = root.Resolve("big2.bin");
        File.WriteAllBytes(first, bytes);
        File.WriteAllBytes(second, bytes);

        var group = Assert.Single(Find([Entry(first), Entry(second)]).Groups);

        Assert.Equal("SHA-256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), group.ContentHash);
    }

    private static VerificationResult Find(IEnumerable<FileEntry> files) =>
        new DuplicateFinder().Find(files, TestContext.Current.CancellationToken);
}

using Microsoft.Win32.SafeHandles;
using PCHealth.Core;
using PCHealth.Testing;
using static PCHealth.Duplicates.Tests.CandidateFinderTests;

namespace PCHealth.Duplicates.Tests;

/// <summary>Hard-link handling with a stand-in identity provider; the Windows provider is tested on Windows.</summary>
public class SameFileTests
{
    [Fact]
    public void Paths_to_one_physical_file_are_not_duplicates()
    {
        using var root = new TempRoot();
        var a = Entry(root.CreateFile("a.txt", "same"));
        var b = Entry(root.CreateFile("b.txt", "same"));
        var identities = new FakeIdentities { [a.FullPath] = Id(1), [b.FullPath] = Id(1) };

        var result = new DuplicateFinder(identities).Find([a, b], TestContext.Current.CancellationToken);

        Assert.Empty(result.Groups);
    }

    [Fact]
    public void A_hard_link_pair_plus_a_real_copy_is_two_physical_copies()
    {
        using var root = new TempRoot();
        var original = Entry(root.CreateFile("original.txt", "same"));
        var link = Entry(root.CreateFile("link.txt", "same"));
        var copy = Entry(root.CreateFile("copy.txt", "same"));
        var identities = new FakeIdentities { [original.FullPath] = Id(1), [link.FullPath] = Id(1), [copy.FullPath] = Id(2) };

        var group = Assert.Single(new DuplicateFinder(identities).Find([original, link, copy], TestContext.Current.CancellationToken).Groups);

        Assert.Equal(3, group.Files.Count);
        Assert.Equal(2, group.PhysicalCopies.Count);
        var linked = Assert.Single(group.PhysicalCopies, c => c.Count == 2);
        Assert.Equal(new[] { "link.txt", "original.txt" }, linked.Select(f => f.File.Name).Order());
        Assert.Contains("All 2 files", group.Confidence.Rationale);
    }

    [Fact]
    public void Files_without_a_known_identity_count_as_separate()
    {
        using var root = new TempRoot();
        var a = Entry(root.CreateFile("a.txt", "same"));
        var b = Entry(root.CreateFile("b.txt", "same"));

        var group = Assert.Single(new DuplicateFinder(new FakeIdentities()).Find([a, b], TestContext.Current.CancellationToken).Groups);

        Assert.Equal(2, group.PhysicalCopies.Count);
        Assert.All(group.Files, f => Assert.Null(f.Identity));
    }

    private static FileIdentity Id(int fileId) => new(VolumeSerialNumber: 7, FileId: (UInt128)fileId);

    private sealed class FakeIdentities : Dictionary<string, FileIdentity>, IFileIdentityProvider
    {
        public FileIdentity? TryGetIdentity(SafeFileHandle handle, string path) =>
            TryGetValue(path, out var identity) ? identity : null;
    }
}

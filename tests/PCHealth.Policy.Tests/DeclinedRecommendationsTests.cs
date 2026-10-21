using PCHealth.Core;
using PCHealth.Policy.Recommendations;
using PCHealth.Testing;

namespace PCHealth.Policy.Tests;

public class DeclinedRecommendationsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PCHealth.Tests", "declines"));

    [Fact]
    public void A_declined_recommendation_stays_declined_across_scans_with_the_same_evidence()
    {
        var declines = new DeclinedRecommendations();
        declines.Decline(Cache(100, "a.tmp"), Now);

        Assert.True(declines.IsDeclined(Cache(100, "a.tmp")));
        Assert.True(declines.IsDeclined(Cache(150, "b.tmp", "c.tmp")));
    }

    [Fact]
    public void An_estimate_that_at_least_doubles_is_new_evidence()
    {
        var declines = new DeclinedRecommendations();
        declines.Decline(Cache(100, "a.tmp"), Now);

        Assert.True(declines.IsDeclined(Cache(199, "a.tmp")));
        Assert.False(declines.IsDeclined(Cache(200, "a.tmp")));
    }

    [Fact]
    public void Different_content_or_other_copies_are_new_evidence()
    {
        var declines = new DeclinedRecommendations();
        declines.Decline(Duplicate("SHA-256:aaa", "x/r.pdf", "y/r.pdf"), Now);

        Assert.True(declines.IsDeclined(Duplicate("SHA-256:aaa", "x/r.pdf", "y/r.pdf")));
        Assert.False(declines.IsDeclined(Duplicate("SHA-256:bbb", "x/r.pdf", "y/r.pdf")));
        Assert.False(declines.IsDeclined(Duplicate("SHA-256:aaa", "x/r.pdf", "y/r.pdf", "z/r.pdf")));
    }

    [Fact]
    public void Other_recommendations_are_not_affected()
    {
        var declines = new DeclinedRecommendations();
        declines.Decline(Cache(100, "a.tmp"), Now);

        Assert.False(declines.IsDeclined(Duplicate("SHA-256:aaa", "x/r.pdf", "y/r.pdf")));
        Assert.True(declines.Remove(Cache(100, "a.tmp").Key));
        Assert.False(declines.IsDeclined(Cache(100, "a.tmp")));
    }

    [Fact]
    public void Declines_survive_a_restart_and_corrupt_files_are_refused()
    {
        using var root = new TempRoot();
        var path = root.Resolve("state/declined.json");
        var declines = new DeclinedRecommendations();
        declines.Decline(Cache(100, "a.tmp"), Now);
        declines.Save(path);

        var loaded = DeclinedRecommendations.Load(path);

        Assert.True(loaded.IsDeclined(Cache(100, "a.tmp")));
        Assert.Equal(Now, Assert.Single(loaded.Entries).DeclinedUtc);
        Assert.Empty(DeclinedRecommendations.Load(root.Resolve("missing.json")).Entries);
        File.WriteAllText(path, "{ nope");
        Assert.Throws<InvalidDataException>(() => DeclinedRecommendations.Load(path));
    }

    private static Recommendation Build(Finding finding) =>
        new RecommendationBuilder(new ProtectionPolicy([]), new ExclusionList()).Build([finding]).Recommendations.Single();

    private static Recommendation Cache(long bytes, params string[] files) => Build(new Finding(
        FindingCategory.Cache, "Temporary files", "Regenerable.",
        [new PathEvidence(Path.Combine(Base, "Temp"), "Known location."), new MeasurementEvidence(new MeasuredSize(bytes), $"{files.Length} files.")],
        new Confidence(ConfidenceLevel.High, "Known location."),
        new Impact("Moves files to the Recycle Bin.", new EstimatedSize(bytes, "sum of sizes")),
        new Recovery(RecoveryMethod.RecycleBin, "Restorable."),
        operations: files.Select(f => new ProposedOperation(OperationKind.MoveToRecycleBin, Path.Combine(Base, "Temp", f), isDirectory: false, new MeasuredSize(1))).ToArray()));

    private static Recommendation Duplicate(string hash, params string[] files) => Build(new Finding(
        FindingCategory.VerifiedDuplicate, "Identical copies of r.pdf", "Wasted space.",
        files.Select(f => (Evidence)new FileContentEvidence(Path.Combine(Base, f), new MeasuredSize(10), Now, hash)).ToArray(),
        new Confidence(ConfidenceLevel.Verified, "Same hash."),
        new Impact("Moves the extra copies to the Recycle Bin.", new EstimatedSize(10 * (files.Length - 1), "one size per copy")),
        new Recovery(RecoveryMethod.RecycleBin, "Restorable."),
        operations: files.Skip(1).Select(f => new ProposedOperation(OperationKind.MoveToRecycleBin, Path.Combine(Base, f), isDirectory: false, new MeasuredSize(10))).ToArray()));
}

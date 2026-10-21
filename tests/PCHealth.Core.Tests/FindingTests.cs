using System.Reflection;

namespace PCHealth.Core.Tests;

public class FindingTests
{
    private static readonly Evidence SomeEvidence = new MeasurementEvidence(new MeasuredSize(1_000), "Folder size");
    private static readonly Confidence HighConfidence = new(ConfidenceLevel.High, "Known cache location");
    private static readonly Impact SomeImpact = new("Deletes cache files", new EstimatedSize(1_000, "folder size"), "The app rebuilds the cache");
    private static readonly Recovery RecycleBin = new(RecoveryMethod.RecycleBin, "Until the Recycle Bin is emptied");

    [Fact]
    public void Finding_keeps_confidence_impact_and_recovery_as_separate_parts()
    {
        var finding = new Finding("Browser cache", "Frees space", [SomeEvidence], HighConfidence, SomeImpact, RecycleBin);

        Assert.Same(HighConfidence, finding.Confidence);
        Assert.Same(SomeImpact, finding.Impact);
        Assert.Same(RecycleBin, finding.Recovery);
    }

    [Fact]
    public void Verified_confidence_does_not_imply_recoverability()
    {
        // High confidence and no undo must be representable side by side (spec §3.3).
        var irreversible = new Recovery(RecoveryMethod.None, "Permanently deleted");

        var finding = new Finding(
            "Duplicate file",
            "Frees space",
            [SomeEvidence],
            new Confidence(ConfidenceLevel.Verified, "Full content hash matched"),
            SomeImpact,
            irreversible);

        Assert.Equal(ConfidenceLevel.Verified, finding.Confidence.Level);
        Assert.False(finding.Recovery.CanUndo);
    }

    [Fact]
    public void Finding_exposes_no_combined_score()
    {
        var names = typeof(Finding).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);

        Assert.Equal(
            new[] { "Confidence", "Evidence", "Impact", "Recovery", "Title", "WhyItMatters" },
            names.Order());
    }

    [Fact]
    public void Finding_requires_evidence()
    {
        Assert.Throws<ArgumentException>(() => new Finding("t", "w", [], HighConfidence, SomeImpact, RecycleBin));
        Assert.Throws<ArgumentException>(() => new Finding("t", "w", [null!], HighConfidence, SomeImpact, RecycleBin));
    }

    [Fact]
    public void Finding_copies_its_evidence_list()
    {
        var list = new List<Evidence> { SomeEvidence };
        var finding = new Finding("t", "w", list, HighConfidence, SomeImpact, RecycleBin);

        list.Add(new PathEvidence("/x", "late addition"));

        Assert.Single(finding.Evidence);
    }

    [Fact]
    public void Finding_requires_all_three_dimensions()
    {
        Assert.Throws<ArgumentNullException>(() => new Finding("t", "w", [SomeEvidence], null!, SomeImpact, RecycleBin));
        Assert.Throws<ArgumentNullException>(() => new Finding("t", "w", [SomeEvidence], HighConfidence, null!, RecycleBin));
        Assert.Throws<ArgumentNullException>(() => new Finding("t", "w", [SomeEvidence], HighConfidence, SomeImpact, null!));
    }

    [Fact]
    public void Irreversible_recovery_cannot_carry_a_retention_deadline()
    {
        Assert.Throws<ArgumentException>(() => new Recovery(RecoveryMethod.None, "gone", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Quarantine_recovery_keeps_its_deadline()
    {
        var deadline = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

        var recovery = new Recovery(RecoveryMethod.Quarantine, "Restored to the original path", deadline);

        Assert.True(recovery.CanUndo);
        Assert.Equal(deadline, recovery.RetentionDeadlineUtc);
    }

    [Fact]
    public void Undefined_enum_values_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Confidence((ConfidenceLevel)99, "r"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Recovery((RecoveryMethod)99, "l"));
    }

    [Fact]
    public void Each_dimension_requires_its_explanation()
    {
        Assert.Throws<ArgumentException>(() => new Confidence(ConfidenceLevel.Low, " "));
        Assert.Throws<ArgumentException>(() => new Impact(""));
        Assert.Throws<ArgumentException>(() => new Recovery(RecoveryMethod.RecycleBin, ""));
    }
}

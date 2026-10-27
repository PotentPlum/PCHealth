using PCHealth.Core;
using PCHealth.Duplicates;
using PCHealth.Inventory;
using PCHealth.Policy.Recommendations;
using PCHealth.Testing;

namespace PCHealth.Policy.Tests;

public class RecommendationBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PCHealth.Tests", "recommendations"));

    [Fact]
    public void An_action_answers_all_six_questions_and_keeps_the_three_dimensions_separate()
    {
        var finding = RecycleFinding("cache", 400);

        var recommendation = Assert.Single(Builder().Build([finding]).ToAct);

        Assert.Equal(Disposition.Act, recommendation.Disposition);
        Assert.Null(recommendation.ReviewReason);
        Assert.Equal($"Some cache ({P("cache")})", recommendation.WhatWeFound);
        Assert.Equal("It is regenerable.", recommendation.WhyItMatters);
        Assert.Same(finding.Evidence, recommendation.Evidence);
        Assert.Equal("Moves 1 file to the Recycle Bin.", recommendation.WhatWillChange);
        Assert.Equal("It is rebuilt on next use.", recommendation.WhatCouldHappenAfterward);
        Assert.StartsWith("Yes, from the Recycle Bin.", recommendation.CanIRecover);
        Assert.Same(finding.Confidence, recommendation.Confidence);
        Assert.Same(finding.Impact, recommendation.Impact);
        Assert.Same(finding.Recovery, recommendation.Recovery);
        Assert.Equal(finding.Operations, recommendation.Operations);
    }

    [Fact]
    public void Review_only_findings_go_to_review_and_nothing_is_selected()
    {
        var finding = QuarantineFinding("odd", ConfidenceLevel.Low, reviewOnly: true);

        var recommendation = Assert.Single(Builder().Build([finding]).ForReview);

        Assert.Equal("The evidence is incomplete or contradictory.", recommendation.ReviewReason);
        Assert.StartsWith("Nothing is selected: review it first.", recommendation.WhatWillChange);
    }

    [Fact]
    public void Low_confidence_findings_go_to_review()
    {
        var finding = new Finding(
            FindingCategory.Cache, "Weak", "Maybe.", [new PathEvidence(P("weak"), "x")],
            new Confidence(ConfidenceLevel.Low, "Unsure."), new Impact("Moves 1 file."),
            new Recovery(RecoveryMethod.RecycleBin, "Recycle Bin."),
            operations: [Op(P("weak/a.tmp"))]);

        Assert.Equal("The evidence is weak.", Assert.Single(Builder().Build([finding]).ForReview).ReviewReason);
    }

    [Fact]
    public void Quarantine_recovery_states_the_retention_period()
    {
        var finding = QuarantineFinding("old", ConfidenceLevel.Medium);

        Assert.Contains("for 14 days", Assert.Single(Builder().Build([finding]).ToAct).CanIRecover);
        Assert.Contains("for 30 days", Assert.Single(Builder(retention: TimeSpan.FromDays(30)).Build([finding]).ToAct).CanIRecover);
    }

    [Fact]
    public void Operations_on_protected_paths_are_removed_and_all_blocked_means_review()
    {
        var finding = RecycleFinding("mixed", 10, P("mixed/a.tmp"), P("shielded/b.tmp"));
        var protection = new ProtectionPolicy([new(P("shielded"), ProtectionKind.PersonalFolder, "test protection.")]);

        var partly = Assert.Single(Builder(protection).Build([finding]).ToAct);
        Assert.Equal(P("mixed/a.tmp"), Assert.Single(partly.Operations).Path);
        Assert.Contains("test protection.", Assert.Single(partly.Blocked).Reason);
        Assert.Contains("1 items are left alone", partly.WhatWillChange);

        var allProtected = new ProtectionPolicy([new(Base, ProtectionKind.PersonalFolder, "everything.")]);
        var blocked = Assert.Single(Builder(allProtected).Build([finding]).ForReview);
        Assert.Equal("Every item it would change is protected or excluded.", blocked.ReviewReason);
        Assert.Empty(blocked.Operations);
    }

    [Fact]
    public void Excluded_findings_are_withheld_and_excluded_operations_removed()
    {
        var excludedFinding = RecycleFinding("ignored", 10);
        var partlyExcluded = RecycleFinding("partly", 10, P("partly/a.tmp"), P("elsewhere/kept.tmp"));
        var exclusions = new ExclusionList();
        exclusions.ExcludeFolder(P("ignored"), Now);
        exclusions.ExcludeFile(P("elsewhere/kept.tmp"), Now);

        var set = Builder(exclusions: exclusions).Build([excludedFinding, partlyExcluded]);

        Assert.Same(excludedFinding, Assert.Single(set.Excluded).Finding);
        var kept = Assert.Single(set.Recommendations);
        Assert.Equal(P("partly/a.tmp"), Assert.Single(kept.Operations).Path);
        Assert.StartsWith("You excluded", Assert.Single(kept.Blocked).Reason);
    }

    [Fact]
    public void Actions_come_first_then_larger_estimated_space()
    {
        var small = RecycleFinding("small", 10);
        var large = RecycleFinding("large", 5000);
        var review = QuarantineFinding("review", ConfidenceLevel.Low, reviewOnly: true);

        var ordered = Builder().Build([review, small, large]).Recommendations;

        Assert.Equal(new[] { large, small, review }, ordered.Select(r => r.Finding));
    }

    [Fact]
    public void Keys_are_stable_for_the_same_paths_and_differ_for_others()
    {
        var a = Builder().Build([RecycleFinding("one", 10)]).Recommendations[0].Key;
        var again = Builder().Build([RecycleFinding("one", 99)]).Recommendations[0].Key;
        var other = Builder().Build([RecycleFinding("two", 10)]).Recommendations[0].Key;

        Assert.Equal(a, again);
        Assert.NotEqual(a, other);
        Assert.StartsWith("Cache:", a);
    }

    [Fact]
    public void Verified_duplicates_keep_the_oldest_copy_and_recycle_the_others()
    {
        using var root = new TempRoot();
        var oldest = Copy(root, "a/report.pdf", ageDays: 90);
        var newer = Copy(root, "b/report.pdf", ageDays: 10);
        var newest = Copy(root, "c/report.pdf", ageDays: 1);

        var finding = FindingSources.FromDuplicates(Group(root), new ProtectionPolicy([]));
        var recommendation = Assert.Single(Builder().Build([finding]).ToAct);

        Assert.Equal(FindingCategory.VerifiedDuplicate, recommendation.Category);
        Assert.Equal(ConfidenceLevel.Verified, recommendation.Confidence.Level);
        Assert.Equal(new[] { newer, newest }, recommendation.Operations.Select(o => o.Path).Order(StringComparer.Ordinal));
        Assert.All(recommendation.Operations, o => Assert.Equal(OperationKind.MoveToRecycleBin, o.Kind));
        Assert.Contains($"Keeps '{oldest}'", recommendation.WhatWillChange);
        Assert.All(recommendation.Operations, o => Assert.Equal(oldest, o.KeptCopy));
        Assert.Equal(2 * 64, recommendation.Impact.PotentiallyReclaimable!.Value.Bytes);
        Assert.Equal("3 locations", recommendation.WhatWeFound.Split('(')[1].TrimEnd(')'));
    }

    [Fact]
    public void A_choice_of_duplicates_keeps_at_least_one_copy_and_names_it()
    {
        using var root = new TempRoot();
        var oldest = Copy(root, "a/report.pdf", ageDays: 90);
        var newer = Copy(root, "b/report.pdf", ageDays: 10);
        var newest = Copy(root, "c/report.pdf", ageDays: 1);
        var group = Group(root);
        IReadOnlyList<HashedFile> Of(string path) => group.PhysicalCopies.Single(c => c[0].File.FullPath == path);

        Assert.Equal("Choose at least one copy to remove.", Builder().ForDuplicates(group, []).Refusal);
        var all = Builder().ForDuplicates(group, group.PhysicalCopies.ToArray());
        Assert.False(all.IsAllowed);
        Assert.StartsWith("At least one copy has to stay.", all.Refusal);

        // The user may keep a newer copy and remove the oldest.
        var choice = Builder().ForDuplicates(group, [Of(oldest), Of(newest)]);

        var recommendation = choice.Recommendation!;
        Assert.Equal(Disposition.Act, recommendation.Disposition);
        Assert.Equal(new[] { oldest, newest }, recommendation.Operations.Select(o => o.Path).Order(StringComparer.Ordinal));
        Assert.All(recommendation.Operations, o => Assert.Equal(newer, o.KeptCopy));
        Assert.Contains($"Keeps '{newer}'", recommendation.WhatWillChange);
        Assert.Equal(2 * 64, recommendation.Impact.PotentiallyReclaimable!.Value.Bytes);
    }

    [Fact]
    public void A_choice_that_touches_a_protected_or_excluded_copy_is_refused()
    {
        using var root = new TempRoot();
        var a = Copy(root, "a/report.pdf", ageDays: 90);
        var docs = Copy(root, "docs/report.pdf", ageDays: 10);
        var c = Copy(root, "c/report.pdf", ageDays: 1);
        var group = Group(root);
        IReadOnlyList<HashedFile> Of(string path) => group.PhysicalCopies.Single(x => x[0].File.FullPath == path);
        var protection = new ProtectionPolicy([new(root.Resolve("docs"), ProtectionKind.PersonalFolder, "your Documents.")]);
        var exclusions = new ExclusionList();
        exclusions.ExcludeFile(c, Now);
        var builder = new RecommendationBuilder(protection, exclusions);

        Assert.Equal($"'{docs}' is protected: your Documents.", builder.ForDuplicates(group, [Of(docs)]).Refusal);
        Assert.Equal($"You excluded '{c}', so '{c}' cannot be removed.", builder.ForDuplicates(group, [Of(a), Of(c)]).Refusal);
        Assert.True(builder.ForDuplicates(group, [Of(a)]).IsAllowed);
    }

    [Fact]
    public void A_protected_duplicate_is_the_one_kept()
    {
        using var root = new TempRoot();
        var oldest = Copy(root, "a/report.pdf", ageDays: 90);
        var protectedCopy = Copy(root, "docs/report.pdf", ageDays: 10);
        var protection = new ProtectionPolicy([new(root.Resolve("docs"), ProtectionKind.PersonalFolder, "your Documents.")]);

        var finding = FindingSources.FromDuplicates(Group(root), protection);
        var recommendation = Assert.Single(new RecommendationBuilder(protection, new ExclusionList()).Build([finding]).ToAct);

        Assert.Equal(oldest, Assert.Single(recommendation.Operations).Path);
        Assert.Contains($"Keeps '{protectedCopy}'", recommendation.WhatWillChange);
        Assert.Empty(recommendation.Blocked);
    }

    [Fact]
    public void Version_candidates_and_large_folders_are_for_review_and_change_nothing()
    {
        using var root = new TempRoot();
        root.CreateFile("docs/Plan v1.docx", "a");
        root.CreateFile("docs/Plan v2.docx", "bb");
        var scan = new RecursiveScanner().Scan(root.Path, cancellationToken: TestContext.Current.CancellationToken);
        var versions = FindingSources.FromVersionCandidates(Assert.Single(VersionCandidates.Find(scan.Entries.OfType<FileEntry>())));
        var folder = FindingSources.FromLargeFolder(StorageSummary.From(scan).LargestFolders(1)[0], scan.IsComplete);

        var set = Builder().Build([versions, folder]);

        Assert.Empty(set.ToAct);
        Assert.Equal(2, set.ForReview.Count);
        Assert.All(set.ForReview, r =>
        {
            Assert.Empty(r.Operations);
            Assert.Equal("Nothing changes: this is for you to review.", r.WhatWillChange);
            Assert.Equal("Nothing changes, so there is nothing to recover.", r.CanIRecover);
        });
        Assert.All(set.ForReview, r => Assert.Equal("There is nothing to change automatically.", r.ReviewReason));
    }

    private static RecommendationBuilder Builder(ProtectionPolicy? protection = null, ExclusionList? exclusions = null, TimeSpan? retention = null) =>
        new(protection ?? new ProtectionPolicy([]), exclusions ?? new ExclusionList(), retention);

    private static string P(string relative) => Path.Combine(Base, relative);

    private static ProposedOperation Op(string path, OperationKind kind = OperationKind.MoveToRecycleBin, long bytes = 1) =>
        new(kind, path, isDirectory: kind == OperationKind.MoveToQuarantine, new MeasuredSize(bytes));

    private static Finding RecycleFinding(string folder, long bytes, params string[] files)
    {
        files = files.Length == 0 ? [P($"{folder}/a.tmp")] : files;
        return new Finding(
            FindingCategory.Cache,
            $"Some {folder}",
            "It is regenerable.",
            [new PathEvidence(P(folder), "Known cache location.")],
            new Confidence(ConfidenceLevel.High, "Known location."),
            new Impact($"Moves {files.Length} file to the Recycle Bin.", new EstimatedSize(bytes, "sum of sizes"), "It is rebuilt on next use."),
            new Recovery(RecoveryMethod.RecycleBin, "Restorable until the Recycle Bin is emptied."),
            operations: files.Select(f => Op(f)).ToArray());
    }

    private static Finding QuarantineFinding(string folder, ConfidenceLevel level, bool reviewOnly = false) =>
        new(
            FindingCategory.AppResidue,
            $"Leftover {folder}",
            "Probably unused.",
            [new PathEvidence(P(folder), "Application data folder.")],
            new Confidence(level, "Name match only."),
            new Impact("Moves the folder to quarantine.", new EstimatedSize(1, "measured")),
            new Recovery(RecoveryMethod.Quarantine, "Restorable to its original path."),
            reviewOnly,
            [Op(P(folder), OperationKind.MoveToQuarantine)]);

    private static string Copy(TempRoot root, string relative, int ageDays)
    {
        var path = root.CreateFile(relative, new string('d', 64));
        File.SetLastWriteTimeUtc(path, Now.AddDays(-ageDays).UtcDateTime);
        return path;
    }

    private static VerifiedDuplicateGroup Group(TempRoot root)
    {
        var scan = new RecursiveScanner().Scan(root.Path, cancellationToken: TestContext.Current.CancellationToken);
        return Assert.Single(new DuplicateFinder().Find(scan.Entries.OfType<FileEntry>(), TestContext.Current.CancellationToken).Groups);
    }
}

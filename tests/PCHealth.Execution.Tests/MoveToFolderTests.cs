using PCHealth.Core;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;

namespace PCHealth.Execution.Tests;

/// <summary>Moves into a folder the user chose (T-51): destination checks, the ledger record, Undo and interruption.</summary>
public partial class ExecutionGatewayTests
{
    [Fact]
    public void A_move_records_both_paths_and_undo_moves_the_items_back()
    {
        using var fixture = new Fixture();
        var file = fixture.File("Desktop/notes.txt", "notes");
        var folder = fixture.Folder("Desktop/Trip", ("a.jpg", "a"));
        var archive = fixture.Root.CreateDirectory("Archive");
        var recommendation = Move(fixture, archive, file, folder);

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.False(File.Exists(file));
        Assert.Equal("a", File.ReadAllText(Path.Combine(archive, "Trip", "a.jpg")));
        var record = fixture.Ledger.Find(result.TransactionId!.Value)!;
        Assert.Equal(LedgerOperation.MoveToFolder, record.Operation);
        Assert.Equal(RecoveryMethod.MoveBack, record.RecoveryMethod);
        Assert.Equal(TransactionState.Verified, record.State);
        Assert.Equal((0L, 0L), (record.ExpectedBytes, record.ObservedBytes));
        Assert.Contains("destination is a real folder, not protected or excluded, on the same drive, with nothing to overwrite", record.Checks);
        Assert.Equal([file, folder], record.Items.Select(i => i.OriginalPath));
        Assert.Equal([Path.Combine(archive, "notes.txt"), Path.Combine(archive, "Trip")], record.Items.Select(i => i.RecoveryLocation));
        Assert.All(record.Items, i => Assert.NotNull(i.After));

        var restorer = new Restorer(fixture.Ledger, fixture.Store, clock: fixture.Clock);
        Assert.StartsWith("2 items can be moved back", restorer.SupportFor(record).Limits);
        var undo = restorer.Restore(record.Id).Restore!;

        Assert.Equal(TransactionState.Verified, undo.State);
        Assert.Equal("notes", File.ReadAllText(file));
        Assert.True(File.Exists(Path.Combine(folder, "a.jpg")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(archive));
        Assert.Equal(["the item is still where it was moved to", "nothing exists at the original path"], undo.Checks);
        Assert.False(restorer.SupportFor(record).CanRestore);
    }

    [Fact]
    public void Undo_leaves_an_item_where_it_went_when_something_now_occupies_its_original_path()
    {
        using var fixture = new Fixture();
        var file = fixture.File("Desktop/notes.txt", "old");
        var archive = fixture.Root.CreateDirectory("Archive");
        var recommendation = Move(fixture, archive, file);
        var moved = fixture.Gateway().Execute(recommendation, recommendation.Operations);
        fixture.File("Desktop/notes.txt", "new, written after the move");

        var undo = new Restorer(fixture.Ledger, fixture.Store, clock: fixture.Clock).Restore(moved.TransactionId!.Value).Restore!;

        Assert.Equal(TransactionState.Failed, undo.State);
        Assert.Equal("Something now exists at the original path, so the item stays where it was moved to.", Assert.Single(undo.Items).Error);
        Assert.Equal("0 of 1 items are back; the others stay where they were moved to.", undo.Outcome);
        Assert.Equal("new, written after the move", File.ReadAllText(file));
        Assert.Equal("old", File.ReadAllText(Path.Combine(archive, "notes.txt")));
    }

    [Fact]
    public void A_destination_that_is_a_link_or_leads_elsewhere_is_refused()
    {
        using var fixture = new Fixture();
        var file = fixture.File("Desktop/notes.txt", "notes");
        var real = fixture.Root.CreateDirectory("Real");
        var link = fixture.Root.Resolve("Link");
        Directory.CreateSymbolicLink(link, real);
        var redirected = fixture.Root.CreateDirectory("Redirected");
        fixture.Resolver.Redirects[redirected] = real;

        Assert.Contains("is a link or leads elsewhere", Reason(fixture, link, file));
        Assert.Contains("is a link or leads elsewhere", Reason(fixture, redirected, file));
        Assert.True(File.Exists(file));
        Assert.Empty(Directory.EnumerateFileSystemEntries(real));
    }

    [Fact]
    public void A_name_clash_in_the_destination_is_refused_and_nothing_is_overwritten()
    {
        using var fixture = new Fixture();
        var file = fixture.File("Desktop/notes.txt", "mine");
        var archive = fixture.Root.CreateDirectory("Archive");
        fixture.File("Archive/notes.txt", "already there");
        var recommendation = Move(fixture, archive, file);

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("Something named 'notes.txt' is already in the destination; nothing is overwritten.", result.Items.Single().Reason);
        Assert.Equal("mine", File.ReadAllText(file));
        Assert.Equal("already there", File.ReadAllText(Path.Combine(archive, "notes.txt")));
    }

    [Fact]
    public void A_source_changed_after_the_preview_is_left_alone()
    {
        using var fixture = new Fixture();
        var file = fixture.File("Desktop/notes.txt", "notes");
        var archive = fixture.Root.CreateDirectory("Archive");
        var recommendation = Move(fixture, archive, file);
        File.WriteAllText(file, "edited after the preview");

        var result = fixture.Gateway().Execute(recommendation, recommendation.Operations);

        Assert.Equal("It changed since it was found, so it was left alone.", result.Items.Single().Reason);
        Assert.True(File.Exists(file));
        Assert.Empty(Directory.EnumerateFileSystemEntries(archive));
    }

    [Fact]
    public void Destinations_on_another_drive_protected_excluded_or_inside_the_item_are_refused()
    {
        using var fixture = new Fixture();
        var file = fixture.File("Desktop/notes.txt", "notes");
        var folder = fixture.Folder("Desktop/Trip", ("a.jpg", "a"));
        var inside = fixture.Root.CreateDirectory("Desktop/Trip/Sub");
        var archive = fixture.Root.CreateDirectory("Archive");
        var system = fixture.Root.CreateDirectory("Windows/Archive");

        fixture.Platform.SameVolumeResult = false;
        Assert.Equal("The destination is on a different drive. Moves between drives are not supported yet.", Reason(fixture, archive, file));
        fixture.Platform.SameVolumeResult = true;

        var protection = new ProtectionPolicy([new(fixture.Root.Resolve("Windows"), ProtectionKind.OperatingSystem, "it holds Windows system files.")]);
        Assert.StartsWith("The destination can't be used:", Reason(fixture, system, file, protection));

        fixture.Exclusions.ExcludeFolder(archive, Now);
        Assert.Equal($"The destination can't be used: You excluded '{archive}'.", Reason(fixture, archive, file));

        Assert.Equal("A folder can't be moved into itself.", Reason(fixture, inside, folder));
        Assert.Equal("It is already in that folder.", Reason(fixture, fixture.Root.Resolve("Desktop"), file));
        Assert.True(File.Exists(file) && Directory.Exists(folder));
    }

    [Fact]
    public void Items_inside_a_personal_folder_can_move_but_the_folder_itself_and_program_files_cannot()
    {
        using var fixture = new Fixture();
        var documents = fixture.Root.CreateDirectory("Documents");
        var file = fixture.File("Documents/notes.txt", "notes");
        var program = fixture.File("Documents/tool.exe", "MZ");
        var archive = fixture.Root.CreateDirectory("Documents/Archive");
        var protection = new ProtectionPolicy([new(documents, ProtectionKind.PersonalFolder, "it is your Documents folder.")]);

        Assert.Equal(ItemStatus.Done, Run(fixture, archive, file, protection).Items.Single().Status);
        Assert.Contains("is protected: it is your Documents folder.", Reason(fixture, fixture.Root.CreateDirectory("Elsewhere"), documents, protection));
        var refused = new RecommendationBuilder(protection, new ExclusionList())
            .ForMove([new MoveCandidate(program, false, new MeasuredSize(2))], archive, "You chose to tidy Documents.");
        Assert.Contains("program code", refused.Refusal);
        Assert.Contains("is protected", protection.Check(Path.Combine(archive, "notes.txt")).Reason);
    }

    [Fact]
    public void A_move_interrupted_mid_way_is_settled_at_startup_and_can_be_undone()
    {
        using var fixture = new Fixture();
        var first = fixture.File("Desktop/a.txt", "a");
        var second = fixture.File("Desktop/b.txt", "b");
        var archive = fixture.Root.CreateDirectory("Archive");
        var recommendation = Move(fixture, archive, first, second);
        fixture.Platform.CrashOnMove = 2;

        Assert.Throws<SimulatedCrash>(() => fixture.Gateway().Execute(recommendation, recommendation.Operations));
        var pending = Assert.Single(fixture.Ledger.InState(TransactionState.Pending));
        Assert.Equal(Path.Combine(archive, "b.txt"), pending.Items[1].RecoveryLocation);

        var resolved = Assert.Single(new InterruptedOperations(fixture.Ledger, fixture.Store).Resolve().Resolved);

        Assert.Equal(TransactionState.Failed, resolved.State);
        Assert.Equal([ItemStatus.Done, ItemStatus.Skipped], resolved.Items.Select(i => i.Status));
        Assert.True(File.Exists(second));

        var undo = new Restorer(fixture.Ledger, fixture.Store, clock: fixture.Clock).Restore(resolved.Id).Restore!;
        Assert.Equal(TransactionState.Verified, undo.State);
        Assert.Equal("a", File.ReadAllText(first));
    }

    [Fact]
    public void An_interrupted_move_found_at_both_paths_is_left_for_the_user()
    {
        using var fixture = new Fixture();
        var file = fixture.File("Desktop/a.txt", "a");
        var archive = fixture.Root.CreateDirectory("Archive");
        var recommendation = Move(fixture, archive, file);
        fixture.Platform.CrashOnMove = 1;
        Assert.Throws<SimulatedCrash>(() => fixture.Gateway().Execute(recommendation, recommendation.Operations));
        fixture.File("Archive/a.txt", "a copy someone made");

        var resolved = Assert.Single(new InterruptedOperations(fixture.Ledger, fixture.Store).Resolve().Resolved);

        Assert.Equal(ItemStatus.Failed, resolved.Items.Single().Status);
        Assert.StartsWith("Found both at the original path and at", resolved.Items.Single().Error);
        Assert.True(File.Exists(file));
    }

    private static Recommendation Move(Fixture fixture, string destination, params string[] paths)
    {
        var items = paths.Select(p => Directory.Exists(p)
            ? new MoveCandidate(p, true, new MeasuredSize(1))
            : new MoveCandidate(p, false, new MeasuredSize(new FileInfo(p).Length), new FileInfo(p).LastWriteTimeUtc)).ToArray();
        var choice = new RecommendationBuilder(new ProtectionPolicy([]), new ExclusionList()).ForMove(items, destination, "You chose to tidy the desktop.");
        Assert.True(choice.IsAllowed, choice.Refusal);
        return choice.Recommendation!;
    }

    private static ExecutionResult Run(Fixture fixture, string destination, string path, ProtectionPolicy? protection = null)
    {
        var recommendation = Move(fixture, destination, path);
        return fixture.Gateway(protection).Execute(recommendation, recommendation.Operations);
    }

    private static string? Reason(Fixture fixture, string destination, string path, ProtectionPolicy? protection = null) =>
        Run(fixture, destination, path, protection).Items.Single().Reason;
}

using PCHealth.Core;
using PCHealth.Policy.Recommendations;

namespace PCHealth.Policy.Tests;

/// <summary>Moves the user chose (T-51): the choice checks and the narrower protection for moves (D-029).</summary>
public class MoveChoiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PCHealth.Tests", "moves"));
    private static readonly string Documents = P("Documents");
    private static readonly ProtectionPolicy Protection = new(
    [
        new(Documents, ProtectionKind.PersonalFolder, "it is your Documents folder."),
        new(P("Windows"), ProtectionKind.OperatingSystem, "it holds Windows system files."),
    ]);

    [Fact]
    public void A_chosen_move_answers_the_six_questions_and_can_be_undone()
    {
        var choice = Builder().ForMove([File("Documents/notes.txt"), Folder("Documents/Trip")], P("Documents/Archive"), "You chose to tidy Documents.");

        var recommendation = choice.Recommendation!;
        Assert.Equal(Disposition.Act, recommendation.Disposition);
        Assert.Equal(FindingCategory.Organization, recommendation.Category);
        Assert.Equal("Move 2 items to Archive", recommendation.Finding.Title);
        Assert.Equal($"Moves 2 items into {P("Documents/Archive")}. Nothing is deleted.", recommendation.WhatWillChange);
        Assert.StartsWith("Yes: Undo moves the items back where they were", recommendation.CanIRecover);
        Assert.Null(recommendation.Impact.PotentiallyReclaimable);
        Assert.All(recommendation.Operations, o =>
        {
            Assert.Equal(OperationKind.MoveToFolder, o.Kind);
            Assert.Equal(P("Documents/Archive"), o.DestinationFolder);
        });
        Assert.Equal(P("Documents/Archive/Trip"), recommendation.Operations[1].Destination);
    }

    [Fact]
    public void Choices_that_cannot_be_moved_as_shown_are_refused()
    {
        var builder = Builder();
        var archive = P("Archive");

        Assert.Equal("Choose at least one item to move.", builder.ForMove([], archive, "x").Refusal);
        Assert.Equal("Choose the folder to move them to.", builder.ForMove([File("a.txt")], "relative", "x").Refusal);
        Assert.Contains("contains", builder.ForMove([File("a.txt")], Base, "x").Refusal);
        Assert.StartsWith("The destination can't be used:", builder.ForMove([File("a.txt")], P("Windows/Temp"), "x").Refusal);
        Assert.Contains("is protected: it is your Documents folder.", builder.ForMove([Folder("Documents")], archive, "x").Refusal);
        Assert.Contains("program code", builder.ForMove([File("Documents/setup.exe")], archive, "x").Refusal);
        Assert.Equal("Two chosen items are both named 'a.txt', so they can't go into one folder.",
            builder.ForMove([File("one/a.txt"), File("two/a.txt")], archive, "x").Refusal);
        Assert.Equal($"'{P("Trip/a.jpg")}' is inside another chosen folder; choose one or the other.",
            builder.ForMove([Folder("Trip"), File("Trip/a.jpg")], archive, "x").Refusal);
    }

    [Fact]
    public void Exclusions_refuse_the_item_the_destination_or_the_whole_category()
    {
        var exclusions = new ExclusionList();
        exclusions.ExcludeFile(P("keep.txt"), Now);
        exclusions.ExcludeFolder(P("Archive"), Now);
        var builder = new RecommendationBuilder(Protection, exclusions);

        Assert.Equal($"You excluded '{P("keep.txt")}', so '{P("keep.txt")}' cannot be moved.", builder.ForMove([File("keep.txt")], P("Other"), "x").Refusal);
        Assert.Equal($"You excluded '{P("Archive")}', so nothing is moved into it.", builder.ForMove([File("a.txt")], P("Archive"), "x").Refusal);

        exclusions.ExcludeCategory(FindingCategory.Organization, Now);
        Assert.Equal("You excluded organizing in Settings, so nothing is moved.", builder.ForMove([File("a.txt")], P("Other"), "x").Refusal);
    }

    [Fact]
    public void Move_protection_only_relaxes_items_inside_personal_folders()
    {
        Assert.True(Protection.Check(P("Documents/notes.txt")).IsBlocked);
        Assert.False(Protection.CheckMove(P("Documents/notes.txt")).IsBlocked);
        Assert.True(Protection.CheckMove(Documents).IsBlocked);
        Assert.False(Protection.CheckMove(Documents, asDestination: true).IsBlocked);
        Assert.True(Protection.CheckMove(P("Windows/notes.txt")).IsBlocked);
        Assert.True(Protection.CheckMove(P("Windows/Temp"), asDestination: true).IsBlocked);
        Assert.True(Protection.CheckMove(Base).IsBlocked);
        Assert.True(Protection.CheckMove(P("Documents/tool.dll")).IsBlocked);
        Assert.False(Protection.CheckMove(P("Documents/Old.exe"), asDestination: true).IsBlocked);
    }

    [Fact]
    public void A_move_names_its_destination_and_nothing_else_does()
    {
        Assert.Throws<ArgumentException>(() => new ProposedOperation(OperationKind.MoveToFolder, P("a.txt"), false, new MeasuredSize(1)));
        Assert.Throws<ArgumentException>(() => new ProposedOperation(OperationKind.MoveToFolder, P("a.txt"), false, new MeasuredSize(1), destinationFolder: "relative"));
        Assert.Throws<ArgumentException>(() => new ProposedOperation(OperationKind.MoveToRecycleBin, P("a.txt"), false, new MeasuredSize(1), destinationFolder: P("x")));
        Assert.Throws<ArgumentException>(() => new ProposedOperation(OperationKind.MoveToFolder, P("a.txt"), false, new MeasuredSize(1), keptCopy: P("b.txt"), destinationFolder: P("x")));
        Assert.Equal(RecoveryMethod.MoveBack, ProposedOperation.RecoveryFor(OperationKind.MoveToFolder));
        Assert.Null(new ProposedOperation(OperationKind.MoveToRecycleBin, P("a.txt"), false, new MeasuredSize(1)).Destination);
    }

    private static RecommendationBuilder Builder() => new(Protection, new ExclusionList());

    private static MoveCandidate File(string relative) => new(P(relative), false, new MeasuredSize(10), Now);

    private static MoveCandidate Folder(string relative) => new(P(relative), true, new MeasuredSize(10));

    private static string P(string relative) => Path.Combine(Base, relative.Replace('/', Path.DirectorySeparatorChar));
}

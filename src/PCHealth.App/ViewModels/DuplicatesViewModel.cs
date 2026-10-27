using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Core;
using PCHealth.Duplicates;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;

namespace PCHealth.App.ViewModels;

/// <summary>One physical copy in a group: its paths (more than one for hard links), size and times, and whether to remove it.</summary>
public sealed class DuplicateCopyViewModel(IReadOnlyList<HashedFile> copy, string? blocked) : ObservableObject
{
    private bool isSelected;

    public IReadOnlyList<HashedFile> Copy { get; } = copy;

    private FileEntry File => Copy[0].File;

    public string Path => string.Join(Environment.NewLine, Copy.Select(f => f.File.FullPath));

    public string Size => SizeText.Measured(File.Size);

    public string Modified => $"Modified {DuplicatesViewModel.Time(File.LastWriteTimeUtc)}";

    public string Created => File.CreationTimeUtc is { } created ? $"Created {DuplicatesViewModel.Time(created)}" : "Created: not known";

    public string? HardLinks => Copy.Count > 1
        ? $"{Copy.Count} paths of one file (hard links). They are removed together, because removing only some of them frees nothing."
        : null;

    /// <summary>Why this copy cannot be removed; null when it can.</summary>
    public string? Blocked { get; } = blocked is null ? null : $"Cannot be removed: {blocked}";

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }
}

/// <summary>One verified group: the copies side by side, the evidence on request, and the user's choice of copies to remove.</summary>
public sealed class DuplicateGroupViewModel : ObservableObject
{
    private bool showEvidence;
    private string? message;

    public DuplicateGroupViewModel(VerifiedDuplicateGroup group, Func<string, string?> blocked)
    {
        Group = group;
        Copies = group.PhysicalCopies
            .OrderBy(c => c.Min(f => f.File.LastWriteTimeUtc))
            .ThenBy(c => c[0].File.FullPath, StringComparer.Ordinal)
            .Select(c => new DuplicateCopyViewModel(c, c.Select(f => blocked(f.File.FullPath)).FirstOrDefault(b => b is not null)))
            .ToArray();
        foreach (var copy in Copies)
        {
            copy.PropertyChanged += (_, _) => OnPropertyChanged(nameof(RemoveText));
        }

        ToggleEvidenceCommand = new RelayCommand(() => ShowEvidence = !ShowEvidence);
    }

    public VerifiedDuplicateGroup Group { get; }

    public IReadOnlyList<DuplicateCopyViewModel> Copies { get; }

    /// <summary>What keeping one copy could free: one file size per extra copy.</summary>
    public long ReclaimableBytes => Group.FileSize.Bytes * (Group.PhysicalCopies.Count - 1);

    public string Title => $"{Group.PhysicalCopies.Count} identical copies of {Copies[0].Copy[0].File.Name}";

    public string Summary =>
        $"{SizeText.Bytes(Group.FileSize.Bytes)} each (measured). Keeping one copy would free about {SizeText.Bytes(ReclaimableBytes)} (estimated, once the Recycle Bin is emptied).";

    public string RemoveText
    {
        get
        {
            var selected = Copies.Count(c => c.IsSelected);
            return selected == 0 ? "Remove selected copies…" : $"Move {selected} selected {(selected == 1 ? "copy" : "copies")} to the Recycle Bin…";
        }
    }

    /// <summary>
    /// The checks behind "verified" (spec §5.1, D-006): equal size, the same head and tail digest, the same full hash,
    /// and the check that the paths are separate files rather than links to one.
    /// </summary>
    public IReadOnlyList<string> Evidence
    {
        get
        {
            var paths = Group.Files.Count;
            var copies = Group.PhysicalCopies.Count;
            var unknown = Group.Files.Count(f => f.Identity is null);
            var sameFile = paths == copies
                ? $"Same-file check: the {paths} paths are {copies} separate files, so each is a real copy."
                : $"Same-file check: {paths} paths but {copies} separate files; paths that are hard links to one file count as one copy.";
            if (unknown > 0)
            {
                sameFile += $" The file ID of {unknown} {(unknown == 1 ? "path" : "paths")} could not be read, so each of those counts as its own copy.";
            }

            return
            [
                string.Create(CultureInfo.CurrentCulture, $"Size: every file is exactly {Group.FileSize.Bytes:N0} bytes."),
                Group.PartialDigest is { } partial
                    ? $"Partial check: the start and end of every file give the same digest ({partial})."
                    : "Partial check: not run for this group.",
                $"Full check: the entire contents of every file give the same hash ({Group.ContentHash}).",
                sameFile,
                "Right before a copy is removed, it is compared byte for byte with a copy that stays; if they differ, it is left alone.",
                .. Group.Evidence.Select(e => e.Description),
            ];
        }
    }

    public bool ShowEvidence
    {
        get => showEvidence;
        set => SetProperty(ref showEvidence, value);
    }

    public IRelayCommand ToggleEvidenceCommand { get; }

    /// <summary>Why the last choice was refused; null when it was not.</summary>
    public string? Message
    {
        get => message;
        set => SetProperty(ref message, value);
    }
}

/// <summary>A choice of copies the policy accepted, ready for the usual preview and approval.</summary>
public sealed record DuplicateRemoval(VerifiedDuplicateGroup Group, Recommendation Recommendation);

/// <summary>
/// The Duplicates page (T-40, spec §5.1, §7): verified groups, biggest first, with each copy's path, size and times
/// side by side and the evidence on request. Nothing is preselected: the user picks the copies to remove. The policy
/// refuses a choice that removes every copy or touches a protected or excluded path; an accepted choice goes to the
/// preview, then through the gateway to the Recycle Bin, and can be undone from Activity.
/// </summary>
public sealed class DuplicatesViewModel : PageViewModel
{
    private readonly FindingsStore findings;
    private readonly ProtectionPolicy protection;
    private readonly ExclusionList exclusions;
    private readonly Func<RecommendationBuilder> builder;
    private string summary = "";

    public DuplicatesViewModel(FindingsStore findings, ProtectionPolicy protection, ExclusionList exclusions, Func<RecommendationBuilder> builder)
        : base("Duplicates")
    {
        this.findings = findings;
        this.protection = protection;
        this.exclusions = exclusions;
        this.builder = builder;
        RemoveCommand = new RelayCommand<DuplicateGroupViewModel>(Remove, group => group is not null);
        findings.Updated += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Raised when an accepted choice should be previewed before anything changes.</summary>
    public event EventHandler<DuplicateRemoval>? PreviewRequested;

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    public string Summary
    {
        get => summary;
        private set => SetProperty(ref summary, value);
    }

    public IRelayCommand<DuplicateGroupViewModel> RemoveCommand { get; }

    public static string Time(DateTimeOffset utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public void Refresh()
    {
        Groups.Clear();
        if (findings.Duplicates is not { } groups)
        {
            Summary = "Duplicates are found by a full scan. Run one from Storage with \"Measure again\".";
            return;
        }

        if (exclusions.IsExcluded(FindingCategory.VerifiedDuplicate))
        {
            Summary = "You excluded duplicates in Settings, so none are shown.";
            return;
        }

        foreach (var group in groups.Select(g => new DuplicateGroupViewModel(g, Blocked)).OrderByDescending(g => g.ReclaimableBytes).ThenByDescending(g => g.Group.FileSize.Bytes))
        {
            Groups.Add(group);
        }

        Summary = Groups.Count == 0
            ? $"No identical files of {SizeText.Bytes(Discovery.DuplicateMinimumBytes)} or more are left to review from the last scan of your user folder."
            : $"{Groups.Count} {(Groups.Count == 1 ? "group" : "groups")} of identical files of {SizeText.Bytes(Discovery.DuplicateMinimumBytes)} or more, biggest first. "
                + $"Keeping one copy of each would free about {SizeText.Bytes(Groups.Sum(g => g.ReclaimableBytes))} (estimated). Nothing is selected: choose the copies you want to remove.";
    }

    private string? Blocked(string path) =>
        protection.Check(path) is { IsBlocked: true } decision ? decision.Reason
        : exclusions.MatchPath(path) is { } exclusion ? $"you excluded '{exclusion.Value}'."
        : null;

    private void Remove(DuplicateGroupViewModel? group)
    {
        var choice = builder().ForDuplicates(group!.Group, group.Copies.Where(c => c.IsSelected).Select(c => c.Copy).ToArray());
        group.Message = choice.Refusal;
        if (choice.Recommendation is { } recommendation)
        {
            PreviewRequested?.Invoke(this, new DuplicateRemoval(group.Group, recommendation));
        }
    }
}

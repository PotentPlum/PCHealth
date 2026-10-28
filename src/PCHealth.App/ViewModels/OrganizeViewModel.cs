using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Core;
using PCHealth.Organization;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;

namespace PCHealth.App.ViewModels;

/// <summary>Asks the user for a folder. The app never picks a destination by itself.</summary>
public interface IFolderPicker
{
    /// <returns>The chosen folder's full path, or null when the user cancelled.</returns>
    string? Pick(string title, string? initialFolder);
}

/// <summary>One suggested file: its path, size, date, confidence and evidence, and whether to move it.</summary>
public sealed class OrganizeItemViewModel : ObservableObject
{
    private bool isSelected;

    public OrganizeItemViewModel(TriageSuggestion suggestion, string? blocked)
    {
        Suggestion = suggestion;
        Blocked = blocked is null ? null : $"Can't be moved: {blocked}";
        isSelected = suggestion.Preselected && blocked is null;
    }

    public TriageSuggestion Suggestion { get; }

    public string Path => Suggestion.File.FullPath;

    public string Size => SizeText.Measured(Suggestion.File.Size);

    public string Modified => $"Modified {DuplicatesViewModel.Time(Suggestion.File.LastWriteTimeUtc)}";

    public string Confidence => $"Confidence: {Suggestion.Confidence.Level}. {Suggestion.Confidence.Rationale}";

    /// <summary>The evidence, the file's own path line first.</summary>
    public IReadOnlyList<string> Evidence => Suggestion.Evidence.Skip(1).Select(e => e.Description).ToArray();

    /// <summary>Why this file can't be moved; null when it can.</summary>
    public string? Blocked { get; }

    public bool CanSelect => Blocked is null;

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value && CanSelect);
    }
}

/// <summary>A suggested category. Sensitive ones start with nothing ticked and need a second confirmation.</summary>
public sealed class OrganizeGroupViewModel : ObservableObject
{
    private bool confirmed;
    private string? message;

    public OrganizeGroupViewModel(TriageGroup group, Func<string, string?> blocked)
    {
        Group = group;
        Items = group.Items.Select(s => new OrganizeItemViewModel(s, blocked(s.File.FullPath))).ToArray();
        foreach (var item in Items)
        {
            item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(MoveText));
        }
    }

    public TriageGroup Group { get; }

    public IReadOnlyList<OrganizeItemViewModel> Items { get; }

    public string Title => $"{FileTriage.Title(Group.Category)} ({Items.Count})";

    public string Explanation => FileTriage.Explanation(Group.Category);

    public bool Sensitive => Group.Sensitive;

    public string? SensitiveNote => Sensitive
        ? "These may be personal or sensitive documents. Nothing here is selected for you, and moving them needs a second confirmation."
        : null;

    /// <summary>The second confirmation a sensitive category needs before its preview.</summary>
    public bool Confirmed
    {
        get => confirmed;
        set => SetProperty(ref confirmed, value);
    }

    public string ConfirmText => "I checked these files and want to move them";

    public string MoveText
    {
        get
        {
            var selected = Items.Count(i => i.IsSelected);
            return selected == 0 ? "Move selected files…" : $"Move {selected} selected {(selected == 1 ? "file" : "files")}…";
        }
    }

    /// <summary>Why the last choice was refused; null when it was not.</summary>
    public string? Message
    {
        get => message;
        set => SetProperty(ref message, value);
    }
}

/// <summary>A project folder: its size, last activity and dependencies. One with dependencies can't be selected.</summary>
public sealed class OrganizeProjectViewModel : ObservableObject
{
    private bool isSelected;

    public OrganizeProjectViewModel(ProjectReport report, string? blocked)
    {
        Report = report;
        Blocked = blocked is not null ? $"Can't be moved: {blocked}"
            : report.Dependencies.MoveRestricted ? "Can't be moved: files in it point outside the folder, so moving it could break it."
            : null;
    }

    public ProjectReport Report { get; }

    public string Name => Report.Project.Name;

    public string Path => Report.Project.Root;

    public string Size => $"{SizeText.Measured(Report.Project.TotalSize)} in {Report.Project.Files.Count} files";

    /// <summary>"Not changed since …": a fact, never a judgement of whether the project is still needed.</summary>
    public string Activity => Report.Project.Activity;

    public string Kind => string.Join(", ", Report.Project.Markers.Select(m => m.Description).Distinct(StringComparer.Ordinal));

    public string? Sync => Report.Project.CloudSync is { } service
        ? $"In a {service} folder.{(Report.Project.OnlineOnlyFiles > 0 ? $" {Report.Project.OnlineOnlyFiles} files are online-only; their size is not space used on this PC." : "")}"
        : null;

    public string Dependencies => Report.Dependencies.Summary;

    public IReadOnlyList<string> Reasons =>
    [
        .. Report.Dependencies.Reasons,
        .. Report.Dependencies.NotChecked.Select(n => $"Not checked: {n.Path}. {n.Reason}"),
    ];

    public string? Blocked { get; }

    public bool CanSelect => Blocked is null;

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value && CanSelect);
    }

    /// <summary>Not changed for at least the archive threshold (T-53).</summary>
    public bool IsInactive { get; private set; }

    /// <summary>What archiving would do for an inactive project; null otherwise. Never a suggestion to delete it.</summary>
    public string? Inactive { get; private set; }

    public void UpdateInactivity(DateTimeOffset nowUtc, int months)
    {
        IsInactive = ProjectArchive.IsInactive(Report.Project, nowUtc, months);
        Inactive = IsInactive ? ProjectArchive.Describe(Report.Project, months) : null;
        OnPropertyChanged(nameof(IsInactive));
        OnPropertyChanged(nameof(Inactive));
    }
}

/// <summary>
/// The Organize page (spec §5.2, T-52): suggested categories for loose files in Desktop, Downloads and Documents, and
/// the projects there, each with its evidence and paths. The user ticks items and picks a destination folder; the
/// policy checks the choice; the preview lists every move; the gateway moves the items, and Activity can undo it.
/// Folders with path dependencies can't be selected, and sensitive categories need a second confirmation. Projects
/// not changed for a threshold the user sets can be archived: moved whole into an archive folder the user chose (T-53).
/// </summary>
public sealed class OrganizeViewModel : PageViewModel
{
    private readonly OrganizeAnalysis analysis;
    private readonly ProtectionPolicy protection;
    private readonly ExclusionList exclusions;
    private readonly Func<RecommendationBuilder> builder;
    private readonly IFolderPicker picker;
    private readonly AppSettings settings;
    private readonly AppPaths paths;
    private readonly TimeProvider clock;
    private string archiveMonths;
    private string? archiveFolder;
    private string? archiveMessage;
    private string summary = "Nothing has been looked at yet. Look for suggestions to see what could be organized; nothing changes until you approve a preview.";
    private string? destination;
    private string? projectsMessage;
    private bool isBusy;

    public OrganizeViewModel(
        OrganizeAnalysis analysis,
        ProtectionPolicy protection,
        ExclusionList exclusions,
        Func<RecommendationBuilder> builder,
        IFolderPicker picker,
        AppSettings settings,
        AppPaths paths,
        TimeProvider clock)
        : base("Organize")
    {
        this.settings = settings;
        this.paths = paths;
        this.clock = clock;
        archiveMonths = settings.ArchiveAfterMonths.ToString(CultureInfo.CurrentCulture);
        SaveArchiveMonthsCommand = new RelayCommand(SaveArchiveMonths);
        BrowseArchiveCommand = new RelayCommand(() => ArchiveFolder = picker.Pick("Choose your archive folder", ArchiveFolder) ?? ArchiveFolder);
        ArchiveCommand = new RelayCommand(Archive);
        this.analysis = analysis;
        this.protection = protection;
        this.exclusions = exclusions;
        this.builder = builder;
        this.picker = picker;
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !IsBusy);
        BrowseCommand = new RelayCommand(Browse);
        MoveGroupCommand = new RelayCommand<OrganizeGroupViewModel>(MoveGroup, group => group is not null);
        MoveProjectsCommand = new RelayCommand(MoveProjects);
    }

    /// <summary>Raised when an accepted choice should be previewed before anything changes.</summary>
    public event EventHandler<ChosenRemoval>? PreviewRequested;

    public ObservableCollection<OrganizeGroupViewModel> Groups { get; } = [];

    public ObservableCollection<OrganizeProjectViewModel> Projects { get; } = [];

    public string Summary
    {
        get => summary;
        private set => SetProperty(ref summary, value);
    }

    /// <summary>The folder the user chose to move items into; nothing is moved until there is one.</summary>
    public string? Destination
    {
        get => destination;
        set
        {
            if (SetProperty(ref destination, value))
            {
                OnPropertyChanged(nameof(DestinationText));
            }
        }
    }

    public string DestinationText => Destination is null ? "No destination folder chosen yet." : $"Move to: {Destination}";

    public string ProjectsExplanation =>
        "Projects are found from their manifests, such as solution files, package.json or a .git folder. \"Not changed since\" is a fact about "
        + "the files, not a sign that a project is no longer needed. A project with paths that point outside its folder can't be selected, "
        + "because moving it could break it.";

    public string? ProjectsMessage
    {
        get => projectsMessage;
        set => SetProperty(ref projectsMessage, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                AnalyzeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public IAsyncRelayCommand AnalyzeCommand { get; }

    public IRelayCommand BrowseCommand { get; }

    public IRelayCommand<OrganizeGroupViewModel> MoveGroupCommand { get; }

    public IRelayCommand MoveProjectsCommand { get; }

    /// <summary>The archive threshold in months, as typed; saved by <see cref="SaveArchiveMonthsCommand"/>.</summary>
    public string ArchiveMonths
    {
        get => archiveMonths;
        set => SetProperty(ref archiveMonths, value);
    }

    public string ArchiveHelp =>
        $"Projects with no file changed for this many months ({AppSettings.MinArchiveMonths} to {AppSettings.MaxArchiveMonths}) can be archived: moved whole "
        + "into an archive folder you choose. Not being changed is not a sign a project is no longer needed, so nothing is ever deleted.";

    public string ArchiveSummary
    {
        get
        {
            var inactive = Projects.Count(p => p.IsInactive);
            return $"{inactive} {(inactive == 1 ? "project has" : "projects have")} not changed for {settings.ArchiveAfterMonths} months or more.";
        }
    }

    /// <summary>The folder the user chose to archive projects into.</summary>
    public string? ArchiveFolder
    {
        get => archiveFolder;
        set
        {
            if (SetProperty(ref archiveFolder, value))
            {
                OnPropertyChanged(nameof(ArchiveFolderText));
            }
        }
    }

    public string ArchiveFolderText => ArchiveFolder is null ? "No archive folder chosen yet." : $"Archive folder: {ArchiveFolder}";

    public string? ArchiveMessage
    {
        get => archiveMessage;
        set => SetProperty(ref archiveMessage, value);
    }

    public IRelayCommand SaveArchiveMonthsCommand { get; }

    public IRelayCommand BrowseArchiveCommand { get; }

    public IRelayCommand ArchiveCommand { get; }

    /// <summary>Takes items that were moved off the page, and groups that are then empty. Other groups keep their ticks.</summary>
    public void Moved(IReadOnlySet<string> paths)
    {
        for (var i = Groups.Count - 1; i >= 0; i--)
        {
            var group = Groups[i].Group;
            if (!group.Items.Any(s => paths.Contains(s.File.FullPath)))
            {
                continue;
            }

            var left = group with { Items = group.Items.Where(s => !paths.Contains(s.File.FullPath)).ToArray() };
            if (left.Items.Count == 0)
            {
                Groups.RemoveAt(i);
            }
            else
            {
                Groups[i] = new OrganizeGroupViewModel(left, Blocked);
            }
        }

        foreach (var project in Projects.Where(p => paths.Contains(p.Path)).ToArray())
        {
            Projects.Remove(project);
        }

        OnPropertyChanged(nameof(ArchiveSummary));
    }

    private async Task AnalyzeAsync()
    {
        IsBusy = true;
        Summary = "Looking at Desktop, Downloads and Documents…";
        try
        {
            Show(await analysis.RunAsync(CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Summary = $"The folders could not be looked at: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Shows a result; public so tests can drive the page without waiting on the background task.</summary>
    public void Show(OrganizeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Groups.Clear();
        Projects.Clear();
        if (exclusions.IsExcluded(FindingCategory.Organization))
        {
            Summary = "You excluded organizing in Settings, so nothing is suggested.";
            return;
        }

        foreach (var group in result.Triage.Groups)
        {
            Groups.Add(new OrganizeGroupViewModel(group, Blocked));
        }

        foreach (var project in result.Projects.OrderBy(p => p.Project.Root, StringComparer.OrdinalIgnoreCase))
        {
            var item = new OrganizeProjectViewModel(project, Blocked(project.Project.Root));
            item.UpdateInactivity(clock.GetUtcNow(), settings.ArchiveAfterMonths);
            Projects.Add(item);
        }

        OnPropertyChanged(nameof(ArchiveSummary));

        var looked = string.Join(", ", result.Folders.Select(f => f.Kind));
        var notRead = result.Triage.NotRead.Count == 0 ? "" : $" {result.Triage.NotRead.Count} paths could not be read, so this is not complete.";
        var excluded = result.Triage.Excluded.Count == 0 ? "" : $" {result.Triage.Excluded.Count} suggestions are hidden by your exclusions.";
        Summary = $"Looked in {looked}: {Groups.Sum(g => g.Items.Count)} suggested files in {Groups.Count} categories and {Projects.Count} projects. "
            + "These are suggestions, not certainties: check each one. Nothing moves until you choose a destination and approve the preview."
            + notRead + excluded;
    }

    private void Browse()
    {
        if (picker.Pick("Choose the folder to move items into", Destination) is { } chosen)
        {
            Destination = chosen;
        }
    }

    private void MoveGroup(OrganizeGroupViewModel? group)
    {
        if (group!.Sensitive && !group.Confirmed)
        {
            group.Message = $"These may be personal documents. Tick \"{group.ConfirmText}\" first.";
            return;
        }

        var chosen = group.Items.Where(i => i.IsSelected).Select(i => i.Suggestion.File).ToArray();
        group.Message = Request(
            chosen.Select(f => new MoveCandidate(f.FullPath, false, f.Size, f.LastWriteTimeUtc)).ToArray(),
            $"You chose to move these from the suggested category \"{FileTriage.Title(group.Group.Category)}\".");
    }

    private void MoveProjects()
    {
        var chosen = Projects.Where(p => p.IsSelected).Select(p => p.Report.Project).ToArray();
        ProjectsMessage = Request(
            chosen.Select(p => new MoveCandidate(p.Root, true, p.TotalSize)).ToArray(),
            "You chose to move these project folders. Their dependency scan found no paths outside them.");
    }

    private void SaveArchiveMonths()
    {
        if (!int.TryParse(ArchiveMonths, NumberStyles.Integer, CultureInfo.CurrentCulture, out var months)
            || months is < AppSettings.MinArchiveMonths or > AppSettings.MaxArchiveMonths)
        {
            ArchiveMessage = $"Enter a whole number of months from {AppSettings.MinArchiveMonths} to {AppSettings.MaxArchiveMonths}.";
            return;
        }

        settings.SetArchiveAfterMonths(months);
        settings.Save(paths.Settings);
        foreach (var project in Projects)
        {
            project.UpdateInactivity(clock.GetUtcNow(), months);
        }

        ArchiveMessage = null;
        OnPropertyChanged(nameof(ArchiveSummary));
    }

    private void Archive()
    {
        var chosen = Projects.Where(p => p.IsSelected && p.IsInactive).Select(p => p.Report.Project).ToArray();
        ArchiveMessage = chosen.Length == 0
            ? "Tick at least one project that has not changed for the set number of months."
            : Request(
                chosen.Select(p => new MoveCandidate(p.Root, true, p.TotalSize)).ToArray(),
                $"Not changed for {settings.ArchiveAfterMonths} months or more; you chose to archive it. It is moved whole, not deleted.",
                ArchiveFolder,
                "Choose your archive folder first.");
    }

    /// <returns>Why the choice was refused; null when it went to the preview.</returns>
    private string? Request(IReadOnlyCollection<MoveCandidate> items, string why) =>
        Request(items, why, Destination, "Choose the folder to move them to first.");

    private string? Request(IReadOnlyCollection<MoveCandidate> items, string why, string? folder, string noFolder)
    {
        if (items.Count == 0)
        {
            return "Choose at least one item to move.";
        }

        if (folder is null)
        {
            return noFolder;
        }

        var choice = builder().ForMove(items, folder, why);
        if (choice.Recommendation is { } recommendation)
        {
            PreviewRequested?.Invoke(this, new ChosenRemoval(recommendation, Moved));
        }

        return choice.Refusal;
    }

    private string? Blocked(string path) =>
        protection.CheckMove(path) is { IsBlocked: true } decision ? decision.Reason
        : exclusions.MatchPath(path) is { } exclusion ? $"you excluded '{exclusion.Value}'."
        : null;
}

using System.IO;
using PCHealth.Core;
using PCHealth.Inventory;
using PCHealth.Organization;
using PCHealth.Policy;

namespace PCHealth.App;

/// <summary>A folder the Organize page looks in.</summary>
public sealed record OrganizeFolder(KnownFolder Kind, string Path);

/// <summary>Where this user's Desktop, Downloads and Documents are.</summary>
public interface IOrganizeFolders
{
    IReadOnlyList<OrganizeFolder> Find();
}

/// <summary>A top-level project with what the dependency scan found in it.</summary>
public sealed record ProjectReport(DetectedProject Project, DependencyReport Dependencies);

/// <param name="Folders">The folders that were looked in.</param>
public sealed record OrganizeResult(IReadOnlyList<OrganizeFolder> Folders, TriageResult Triage, IReadOnlyList<ProjectReport> Projects);

/// <summary>
/// The Organize page's read-only analysis (spec §5.2, T-52): it scans Desktop, Downloads and Documents, suggests
/// categories for loose files, finds projects and scans each for path dependencies. Files inside a project are left
/// out of the loose-file suggestions, because moving one alone could break the project. Nothing here changes a file.
/// </summary>
public sealed class OrganizeAnalysis(IOrganizeFolders folders, IInventoryScanner scanner, ExclusionList exclusions)
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public Task<OrganizeResult> RunAsync(CancellationToken cancellationToken) => Task.Run(() => Run(cancellationToken), cancellationToken);

    public OrganizeResult Run(CancellationToken cancellationToken = default)
    {
        var found = folders.Find();
        var scanned = new List<ScannedFolder>();
        var projects = new List<ProjectReport>();
        foreach (var folder in found)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scan = scanner.Scan(folder.Path, cancellationToken: cancellationToken);
            scanned.Add(new ScannedFolder(folder.Kind, folder.Path, scan));

            // A project's nested projects move with it, so only the outermost ones are offered. A manifest directly in
            // Desktop or Documents does not make the whole folder a project.
            var root = Path.TrimEndingDirectorySeparator(folder.Path);
            foreach (var project in ProjectDetector.Find(scan).Where(p => !Same(p.Root, root) && (p.ParentRoot is null || Same(p.ParentRoot, root))))
            {
                cancellationToken.ThrowIfCancellationRequested();
                projects.Add(new ProjectReport(project, DependencyScanner.Scan(project)));
            }
        }

        var triage = new FileTriage(exclusions).Suggest(scanned);
        var roots = projects.Select(p => p.Project.Root).ToArray();
        var loose = triage.Groups
            .Select(g => g with { Items = g.Items.Where(i => !roots.Any(r => i.File.FullPath.StartsWith(r + Path.DirectorySeparatorChar, PathComparison))).ToArray() })
            .Where(g => g.Items.Count > 0)
            .ToArray();
        return new OrganizeResult(found, triage with { Groups = loose }, projects);
    }

    private static bool Same(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), b, PathComparison);
}

/// <summary>This user's Desktop, Downloads and Documents from the known-folder API.</summary>
public sealed class WindowsOrganizeFolders(Execution.IPathResolver resolver) : IOrganizeFolders
{
    public IReadOnlyList<OrganizeFolder> Find() =>
        Execution.Windows.WindowsUserFolders.Find(resolver)
            .Select(f => new OrganizeFolder(Enum.Parse<KnownFolder>(f.Name), f.Path))
            .ToArray();
}

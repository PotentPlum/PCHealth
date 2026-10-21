using System.Text.Json;
using System.Text.Json.Serialization;
using PCHealth.Core;

namespace PCHealth.Policy;

public enum ExclusionKind
{
    File,

    /// <summary>The folder and everything below it.</summary>
    Folder,
    Category,
}

/// <summary>One thing the user asked the product to leave alone (spec §3.2, §6.5).</summary>
/// <param name="Value">A full path for files and folders, or a <see cref="FindingCategory"/> name.</param>
public sealed record Exclusion(ExclusionKind Kind, string Value, DateTimeOffset AddedUtc, string? Note = null);

/// <summary>A finding that an exclusion suppressed, and which exclusion did it.</summary>
public sealed record ExcludedFinding(Finding Finding, Exclusion Exclusion);

public sealed record ExclusionResult(IReadOnlyList<Finding> Kept, IReadOnlyList<ExcludedFinding> Excluded);

/// <summary>
/// The user's file, folder and category exclusions. Finding builders should check <see cref="IsExcluded(string)"/>
/// before they look at a path; <see cref="Apply"/> is the backstop that drops any finding still touching an exclusion.
/// </summary>
public sealed class ExclusionList
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly List<Exclusion> entries = [];

    public IReadOnlyList<Exclusion> Entries => entries;

    public Exclusion ExcludeFile(string path, DateTimeOffset nowUtc, string? note = null) =>
        Add(new Exclusion(ExclusionKind.File, Normalize(path), nowUtc, note));

    public Exclusion ExcludeFolder(string path, DateTimeOffset nowUtc, string? note = null) =>
        Add(new Exclusion(ExclusionKind.Folder, Normalize(path), nowUtc, note));

    public Exclusion ExcludeCategory(FindingCategory category, DateTimeOffset nowUtc, string? note = null)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        return Add(new Exclusion(ExclusionKind.Category, category.ToString(), nowUtc, note));
    }

    public bool Remove(Exclusion exclusion) => entries.RemoveAll(e => Same(e, exclusion)) > 0;

    public bool IsExcluded(string path) => MatchPath(path) is not null;

    public bool IsExcluded(FindingCategory category) => MatchCategory(category) is not null;

    /// <summary>The exclusion covering <paramref name="path"/>, if any.</summary>
    public Exclusion? MatchPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var target = Normalize(path);
        return entries.FirstOrDefault(e => e.Kind switch
        {
            ExclusionKind.File => PathComparer.Equals(e.Value, target),
            ExclusionKind.Folder => PathComparer.Equals(e.Value, target) || IsBelow(target, e.Value),
            _ => false,
        });
    }

    public Exclusion? MatchCategory(FindingCategory category) =>
        entries.FirstOrDefault(e => e.Kind == ExclusionKind.Category && e.Value == category.ToString());

    /// <summary>
    /// Drops findings in an excluded category, and findings whose evidence names an excluded path. This is
    /// deliberately conservative: a finding that touches an excluded path at all is withheld, not trimmed.
    /// </summary>
    public ExclusionResult Apply(IEnumerable<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var kept = new List<Finding>();
        var excluded = new List<ExcludedFinding>();
        foreach (var finding in findings)
        {
            var match = MatchCategory(finding.Category)
                ?? EvidencePaths(finding).Select(MatchPath).FirstOrDefault(m => m is not null);
            if (match is null)
            {
                kept.Add(finding);
            }
            else
            {
                excluded.Add(new ExcludedFinding(finding, match));
            }
        }

        return new ExclusionResult(kept, excluded);
    }

    /// <summary>Reads exclusions from a local JSON file. A missing file means no exclusions.</summary>
    /// <exception cref="InvalidDataException">The file exists but is not a valid exclusion list.</exception>
    public static ExclusionList Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var list = new ExclusionList();
        if (!File.Exists(filePath))
        {
            return list;
        }

        Document? document;
        try
        {
            using var stream = File.OpenRead(filePath);
            document = JsonSerializer.Deserialize<Document>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{filePath}' is not a valid exclusion list.", ex);
        }

        if (document?.Version != Document.CurrentVersion || document.Exclusions is null)
        {
            throw new InvalidDataException($"'{filePath}' has an unsupported format.");
        }

        foreach (var exclusion in document.Exclusions)
        {
            if (!Enum.IsDefined(exclusion.Kind) || string.IsNullOrWhiteSpace(exclusion.Value)
                || (exclusion.Kind == ExclusionKind.Category && !Enum.TryParse<FindingCategory>(exclusion.Value, out _))
                || (exclusion.Kind != ExclusionKind.Category && !Path.IsPathFullyQualified(exclusion.Value)))
            {
                throw new InvalidDataException($"'{filePath}' contains an invalid exclusion.");
            }

            list.Add(exclusion);
        }

        return list;
    }

    /// <summary>Writes the list to a local JSON file, replacing the old file only once the new one is complete.</summary>
    public void Save(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var temporary = filePath + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, new Document(Document.CurrentVersion, entries), JsonOptions);
        }

        File.Move(temporary, filePath, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private Exclusion Add(Exclusion exclusion)
    {
        var existing = entries.FirstOrDefault(e => Same(e, exclusion));
        if (existing is not null)
        {
            return existing;
        }

        entries.Add(exclusion);
        return exclusion;
    }

    private static bool Same(Exclusion a, Exclusion b) =>
        a.Kind == b.Kind && (a.Kind == ExclusionKind.Category ? a.Value == b.Value : PathComparer.Equals(a.Value, b.Value));

    private static IEnumerable<string> EvidencePaths(Finding finding) =>
        finding.Evidence.Select(e => e switch
        {
            PathEvidence p => p.Path,
            FileContentEvidence f => f.Path,
            InaccessibleEvidence i => i.Path,
            _ => null,
        }).OfType<string>();

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"Exclusions need a full path: '{path}'.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsBelow(string path, string folder) =>
        path.StartsWith(folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar, PathComparison);

    private sealed record Document(int Version, IReadOnlyList<Exclusion>? Exclusions)
    {
        public const int CurrentVersion = 1;
    }
}

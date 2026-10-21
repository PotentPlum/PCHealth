using System.IO.Enumeration;
using System.Text.Json;
using PCHealth.Core;
using PCHealth.Inventory;

namespace PCHealth.Policy.Catalog;

/// <summary>Where a token such as <c>{LocalAppData}</c> points on this PC. Windows code implements it with known folders.</summary>
public interface IKnownLocations
{
    /// <summary>The folder for a token name (without braces), or null if this PC has none.</summary>
    string? Resolve(string token);
}

/// <summary>One well-understood kind of regenerable data (spec §5.1).</summary>
/// <param name="Location">A path built from a token and segments, e.g. <c>{LocalAppData}/Google/Chrome/User Data/*/Cache</c>; one segment may be <c>*</c>.</param>
/// <param name="FilePattern">Only files matching this name pattern count, e.g. <c>*.dmp</c>; null means all files.</param>
/// <param name="MinAgeDays">Only files not written for at least this many days count; null means any age.</param>
/// <param name="Rebuild">What gets rebuilt or downloaded afterwards, shown to the user.</param>
public sealed record CacheRule(string Id, string Name, string Location, string Why, string Rebuild, string? FilePattern = null, int? MinAgeDays = null);

/// <summary>A rule that did not produce a finding, and why.</summary>
public sealed record SkippedRule(CacheRule Rule, string Location, string Reason);

public sealed record CatalogResult(IReadOnlyList<Finding> Findings, IReadOnlyList<SkippedRule> Skipped);

/// <summary>
/// Turns the data-driven cache rules into findings. Discovery only: locations are scanned read-only and every
/// location is checked against the protection policy and the user's exclusions first.
/// </summary>
public sealed class CacheCatalog
{
    private const string ResourceName = "PCHealth.Policy.Catalog.known-caches.json";

    public CacheCatalog(IReadOnlyList<CacheRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var rule in rules)
        {
            Validate(rule);
        }

        if (rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rules.Count)
        {
            throw new ArgumentException("Rule ids must be unique.", nameof(rules));
        }

        Rules = rules;
    }

    public IReadOnlyList<CacheRule> Rules { get; }

    /// <summary>The catalog shipped with the product.</summary>
    public static CacheCatalog LoadDefault()
    {
        using var stream = typeof(CacheCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}.");
        var document = JsonSerializer.Deserialize<Document>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The cache catalog is empty.");
        if (document.Version != 1 || document.Rules is null)
        {
            throw new InvalidDataException("The cache catalog has an unsupported format.");
        }

        return new CacheCatalog(document.Rules);
    }

    public CatalogResult Evaluate(
        IKnownLocations locations,
        IInventoryScanner scanner,
        ProtectionPolicy protection,
        ExclusionList exclusions,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(exclusions);

        var findings = new List<Finding>();
        var skipped = new List<SkippedRule>();
        if (exclusions.IsExcluded(FindingCategory.Cache))
        {
            return new CatalogResult(findings, Rules.Select(r => new SkippedRule(r, r.Location, "You excluded cache findings.")).ToArray());
        }

        foreach (var rule in Rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folders = Expand(rule, locations, out var unresolved);
            if (unresolved is not null)
            {
                skipped.Add(new SkippedRule(rule, rule.Location, unresolved));
                continue;
            }

            foreach (var folder in folders)
            {
                if (protection.Check(folder) is { IsBlocked: true } blocked)
                {
                    skipped.Add(new SkippedRule(rule, folder, blocked.Reason));
                }
                else if (exclusions.MatchPath(folder) is { } exclusion)
                {
                    skipped.Add(new SkippedRule(rule, folder, $"You excluded '{exclusion.Value}'."));
                }
                else if (Evaluate(rule, folder, scanner, protection, exclusions, nowUtc, cancellationToken) is { } finding)
                {
                    findings.Add(finding);
                }
                else
                {
                    skipped.Add(new SkippedRule(rule, folder, "Nothing matching this rule was found."));
                }
            }
        }

        return new CatalogResult(findings, skipped);
    }

    private static Finding? Evaluate(
        CacheRule rule,
        string folder,
        IInventoryScanner scanner,
        ProtectionPolicy protection,
        ExclusionList exclusions,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var scan = scanner.Scan(folder, cancellationToken: cancellationToken);
        var cutoff = rule.MinAgeDays is { } days ? nowUtc.AddDays(-days) : (DateTimeOffset?)null;
        var files = scan.Entries
            .OfType<FileEntry>()
            .Where(f => !f.IsReparsePoint)
            .Where(f => rule.FilePattern is null || FileSystemName.MatchesSimpleExpression(rule.FilePattern, f.Name))
            .Where(f => cutoff is null || f.LastWriteTimeUtc <= cutoff)
            .Where(f => !protection.Check(f.FullPath).IsBlocked && !exclusions.IsExcluded(f.FullPath))
            .ToArray();
        if (files.Length == 0)
        {
            return null;
        }

        var bytes = files.Aggregate(MeasuredSize.Zero, (total, f) => total + f.Size);
        var evidence = new List<Evidence>
        {
            new PathEvidence(folder, $"Known location of {rule.Name.ToLowerInvariant()}."),
            new MeasurementEvidence(bytes, $"{files.Length} matching files, measured during this scan."),
        };
        evidence.AddRange(scan.Inaccessible);

        var filter = string.Join(", ", new[]
        {
            rule.FilePattern is null ? null : $"named {rule.FilePattern}",
            rule.MinAgeDays is null ? null : $"unchanged for {rule.MinAgeDays} days or more",
        }.OfType<string>());
        var confidence = scan.IsComplete
            ? new Confidence(ConfidenceLevel.High, $"'{folder}' is a known location for {rule.Name.ToLowerInvariant()} (rule {rule.Id}).")
            : new Confidence(ConfidenceLevel.Medium, $"Known location (rule {rule.Id}), but {scan.Inaccessible.Count} folders could not be read, so the totals are incomplete.");

        return new Finding(
            FindingCategory.Cache,
            rule.Name,
            rule.Why,
            evidence,
            confidence,
            new Impact(
                $"Moves {files.Length} files{(filter.Length > 0 ? $" ({filter})" : "")} in '{folder}' to the Recycle Bin.",
                new EstimatedSize(bytes.Bytes, "sum of the matching files' sizes; space returns when the Recycle Bin is emptied"),
                rule.Rebuild),
            new Recovery(RecoveryMethod.RecycleBin, "Files can be restored from the Recycle Bin until it is emptied. Files in use are skipped."));
    }

    /// <summary>Expands the rule's location into existing folders; a single <c>*</c> segment matches each subfolder that is not a link.</summary>
    private static IReadOnlyList<string> Expand(CacheRule rule, IKnownLocations locations, out string? unresolved)
    {
        unresolved = null;
        var segments = rule.Location.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var token = segments[0][1..^1];
        if (locations.Resolve(token) is not { } basePath)
        {
            unresolved = $"This PC has no {token} folder.";
            return [];
        }

        IEnumerable<string> current = [basePath];
        foreach (var segment in segments.Skip(1))
        {
            current = segment == "*"
                ? current.SelectMany(SubfoldersNotLinks)
                : current.Select(p => Path.Combine(p, segment));
        }

        return current.Where(p => Directory.Exists(p) && !File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)).ToArray();
    }

    private static IEnumerable<string> SubfoldersNotLinks(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            return new DirectoryInfo(folder)
                .EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(d => d.FullName)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static void Validate(CacheRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Name) || string.IsNullOrWhiteSpace(rule.Why))
        {
            throw new ArgumentException("A cache rule needs an id, a name and a reason.");
        }

        if (string.IsNullOrWhiteSpace(rule.Rebuild))
        {
            throw new ArgumentException($"Rule {rule.Id} must say what is rebuilt or downloaded afterwards.");
        }

        var segments = rule.Location?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (segments.Length == 0 || segments[0].Length < 3 || segments[0][0] != '{' || segments[0][^1] != '}'
            || segments.Skip(1).Any(s => s is "." or ".." || (s.Contains('*') && s != "*"))
            || segments.Count(s => s == "*") > 1)
        {
            throw new ArgumentException($"Rule {rule.Id} has an invalid location '{rule.Location}'.");
        }

        if (rule.MinAgeDays is < 0)
        {
            throw new ArgumentException($"Rule {rule.Id} has a negative minimum age.");
        }
    }

    private sealed record Document(int Version, IReadOnlyList<CacheRule>? Rules);
}

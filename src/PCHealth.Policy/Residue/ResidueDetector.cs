using System.Text.RegularExpressions;
using PCHealth.Core;
using PCHealth.Inventory;
using PCHealth.Policy.Catalog;

namespace PCHealth.Policy.Residue;

/// <summary>A folder that was looked at and produced no finding, and why.</summary>
public sealed record SkippedFolder(string Path, string Reason);

public sealed record ResidueResult(IReadOnlyList<Finding> Findings, IReadOnlyList<SkippedFolder> Skipped);

/// <summary>
/// Finds application data that no installed application or package claims (spec §5.1). Each top-level folder in
/// AppData (Local and Roaming) and ProgramData, and each package data folder in <c>LocalAppData\Packages</c>, is
/// cross-referenced with uninstall entries and AppX/MSIX packages. Discovery only: folders are scanned read-only.
/// A folder becomes a likely-orphaned finding only when every check agrees; incomplete or contradictory evidence
/// produces an uncertain, review-only finding instead.
/// </summary>
public static partial class ResidueDetector
{
    /// <summary>A file written more recently than this suggests something still uses the folder.</summary>
    public const int RecentActivityDays = 30;

    private static readonly string[] RootTokens = ["LocalAppData", "RoamingAppData", "ProgramData"];

    // Folders Windows itself creates in these roots; they are never residue candidates.
    private static readonly HashSet<string> WindowsOwned = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Programs", "Temp", "Publishers", "Comms", "ConnectedDevicesPlatform", "CrashDumps",
        "D3DSCache", "VirtualStore", "PackageManagement", "Package Cache", "PeerDistRepub", "PlaceholderTileLogoFolder",
        "ssh", "USOPrivate", "USOShared", "SoftwareDistribution", "regid.1991-06.com.microsoft", "WindowsHolographicDevices",
        "Application Data", "History", "Temporary Internet Files", "Desktop", "Documents", "Start Menu", "Templates",
        "Favorites", "IsolatedStorage",
    };

    // Words that say nothing about who owns a folder, so sharing one is not a resemblance.
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "software", "data", "app", "apps", "application", "update", "updater", "service", "services", "user", "users",
        "cache", "game", "games", "studio", "tools", "tool", "client", "launcher", "helper", "manager", "player", "free",
        "online", "desktop", "windows", "microsoft", "system", "driver", "drivers", "runtime", "edition", "plugin",
        "plugins", "setup", "installer", "version", "home", "files", "common", "shared", "local", "roaming", "program",
        "programs", "package", "packages", "settings", "config",
    };

    // Company-form suffixes ("Contoso Ltd.") and architecture markers, dropped before names are compared.
    private static readonly HashSet<string> NoiseWords = new(StringComparer.Ordinal)
    {
        "inc", "llc", "ltd", "corp", "corporation", "gmbh", "co", "company", "limited", "ag", "sa", "bv", "plc", "pty",
        "srl", "sro", "x64", "x86", "arm64", "bit",
    };

    public static ResidueResult Evaluate(
        IKnownLocations locations,
        IAppRegistrations registrations,
        IInventoryScanner scanner,
        ProtectionPolicy protection,
        ExclusionList exclusions,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(exclusions);

        var findings = new List<Finding>();
        var skipped = new List<SkippedFolder>();
        var roots = RootTokens.Select(t => (Token: t, Path: locations.Resolve(t))).ToArray();
        if (exclusions.IsExcluded(FindingCategory.AppResidue))
        {
            skipped.AddRange(roots.Where(r => r.Path is not null).Select(r => new SkippedFolder(r.Path!, "You excluded app residue findings.")));
            return new ResidueResult(findings, skipped);
        }

        var snapshot = registrations.Read();
        var context = new Context(snapshot, scanner, protection, exclusions, nowUtc, findings, skipped, cancellationToken);
        foreach (var (token, root) in roots)
        {
            if (root is null)
            {
                skipped.Add(new SkippedFolder($"{{{token}}}", $"This PC has no {token} folder."));
                continue;
            }

            foreach (var folder in Subfolders(root, skipped))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(folder);
                if (token == "LocalAppData" && string.Equals(name, "Packages", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var packageFolder in Subfolders(folder, skipped))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        context.Evaluate(packageFolder, MatchPackage(Path.GetFileName(packageFolder), snapshot));
                    }
                }
                else if (WindowsOwned.Contains(name))
                {
                    skipped.Add(new SkippedFolder(folder, "Windows creates and uses this folder."));
                }
                else
                {
                    context.Evaluate(folder, MatchFolder(folder, snapshot));
                }
            }
        }

        return new ResidueResult(findings, skipped);
    }

    private sealed record Ownership(AppRegistration? Owner, IReadOnlyList<AppRegistration> Related);

    private sealed record Context(
        RegistrationSnapshot Snapshot,
        IInventoryScanner Scanner,
        ProtectionPolicy Protection,
        ExclusionList Exclusions,
        DateTimeOffset NowUtc,
        List<Finding> Findings,
        List<SkippedFolder> Skipped,
        CancellationToken CancellationToken)
    {
        public void Evaluate(string folder, Ownership ownership)
        {
            if (Protection.Check(folder) is { IsBlocked: true } blocked)
            {
                Skipped.Add(new SkippedFolder(folder, blocked.Reason));
                return;
            }

            if (Exclusions.MatchPath(folder) is { } exclusion)
            {
                Skipped.Add(new SkippedFolder(folder, $"You excluded '{exclusion.Value}'."));
                return;
            }

            if (ownership.Owner is { } owner)
            {
                Skipped.Add(new SkippedFolder(folder, $"Belongs to '{owner.DisplayName}' ({Label(owner.Source)})."));
                return;
            }

            var scan = Scanner.Scan(folder, cancellationToken: CancellationToken);
            var files = scan.Entries.OfType<FileEntry>().Where(f => !f.IsReparsePoint).ToArray();
            if (files.Length == 0 && scan.IsComplete)
            {
                Skipped.Add(new SkippedFolder(folder, "The folder holds no files."));
                return;
            }

            Findings.Add(Build(folder, ownership, scan, files));
        }

        private Finding Build(string folder, Ownership ownership, ScanResult scan, FileEntry[] files)
        {
            var name = Path.GetFileName(folder);
            var bytes = files.Aggregate(MeasuredSize.Zero, (total, f) => total + f.Size);
            var newest = files.Length == 0 ? (DateTimeOffset?)null : files.Max(f => f.LastWriteTimeUtc);
            var binaries = files.Where(f => ProtectionPolicy.BinaryExtensions.Contains(Path.GetExtension(f.Name))).ToArray();
            var uninstallCount = Snapshot.Count(RegistrationSource.UninstallEntry);
            var packageCount = Snapshot.Count(RegistrationSource.Package);

            var evidence = new List<Evidence>
            {
                new PathEvidence(folder, "Application data folder."),
                new MeasurementEvidence(bytes, $"{files.Length} files, measured during this scan."),
                new CheckEvidence("registrations", $"Checked {uninstallCount} uninstall entries and {packageCount} packages; none of them claims '{name}'."),
            };
            evidence.AddRange(ownership.Related.Select(r => new RegistrationEvidence(
                Label(r.Source), r.DisplayName, $"'{r.DisplayName}' ({Label(r.Source)}) is installed and has a similar name.")));
            if (newest is { } last)
            {
                evidence.Add(new CheckEvidence("last activity", $"The newest file in it was written {last:u}."));
            }

            if (binaries.Length > 0)
            {
                evidence.Add(new CheckEvidence("program files", $"It contains {binaries.Length} program files, e.g. '{binaries[0].Name}'."));
            }

            evidence.AddRange(scan.Inaccessible);

            // Every doubt is reported; any one of them makes the finding uncertain.
            var doubts = new List<string>();
            doubts.AddRange(Snapshot.Gaps.Select(g => $"Registrations are incomplete: {g}"));
            doubts.AddRange(ownership.Related.Select(r => $"Its name resembles '{r.DisplayName}', which is installed."));
            if (!scan.IsComplete)
            {
                doubts.Add($"{scan.Inaccessible.Count} items in it could not be read.");
            }

            if (binaries.Length > 0)
            {
                doubts.Add("It contains program files, so it may be an application that runs from here.");
            }

            if (newest is { } recent && recent > NowUtc.AddDays(-RecentActivityDays))
            {
                doubts.Add($"A file in it was written {(int)(NowUtc - recent).TotalDays} days ago, so an application may still use it.");
            }

            var estimate = new EstimatedSize(bytes.Bytes, "measured size of the folder; space returns when it leaves quarantine");
            var recovery = new Recovery(
                RecoveryMethod.Quarantine,
                "The folder can be restored to its original path while it is in quarantine, unless something new has been created there.");
            var operation = new[] { new ProposedOperation(OperationKind.MoveToQuarantine, folder, isDirectory: true, bytes) };
            const string Aftereffects = "If an application still uses this data, it may lose its settings or have to recreate them.";

            if (doubts.Count > 0)
            {
                return new Finding(
                    FindingCategory.AppResidue,
                    $"Unclear ownership: {name}",
                    "This folder may be left over from uninstalled software, but the evidence is incomplete or contradictory, so it is listed for review only.",
                    evidence,
                    new Confidence(ConfidenceLevel.Low, "Uncertain. " + string.Join(" ", doubts)),
                    new Impact($"Moves the folder '{folder}' ({files.Length} files) to quarantine.", estimate, Aftereffects),
                    recovery,
                    reviewOnly: true,
                    operations: operation);
            }

            return new Finding(
                FindingCategory.AppResidue,
                $"Possible leftover data: {name}",
                "No installed application or package claims this folder, so it is probably left over from software that was uninstalled.",
                evidence,
                new Confidence(
                    ConfidenceLevel.Medium,
                    $"No uninstall entry or package claims '{name}', nothing in it was written in the last {RecentActivityDays} days, and it holds no program files. Folder names are only a clue to ownership, so this is not certain."),
                new Impact($"Moves the folder '{folder}' ({files.Length} files) to quarantine.", estimate, Aftereffects),
                recovery,
                operations: operation);
        }
    }

    /// <summary>A package data folder is named after its package family; a family with the same name but another publisher is only related.</summary>
    private static Ownership MatchPackage(string familyName, RegistrationSnapshot snapshot)
    {
        var packages = snapshot.Registrations.Where(r => r.Source == RegistrationSource.Package).ToArray();
        var owner = packages.FirstOrDefault(p => string.Equals(p.Id, familyName, StringComparison.OrdinalIgnoreCase));
        var related = owner is null
            ? packages.Where(p => string.Equals(PackageName(p.Id), PackageName(familyName), StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];
        return new Ownership(owner, related);
    }

    /// <summary>
    /// Owned when a registration installs into the folder, or its display name, publisher or install folder name is
    /// the folder's name (or starts with it, as "Mozilla Firefox" does for "Mozilla"). Related when the names only
    /// overlap; that is contradictory evidence, not ownership.
    /// </summary>
    private static Ownership MatchFolder(string folder, RegistrationSnapshot snapshot)
    {
        var words = Words(Path.GetFileName(folder));
        var joined = string.Concat(words);
        var related = new List<AppRegistration>();
        foreach (var registration in snapshot.Registrations)
        {
            if (registration.InstallLocation is { Length: > 0 } location && Path.IsPathFullyQualified(location)
                && ProtectionPolicy.IsSameOrBelow(Path.TrimEndingDirectorySeparator(location), folder))
            {
                return new Ownership(registration, []);
            }

            if (joined.Length == 0)
            {
                continue;
            }

            var resemblance = false;
            foreach (var candidate in Names(registration))
            {
                var other = Words(candidate);
                var otherJoined = string.Concat(other);
                if (otherJoined.Length == 0)
                {
                    continue;
                }

                if (otherJoined == joined || other.Take(words.Length).SequenceEqual(words))
                {
                    return new Ownership(registration, []);
                }

                resemblance |= Contains(joined, otherJoined) || Contains(otherJoined, joined)
                    || words.Intersect(other).Any(w => w.Length >= 4 && !GenericWords.Contains(w));
            }

            if (resemblance)
            {
                related.Add(registration);
            }
        }

        return new Ownership(null, related);
    }

    private static IEnumerable<string> Names(AppRegistration registration)
    {
        yield return registration.Source == RegistrationSource.Package ? PackageName(registration.Id) : registration.DisplayName;
        if (registration.Source == RegistrationSource.Package)
        {
            yield return registration.DisplayName;
        }

        if (registration.Publisher is { Length: > 0 } publisher)
        {
            yield return publisher;
        }

        if (registration.InstallLocation is { Length: > 0 } location
            && Path.GetFileName(Path.TrimEndingDirectorySeparator(location)) is { Length: > 0 } leaf)
        {
            yield return leaf;
        }
    }

    private static bool Contains(string longer, string shorter) => shorter.Length >= 4 && longer.Contains(shorter, StringComparison.Ordinal);

    /// <summary>Lower-case words without numbers, versions, company forms or single letters.</summary>
    private static string[] Words(string? text) =>
        text is null
            ? []
            : NonAlphanumeric().Split(text.ToLowerInvariant())
                .Where(w => w.Length > 1 && !w.All(char.IsAsciiDigit) && !NoiseWords.Contains(w))
                .ToArray();

    /// <summary>The name part of a package family name (<c>Name_PublisherId</c>).</summary>
    private static string PackageName(string familyName) =>
        familyName.LastIndexOf('_') is var i and > 0 ? familyName[..i] : familyName;

    private static string Label(RegistrationSource source) => source switch
    {
        RegistrationSource.Package => "package",
        _ => "uninstall entry",
    };

    private static IReadOnlyList<string> Subfolders(string folder, List<SkippedFolder> skipped)
    {
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
                .Select(d => d.FullName)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            skipped.Add(new SkippedFolder(folder, $"The folder could not be listed: {ex.Message}"));
            return [];
        }
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlphanumeric();
}

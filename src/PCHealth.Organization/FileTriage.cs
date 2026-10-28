using System.IO.Compression;
using System.Text.RegularExpressions;
using PCHealth.Core;
using PCHealth.Duplicates;
using PCHealth.Inventory;
using PCHealth.Policy;

namespace PCHealth.Organization;

public enum KnownFolder
{
    Desktop,
    Downloads,
    Documents,
}

/// <summary>One of the user's folders, as scanned by a read-only inventory.</summary>
public sealed record ScannedFolder(KnownFolder Kind, string Path, ScanResult Scan);

/// <summary>One file suggested for a category, with how sure the rule is and why.</summary>
public sealed record TriageSuggestion(FileEntry File, KnownFolder Folder, TriageCategory Category, Confidence Confidence, IReadOnlyList<Evidence> Evidence)
{
    public bool Sensitive => FileTriage.IsSensitive(Category);

    /// <summary>Only non-sensitive suggestions with High confidence start ticked; the user can still untick them.</summary>
    public bool Preselected => !Sensitive && Confidence.Level >= ConfidenceLevel.High;
}

/// <summary>A suggested category and the files in it. Always a suggestion: nothing moves without approval (spec §5.2).</summary>
public sealed record TriageGroup(TriageCategory Category, string Title, string Explanation, IReadOnlyList<TriageSuggestion> Items)
{
    /// <summary>The category may hold personal or sensitive documents. Its items are never preselected.</summary>
    public bool Sensitive => FileTriage.IsSensitive(Category);
}

public sealed record ExcludedSuggestion(TriageSuggestion Suggestion, Exclusion Exclusion);

/// <param name="NotRead">Paths that could not be read, by the scan or for their properties; the result is partial for them.</param>
public sealed record TriageResult(IReadOnlyList<TriageGroup> Groups, IReadOnlyList<ExcludedSuggestion> Excluded, IReadOnlyList<InaccessibleEvidence> NotRead);

/// <summary>
/// Rule-based suggestions for loose files in Desktop, Downloads and Documents (spec §5.2, T-48). Read-only: it looks at
/// names, sizes, times, sibling folders, zip listings and document properties, and never changes a file. A file gets
/// at most one category; the sensitive categories are tried first, so a possible tax document is never filed under a
/// category that may be preselected.
/// </summary>
public sealed partial class FileTriage(ExclusionList exclusions)
{
    /// <summary>Most entries a zip listing is compared against its folder; larger archives are matched by name only.</summary>
    internal const int MaxZipEntries = 10_000;

    private static readonly HashSet<string> PackageInstallers = Set(".msi", ".msix", ".msixbundle", ".appx", ".appxbundle");
    private static readonly HashSet<string> DiskImageExtensions = Set(".iso", ".vhd", ".vhdx", ".vmdk", ".qcow2", ".dmg");
    private static readonly HashSet<string> ArchiveExtensions = Set(".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz");
    private static readonly HashSet<string> ImageExtensions = Set(".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".heic");
    private static readonly HashSet<string> DocumentExtensions = Set(".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ods", ".odt", ".csv", ".txt", ".rtf");
    private static readonly HashSet<string> FinancialFormats = Set(".ofx", ".qfx", ".qif", ".mt940");

    private static readonly string[] InstallerWords = ["setup", "install", "installer", "installation", "installs"];

    private static readonly string[] ScreenshotPrefixes =
        ["screenshot", "screen shot", "screen_shot", "schermafbeelding", "bildschirmfoto", "capture d", "captura de pantalla", "knipsel", "snip"];

    // Whole words, or the start of a longer word for the longer keywords ("belastingaangifte", "facturen").
    private static readonly string[] FinancialWords =
    [
        "invoice", "invoices", "receipt", "receipts", "tax", "taxes", "vat", "payslip", "paystub", "salary", "mortgage", "pension",
        "w2", "1099", "1040", "btw", "factuur", "facturen", "kwitantie", "belasting", "aangifte", "jaaropgave",
        "loonstrook", "salaris", "hypotheek", "pensioen", "rekeningafschrift", "bankafschrift", "rechnung", "steuer",
    ];

    private static readonly string[] SignedWords =
        ["signed", "esigned", "signature", "docusign", "getekend", "ondertekend", "handtekening", "unterschrieben", "unterschrift"];

    private readonly ExclusionList exclusions = exclusions ?? throw new ArgumentNullException(nameof(exclusions));

    public static bool IsSensitive(TriageCategory category) =>
        category is TriageCategory.FinancialDocuments or TriageCategory.SignedDocuments;

    public TriageResult Suggest(IReadOnlyList<ScannedFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var suggestions = new List<TriageSuggestion>();
        var notRead = new List<InaccessibleEvidence>();
        foreach (var folder in folders)
        {
            notRead.AddRange(folder.Scan.Inaccessible);
            var all = folder.Scan.Entries.OfType<FileEntry>().Where(f => !f.IsReparsePoint).ToArray();
            var files = all.Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Attributes.HasFlag(FileAttributes.System)).ToArray();
            var context = new FolderContext(folder, all);
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var suggestion = Classify(context, file, notRead);
                if (suggestion is not null)
                {
                    suggestions.Add(suggestion);
                    assigned.Add(file.FullPath);
                }
            }

            suggestions.AddRange(Superseded(folder.Kind, files.Where(f => !assigned.Contains(f.FullPath))));
        }

        var kept = new List<TriageSuggestion>();
        var excluded = new List<ExcludedSuggestion>();
        foreach (var suggestion in suggestions)
        {
            var match = exclusions.MatchCategory(suggestion.Category) ?? exclusions.MatchPath(suggestion.File.FullPath);
            if (match is null)
            {
                kept.Add(suggestion);
            }
            else
            {
                excluded.Add(new ExcludedSuggestion(suggestion, match));
            }
        }

        var groups = kept
            .GroupBy(s => s.Category)
            .OrderBy(g => g.Key)
            .Select(g => new TriageGroup(
                g.Key,
                Title(g.Key),
                Explanation(g.Key),
                g.OrderByDescending(s => s.Confidence.Level).ThenBy(s => s.File.FullPath, StringComparer.Ordinal).ToArray()))
            .ToArray();
        return new TriageResult(groups, excluded, notRead);
    }

    public static string Title(TriageCategory category) => category switch
    {
        TriageCategory.Installers => "Installers",
        TriageCategory.ExtractedArchives => "Archives that are already extracted",
        TriageCategory.DiskImages => "Disk images",
        TriageCategory.Screenshots => "Screenshots",
        TriageCategory.FinancialDocuments => "Possible financial or tax documents",
        TriageCategory.SignedDocuments => "Possible signed documents",
        TriageCategory.SupersededFiles => "Likely older versions",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    public static string Explanation(TriageCategory category) => category switch
    {
        TriageCategory.Installers => "Setup files often stay behind after an app is installed. Keep any you need to reinstall without a download.",
        TriageCategory.ExtractedArchives => "A folder with the same name sits next to each archive. Where the archive is a zip, its files were compared with the folder.",
        TriageCategory.DiskImages => "Disk images are often large and used only once, for example to install an operating system.",
        TriageCategory.Screenshots => "Screenshots matched by their name or the folder they are in.",
        TriageCategory.FinancialDocuments => "Matched by name and document properties, which can be wrong. These may be personal documents, so none is selected for you.",
        TriageCategory.SignedDocuments => "Matched by name, or a PDF that carries a digital signature. These may be personal documents, so none is selected for you.",
        TriageCategory.SupersededFiles => "A newer file with the same name, apart from version or copy markers, is in the same folder. The contents were not compared.",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    private TriageSuggestion? Classify(FolderContext context, FileEntry file, List<InaccessibleEvidence> notRead)
    {
        var extension = Path.GetExtension(file.Name);
        var words = Words(Path.GetFileNameWithoutExtension(file.Name));
        DocumentProperties properties;
        try
        {
            properties = DocumentPropertyReader.Reads(file.FullPath) ? DocumentPropertyReader.Read(file.FullPath) : DocumentProperties.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notRead.Add(new InaccessibleEvidence(
                file.FullPath,
                ex is UnauthorizedAccessException ? InaccessibleReason.AccessDenied : InaccessibleReason.IoError,
                $"Its document properties could not be read: {ex.Message}"));
            properties = DocumentProperties.None;
        }

        return Financial(context, file, extension, words, properties)
            ?? Signed(context, file, extension, words, properties)
            ?? DiskImage(context, file, extension)
            ?? Installer(context, file, extension, words)
            ?? ExtractedArchive(context, file, extension, notRead)
            ?? Screenshot(context, file, extension);
    }

    private static TriageSuggestion? Financial(FolderContext context, FileEntry file, string extension, string[] words, DocumentProperties properties)
    {
        if (FinancialFormats.Contains(extension))
        {
            return Suggest(context, file, TriageCategory.FinancialDocuments, ConfidenceLevel.High,
                $"'{extension}' is a bank or bookkeeping data format.",
                new CheckEvidence("file format", $"{file.Name} is a {extension} file, a format for bank or bookkeeping data."));
        }

        if (!DocumentExtensions.Contains(extension) && !ImageExtensions.Contains(extension))
        {
            return null;
        }

        var evidence = new List<Evidence>();
        var nameWord = Matching(words, FinancialWords);
        if (nameWord is not null)
        {
            evidence.Add(new CheckEvidence("name", $"The name contains \"{nameWord}\"."));
        }

        var property = properties.Text.Select(t => (Text: t, Word: Matching(Words(t), FinancialWords))).FirstOrDefault(p => p.Word is not null);
        if (property.Word is not null)
        {
            evidence.Add(new CheckEvidence("document properties", $"Its properties contain \"{property.Word}\": \"{Shorten(property.Text)}\"."));
        }

        return evidence.Count switch
        {
            0 => null,
            1 => Suggest(context, file, TriageCategory.FinancialDocuments, ConfidenceLevel.Medium,
                "One sign points to a financial or tax document; a word match can be wrong.", [.. evidence]),
            _ => Suggest(context, file, TriageCategory.FinancialDocuments, ConfidenceLevel.High,
                "Both the name and the document properties point to a financial or tax document.", [.. evidence]),
        };
    }

    private static TriageSuggestion? Signed(FolderContext context, FileEntry file, string extension, string[] words, DocumentProperties properties)
    {
        if (properties.HasSignature)
        {
            return Suggest(context, file, TriageCategory.SignedDocuments, ConfidenceLevel.High,
                "The PDF carries a digital signature. Whether the signature is valid was not checked.",
                new CheckEvidence("signature", "The PDF contains a signature dictionary. Its validity was not checked."));
        }

        if (!DocumentExtensions.Contains(extension) && !ImageExtensions.Contains(extension))
        {
            return null;
        }

        var word = Matching(words, SignedWords);
        return word is null
            ? null
            : Suggest(context, file, TriageCategory.SignedDocuments, ConfidenceLevel.Medium,
                "The name suggests a signed document; a word match can be wrong.",
                new CheckEvidence("name", $"The name contains \"{word}\"."));
    }

    private static TriageSuggestion? DiskImage(FolderContext context, FileEntry file, string extension)
    {
        if (DiskImageExtensions.Contains(extension))
        {
            return Suggest(context, file, TriageCategory.DiskImages, ConfidenceLevel.High,
                $"'{extension}' is a disk image format.",
                new CheckEvidence("file format", $"{file.Name} is a {extension} disk image."));
        }

        // .img is also used for other things, such as some photo and firmware files.
        return extension.Equals(".img", StringComparison.OrdinalIgnoreCase)
            ? Suggest(context, file, TriageCategory.DiskImages, ConfidenceLevel.Medium,
                "'.img' is usually a disk image, but other programs use it too.",
                new CheckEvidence("file format", $"{file.Name} is an .img file, usually a disk image."))
            : null;
    }

    private static TriageSuggestion? Installer(FolderContext context, FileEntry file, string extension, string[] words)
    {
        if (PackageInstallers.Contains(extension))
        {
            return Suggest(context, file, TriageCategory.Installers, ConfidenceLevel.High,
                $"'{extension}' is a Windows installer package.",
                new CheckEvidence("file format", $"{file.Name} is a {extension} installer package."));
        }

        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var word = Matching(words, InstallerWords);
        if (word is not null)
        {
            return Suggest(context, file, TriageCategory.Installers, ConfidenceLevel.Medium,
                "A program whose name says it installs something.",
                new CheckEvidence("name", $"The name contains \"{word}\"."));
        }

        return context.Folder.Kind == KnownFolder.Downloads
            ? Suggest(context, file, TriageCategory.Installers, ConfidenceLevel.Low,
                "A program in Downloads. Programs downloaded there are often installers, but this one may be a tool you run directly.",
                new CheckEvidence("location", "A program in the Downloads folder."))
            : null;
    }

    private static TriageSuggestion? ExtractedArchive(FolderContext context, FileEntry file, string extension, List<InaccessibleEvidence> notRead)
    {
        if (!ArchiveExtensions.Contains(extension))
        {
            return null;
        }

        var folderPath = Path.Combine(Path.GetDirectoryName(file.FullPath)!, ArchiveStem(file.Name));
        var extracted = context.FilesBelow(folderPath);
        if (extracted.Count == 0)
        {
            return null;
        }

        var folderEvidence = new PathEvidence(folderPath, $"A folder with the same name holds {extracted.Count} files.");
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Suggest(context, file, TriageCategory.ExtractedArchives, ConfidenceLevel.Medium,
                "A folder with the same name is next to it. Its contents were not compared, because only zip files are read.",
                folderEvidence);
        }

        IReadOnlyList<(string Name, long Length)> listing;
        try
        {
            listing = ZipListing(file.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (ex is not InvalidDataException)
            {
                notRead.Add(new InaccessibleEvidence(
                    file.FullPath,
                    ex is UnauthorizedAccessException ? InaccessibleReason.AccessDenied : InaccessibleReason.IoError,
                    $"The zip file could not be read: {ex.Message}"));
            }

            return Suggest(context, file, TriageCategory.ExtractedArchives, ConfidenceLevel.Low,
                "A folder with the same name is next to it, but the zip file could not be read to compare them.",
                folderEvidence);
        }

        if (listing.Count > MaxZipEntries)
        {
            return Suggest(context, file, TriageCategory.ExtractedArchives, ConfidenceLevel.Medium,
                $"A folder with the same name is next to it. The zip holds more than {MaxZipEntries} files, so they were not compared.",
                folderEvidence);
        }

        var found = Math.Max(Matched(listing, extracted, stripFirst: false), Matched(listing, extracted, stripFirst: true));
        var comparison = new CheckEvidence("zip contents", $"{found} of the {listing.Count} files in the zip are in the folder with the same size.");
        if (listing.Count > 0 && found == listing.Count)
        {
            return Suggest(context, file, TriageCategory.ExtractedArchives, ConfidenceLevel.High,
                "Every file in the zip is in the folder next to it, with the same size. Contents were not compared byte for byte.",
                folderEvidence, comparison);
        }

        return Suggest(context, file, TriageCategory.ExtractedArchives, found > 0 ? ConfidenceLevel.Medium : ConfidenceLevel.Low,
            found > 0
                ? "Some, not all, of the zip's files are in the folder next to it. Files may have been changed or removed since."
                : "A folder with the same name is next to it, but none of the zip's files were found in it.",
            folderEvidence, comparison);
    }

    private static TriageSuggestion? Screenshot(FolderContext context, FileEntry file, string extension)
    {
        if (!ImageExtensions.Contains(extension))
        {
            return null;
        }

        var name = file.Name.ToLowerInvariant();
        var prefix = ScreenshotPrefixes.FirstOrDefault(p => name.StartsWith(p, StringComparison.Ordinal));
        if (prefix is not null)
        {
            return Suggest(context, file, TriageCategory.Screenshots, ConfidenceLevel.High,
                "The name is the one Windows or a screenshot tool gives a screenshot.",
                new CheckEvidence("name", $"The name starts with \"{prefix}\"."));
        }

        var parent = Path.GetFileName(Path.GetDirectoryName(file.FullPath));
        return string.Equals(parent, "Screenshots", StringComparison.OrdinalIgnoreCase)
            ? Suggest(context, file, TriageCategory.Screenshots, ConfidenceLevel.Medium,
                "An image in a folder named Screenshots.",
                new CheckEvidence("location", "The image is in a folder named Screenshots."))
            : null;
    }

    private static IEnumerable<TriageSuggestion> Superseded(KnownFolder folder, IEnumerable<FileEntry> files)
    {
        foreach (var group in VersionCandidates.Find(files).Where(g => g.SameFolder))
        {
            var newest = group.Files[0];
            foreach (var older in group.Files.Skip(1).Where(f => f.LastWriteTimeUtc < newest.LastWriteTimeUtc))
            {
                yield return new TriageSuggestion(
                    older,
                    folder,
                    TriageCategory.SupersededFiles,
                    new Confidence(ConfidenceLevel.Medium,
                        "A newer file with the same name, apart from version or copy markers, is in the same folder. The contents were not compared."),
                    [
                        Located(older),
                        new PathEvidence(newest.FullPath, $"Newer version: {newest.Name} · modified {newest.LastWriteTimeUtc:u}"),
                    ]);
            }
        }
    }

    private static TriageSuggestion Suggest(FolderContext context, FileEntry file, TriageCategory category, ConfidenceLevel level, string rationale, params Evidence[] evidence) =>
        new(file, context.Folder.Kind, category, new Confidence(level, rationale), [Located(file), .. evidence]);

    private static PathEvidence Located(FileEntry file) =>
        new(file.FullPath, $"{file.Name} · {file.Size.Bytes} bytes · modified {file.LastWriteTimeUtc:u}");

    private static IReadOnlyList<(string Name, long Length)> ZipListing(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return zip.Entries
            .Take(MaxZipEntries + 1)
            .Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\'))
            .Select(e => (e.FullName.Replace('\\', '/'), e.Length))
            .ToArray();
    }

    /// <summary>Zip files found at the same relative path and size; <paramref name="stripFirst"/> drops a top folder the zip itself has.</summary>
    private static int Matched(IReadOnlyList<(string Name, long Length)> listing, IReadOnlyDictionary<string, long> extracted, bool stripFirst)
    {
        var found = 0;
        foreach (var (name, length) in listing)
        {
            var relative = name;
            if (stripFirst)
            {
                var slash = name.IndexOf('/');
                if (slash < 0)
                {
                    continue;
                }

                relative = name[(slash + 1)..];
            }

            if (extracted.TryGetValue(relative, out var size) && size == length)
            {
                found++;
            }
        }

        return found;
    }

    private static string ArchiveStem(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ? stem[..^4] : stem;
    }

    /// <summary>The first keyword found among <paramref name="words"/>: a whole word, or the start of one for keywords of six letters or more.</summary>
    private static string? Matching(string[] words, string[] keywords) =>
        keywords.FirstOrDefault(k => words.Any(w => w == k || (k.Length >= 6 && w.StartsWith(k, StringComparison.Ordinal))));

    /// <summary>Lower-case words, split at separators and between letters and digits ("Factuur2024" is "factuur", "2024").</summary>
    internal static string[] Words(string text) =>
        WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value.Replace("-", "", StringComparison.Ordinal)).ToArray();

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..77] + "...";

    private static HashSet<string> Set(params string[] extensions) => new(extensions, StringComparer.OrdinalIgnoreCase);

    // "W-2" and "W2" both become "w2"; other words are runs of letters or runs of digits.
    [GeneratedRegex(@"w-?2(?![a-z0-9])|\p{L}+|\d+")]
    private static partial Regex WordPattern();

    private sealed class FolderContext(ScannedFolder folder, IReadOnlyList<FileEntry> files)
    {
        private static readonly StringComparison PathComparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public ScannedFolder Folder { get; } = folder;

        /// <summary>Files below <paramref name="folderPath"/>, by their relative path with forward slashes, and their sizes.</summary>
        public IReadOnlyDictionary<string, long> FilesBelow(string folderPath)
        {
            var prefix = folderPath + Path.DirectorySeparatorChar;
            var map = new Dictionary<string, long>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var file in files.Where(f => f.FullPath.StartsWith(prefix, PathComparison)))
            {
                map[file.FullPath[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/')] = file.Size.Bytes;
            }

            return map;
        }
    }
}

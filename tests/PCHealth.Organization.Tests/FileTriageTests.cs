using System.IO.Compression;
using System.Text;
using PCHealth.Core;
using PCHealth.Inventory;
using PCHealth.Policy;
using PCHealth.Testing;

namespace PCHealth.Organization.Tests;

public sealed class FileTriageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly TempRoot root = new();

    public FileTriageTests()
    {
        // Downloads
        root.CreateFile("Downloads/tool-setup.exe", "MZ");
        root.CreateFile("Downloads/App.msi", "msi");
        root.CreateFile("Downloads/portable.exe", "MZ");
        Zip("Downloads/photos.zip", ("a.txt", "alpha"), ("b/c.txt", "charlie"));
        root.CreateFile("Downloads/photos/a.txt", "alpha");
        root.CreateFile("Downloads/photos/b/c.txt", "charlie");
        Zip("Downloads/nested.zip", ("nested/one.txt", "1"), ("nested/two.txt", "22"));
        root.CreateFile("Downloads/nested/one.txt", "1");
        root.CreateFile("Downloads/nested/two.txt", "22");
        Zip("Downloads/partial.zip", ("x.txt", "x"), ("y.txt", "y"));
        root.CreateFile("Downloads/partial/x.txt", "x");
        root.CreateFile("Downloads/data.7z", "7z");
        root.CreateFile("Downloads/data/inside.txt", "in");
        Zip("Downloads/lonely.zip", ("z.txt", "z"));
        root.CreateFile("Downloads/ubuntu.iso", "iso");
        root.CreateFile("Downloads/Factuur2024-03.pdf", "%PDF-1.7\n");
        root.CreateFile("Downloads/bank.ofx", "<OFX>");
        root.CreateFile("Downloads/contract-signed.pdf", "%PDF-1.7\n");
        root.CreateFile("Downloads/agreement.pdf", "%PDF-1.7\n1 0 obj << /Type /Sig /Filter /Adobe.PPKLite /ByteRange [0 10 20 30] >> endobj\n");
        root.CreateFile("Downloads/scan.pdf", "%PDF-1.7\n2 0 obj << /Title (Tax return 2025) /Producer (Scanner) >> endobj\n");
        Docx("Documents/Belastingaangifte 2025.docx", title: "Aangifte", keywords: "belasting inkomsten");
        Docx("Documents/Holiday plan.docx", title: "Holiday", keywords: "beach");

        // Desktop
        root.CreateFile("Desktop/Screenshot 2026-01-01 101010.png", "png");
        root.CreateFile("Desktop/Screenshots/img001.png", "png");
        Aged(root.CreateFile("Desktop/Report v1.docx", "one"), minutesAgo: 60);
        Aged(root.CreateFile("Desktop/Report v2.docx", "two"), minutesAgo: 5);
        root.CreateFile("Desktop/notes.txt", "notes");
        // A leading dot is how Linux marks a file hidden; the attribute is how Windows does.
        var hidden = root.CreateFile("Desktop/.setup.msi", "hidden");
        File.SetAttributes(hidden, FileAttributes.Hidden);
    }

    public void Dispose() => root.Dispose();

    [Fact]
    public void Every_category_is_found_with_its_confidence_and_evidence()
    {
        var result = Triage(new ExclusionList());

        Assert.Equal(ConfidenceLevel.High, Find(result, "App.msi").Confidence.Level);
        Assert.Equal(ConfidenceLevel.Medium, Find(result, "tool-setup.exe").Confidence.Level);
        Assert.Equal(ConfidenceLevel.Low, Find(result, "portable.exe").Confidence.Level);
        Assert.All(new[] { "App.msi", "tool-setup.exe", "portable.exe" }, n => Assert.Equal(TriageCategory.Installers, Find(result, n).Category));

        Assert.Equal(ConfidenceLevel.High, Find(result, "photos.zip").Confidence.Level);
        Assert.Equal(ConfidenceLevel.High, Find(result, "nested.zip").Confidence.Level);
        Assert.Equal(ConfidenceLevel.Medium, Find(result, "partial.zip").Confidence.Level);
        Assert.Contains(Find(result, "partial.zip").Evidence, e => e.Description == "1 of the 2 files in the zip are in the folder with the same size.");
        Assert.Equal(ConfidenceLevel.Medium, Find(result, "data.7z").Confidence.Level);
        Assert.Null(Maybe(result, "lonely.zip"));

        Assert.Equal(TriageCategory.DiskImages, Find(result, "ubuntu.iso").Category);

        Assert.Equal((TriageCategory.FinancialDocuments, ConfidenceLevel.Medium), Of(Find(result, "Factuur2024-03.pdf")));
        Assert.Equal((TriageCategory.FinancialDocuments, ConfidenceLevel.High), Of(Find(result, "bank.ofx")));
        Assert.Equal((TriageCategory.FinancialDocuments, ConfidenceLevel.Medium), Of(Find(result, "scan.pdf")));
        Assert.Contains(Find(result, "scan.pdf").Evidence, e => e.Description.Contains("Tax return 2025", StringComparison.Ordinal));
        Assert.Equal((TriageCategory.FinancialDocuments, ConfidenceLevel.High), Of(Find(result, "Belastingaangifte 2025.docx")));
        Assert.Null(Maybe(result, "Holiday plan.docx"));

        Assert.Equal((TriageCategory.SignedDocuments, ConfidenceLevel.Medium), Of(Find(result, "contract-signed.pdf")));
        Assert.Equal((TriageCategory.SignedDocuments, ConfidenceLevel.High), Of(Find(result, "agreement.pdf")));

        Assert.Equal((TriageCategory.Screenshots, ConfidenceLevel.High), Of(Find(result, "Screenshot 2026-01-01 101010.png")));
        Assert.Equal((TriageCategory.Screenshots, ConfidenceLevel.Medium), Of(Find(result, "img001.png")));

        var older = Find(result, "Report v1.docx");
        Assert.Equal(TriageCategory.SupersededFiles, older.Category);
        Assert.Contains(older.Evidence, e => e is PathEvidence p && p.Path == root.Resolve("Desktop/Report v2.docx"));
        Assert.Null(Maybe(result, "Report v2.docx"));

        Assert.Null(Maybe(result, "notes.txt"));
        Assert.Null(Maybe(result, ".setup.msi"));
        Assert.All(result.Groups.SelectMany(g => g.Items), s => Assert.Equal(s.File.FullPath, Assert.IsType<PathEvidence>(s.Evidence[0]).Path));
    }

    [Fact]
    public void Sensitive_categories_are_marked_and_never_preselected()
    {
        var result = Triage(new ExclusionList());

        var sensitive = result.Groups.Where(g => g.Sensitive).Select(g => g.Category).ToArray();
        Assert.Equal([TriageCategory.FinancialDocuments, TriageCategory.SignedDocuments], sensitive);
        Assert.All(result.Groups.Where(g => g.Sensitive).SelectMany(g => g.Items), s => Assert.False(s.Preselected));
        Assert.True(Find(result, "bank.ofx").Confidence.Level == ConfidenceLevel.High && !Find(result, "bank.ofx").Preselected);

        Assert.True(Find(result, "App.msi").Preselected);
        Assert.False(Find(result, "portable.exe").Preselected);
        Assert.False(Find(result, "Report v1.docx").Preselected);
    }

    [Fact]
    public void A_file_in_a_sensitive_category_is_never_filed_under_another()
    {
        root.CreateFile("Downloads/Invoice screenshot.png", "png");

        var result = Triage(new ExclusionList());

        Assert.Equal(TriageCategory.FinancialDocuments, Find(result, "Invoice screenshot.png").Category);
    }

    [Fact]
    public void File_folder_and_category_exclusions_withhold_suggestions()
    {
        var exclusions = new ExclusionList();
        exclusions.ExcludeCategory(TriageCategory.Screenshots, Now);
        exclusions.ExcludeFile(root.Resolve("Downloads/App.msi"), Now);
        exclusions.ExcludeFolder(root.Resolve("Documents"), Now);

        var result = Triage(exclusions);

        Assert.DoesNotContain(result.Groups, g => g.Category == TriageCategory.Screenshots);
        Assert.Null(Maybe(result, "App.msi"));
        Assert.Null(Maybe(result, "Belastingaangifte 2025.docx"));
        Assert.Equal(
            ["App.msi", "Belastingaangifte 2025.docx", "Screenshot 2026-01-01 101010.png", "img001.png"],
            result.Excluded.Select(e => e.Suggestion.File.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ExclusionKind.TriageCategory, result.Excluded.Single(e => e.Suggestion.File.Name == "img001.png").Exclusion.Kind);
    }

    [Fact]
    public void A_category_exclusion_does_not_let_the_file_fall_into_another_category()
    {
        root.CreateFile("Downloads/Invoice screenshot.png", "png");
        var exclusions = new ExclusionList();
        exclusions.ExcludeCategory(TriageCategory.FinancialDocuments, Now);

        var result = Triage(exclusions);

        Assert.Null(Maybe(result, "Invoice screenshot.png"));
        Assert.Contains(result.Excluded, e => e.Suggestion.File.Name == "Invoice screenshot.png");
    }

    [Fact]
    public void Triage_changes_nothing_on_disk()
    {
        var before = Snapshot();

        Triage(new ExclusionList());

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void A_damaged_zip_is_still_suggested_with_low_confidence()
    {
        root.CreateFile("Downloads/broken.zip", "not a zip");
        root.CreateFile("Downloads/broken/file.txt", "x");

        var result = Triage(new ExclusionList());

        Assert.Equal((TriageCategory.ExtractedArchives, ConfidenceLevel.Low), Of(Find(result, "broken.zip")));
    }

    [Theory]
    [InlineData("Factuur2024-03", new[] { "factuur", "2024", "03" })]
    [InlineData("W-2 2025", new[] { "w2", "2025" })]
    [InlineData("tax_return", new[] { "tax", "return" })]
    public void Names_split_into_words(string name, string[] expected)
    {
        Assert.Equal(expected, FileTriage.Words(name));
    }

    [Theory]
    [InlineData("Syntax notes.pdf")]
    [InlineData("Taxi receipt.txt", true)]
    [InlineData("Vatican trip.pdf")]
    public void Keywords_match_whole_words_not_parts_of_other_words(string name, bool financial = false)
    {
        root.CreateFile("Documents/" + name, "x");

        var result = Triage(new ExclusionList());

        Assert.Equal(financial, Maybe(result, name)?.Category == TriageCategory.FinancialDocuments);
    }

    private TriageResult Triage(ExclusionList exclusions)
    {
        var scanner = new RecursiveScanner();
        ScannedFolder Scan(KnownFolder kind, string name)
        {
            var path = root.CreateDirectory(name);
            return new ScannedFolder(kind, path, scanner.Scan(path));
        }

        return new FileTriage(exclusions).Suggest(
        [
            Scan(KnownFolder.Desktop, "Desktop"),
            Scan(KnownFolder.Downloads, "Downloads"),
            Scan(KnownFolder.Documents, "Documents"),
        ]);
    }

    private static TriageSuggestion Find(TriageResult result, string name) =>
        Maybe(result, name) ?? throw new Xunit.Sdk.XunitException($"No suggestion for {name}.");

    private static TriageSuggestion? Maybe(TriageResult result, string name) =>
        result.Groups.SelectMany(g => g.Items).SingleOrDefault(s => s.File.Name == name);

    private static (TriageCategory, ConfidenceLevel) Of(TriageSuggestion suggestion) => (suggestion.Category, suggestion.Confidence.Level);

    private string[] Snapshot() =>
        Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories)
            .Select(f => $"{f}|{new FileInfo(f).Length}|{File.GetLastWriteTimeUtc(f):O}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private void Zip(string relativePath, params (string Name, string Contents)[] entries)
    {
        var path = root.Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, contents) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(contents);
        }
    }

    private void Docx(string relativePath, string title, string keywords)
    {
        var path = root.Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("docProps/core.xml").Open(), new UTF8Encoding(false));
        writer.Write($"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <dc:title>{title}</dc:title>
              <cp:keywords>{keywords}</cp:keywords>
            </cp:coreProperties>
            """);
    }

    private static void Aged(string path, int minutesAgo) =>
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
}

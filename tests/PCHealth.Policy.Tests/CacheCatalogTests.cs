using PCHealth.Core;
using PCHealth.Inventory;
using PCHealth.Policy.Catalog;
using PCHealth.Policy.Recommendations;
using PCHealth.Testing;

namespace PCHealth.Policy.Tests;

/// <summary>Cache rules evaluated against fixture trees standing in for the user's folders.</summary>
public class CacheCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_shipped_catalog_loads_and_every_rule_says_what_is_rebuilt()
    {
        var catalog = CacheCatalog.LoadDefault();

        Assert.NotEmpty(catalog.Rules);
        Assert.All(catalog.Rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Rebuild));
            Assert.False(string.IsNullOrWhiteSpace(r.Why));
            Assert.StartsWith("{", r.Location);
        });
        Assert.Contains(catalog.Rules, r => r.Id == "user-temp");
        Assert.Contains(catalog.Rules, r => r.Id == "crash-dumps");
    }

    [Fact]
    public void Crash_dumps_produce_a_finding_with_evidence_impact_and_recovery()
    {
        using var fixture = new Fixture();
        fixture.File("LocalAppData/CrashDumps/app.exe.1234.dmp", 300, ageDays: 1);
        fixture.File("LocalAppData/CrashDumps/app.exe.5678.dmp", 200, ageDays: 1);
        fixture.File("LocalAppData/CrashDumps/readme.txt", 50, ageDays: 1);

        var finding = Assert.Single(fixture.Evaluate().Findings, f => f.Title == "Application crash dumps");

        Assert.Equal(FindingCategory.Cache, finding.Category);
        var folder = fixture.Root.Resolve("LocalAppData/CrashDumps");
        Assert.Contains(finding.Evidence, e => e is PathEvidence p && p.Path == folder);
        Assert.Equal(500, finding.Evidence.OfType<MeasurementEvidence>().Single().Size.Bytes);
        Assert.Equal(500, finding.Impact.PotentiallyReclaimable!.Value.Bytes);
        Assert.Contains("2 files", finding.Impact.WhatChanges);
        Assert.Equal(CacheCatalog.LoadDefault().Rules.Single(r => r.Id == "crash-dumps").Rebuild, finding.Impact.Aftereffects);
        Assert.Equal(ConfidenceLevel.High, finding.Confidence.Level);
        Assert.Equal(RecoveryMethod.RecycleBin, finding.Recovery.Method);
    }

    [Fact]
    public void Minimum_age_keeps_recent_temporary_files_out()
    {
        using var fixture = new Fixture();
        fixture.File("Temp/old.tmp", 100, ageDays: 30);
        fixture.File("Temp/sub/older.log", 10, ageDays: 8);
        fixture.File("Temp/fresh.tmp", 1000, ageDays: 1);

        var finding = Assert.Single(fixture.Evaluate().Findings, f => f.Title == "Temporary files");

        Assert.Equal(110, finding.Evidence.OfType<MeasurementEvidence>().Single().Size.Bytes);
        Assert.Contains("unchanged for 7 days or more", finding.Impact.WhatChanges);
    }

    [Fact]
    public void A_wildcard_segment_matches_each_browser_profile()
    {
        using var fixture = new Fixture();
        fixture.File("LocalAppData/Google/Chrome/User Data/Default/Cache/Cache_Data/f_000001", 40, ageDays: 2);
        fixture.File("LocalAppData/Google/Chrome/User Data/Profile 1/Cache/Cache_Data/f_000002", 60, ageDays: 2);
        fixture.File("LocalAppData/Google/Chrome/User Data/Default/History", 999, ageDays: 2);

        var chrome = fixture.Evaluate().Findings.Where(f => f.Title == "Google Chrome cache").ToArray();

        Assert.Equal(2, chrome.Length);
        Assert.Equal(new long[] { 40, 60 }, chrome.Select(f => f.Evidence.OfType<MeasurementEvidence>().Single().Size.Bytes).Order());
    }

    [Fact]
    public void A_browser_cache_says_to_close_the_browser_first()
    {
        using var fixture = new Fixture();
        fixture.File("LocalAppData/Google/Chrome/User Data/Default/Cache/Cache_Data/f_000001", 40, ageDays: 2);
        fixture.File("LocalAppData/CrashDumps/app.dmp", 10, ageDays: 2);

        var findings = fixture.Evaluate().Findings;
        var chrome = findings.Single(f => f.Title == "Google Chrome cache");
        var recommendations = new RecommendationBuilder(new ProtectionPolicy([]), new ExclusionList()).Build(findings).Recommendations;

        Assert.Equal(new AppToClose("chrome", "Google Chrome"), Assert.Single(chrome.CloseFirst));
        Assert.Empty(findings.Single(f => f.Title == "Application crash dumps").CloseFirst);
        var recommendation = recommendations.Single(r => r.Finding == chrome);
        Assert.Equal("Close Google Chrome first.", recommendation.CloseFirst);
        Assert.StartsWith("Close Google Chrome first. Moves 1 files", recommendation.WhatWillChange);
        Assert.Null(recommendations.Single(r => r.Finding.Title == "Application crash dumps").CloseFirst);
        Assert.Equal("msedge", Assert.Single(CacheCatalog.LoadDefault().Rules.Single(r => r.Id == "edge-cache").CloseFirst!).Process);
    }

    [Fact]
    public void Only_the_current_catalog_format_is_read()
    {
        const string Rule = """{ "id": "x", "name": "X cache", "location": "{LocalAppData}/X", "why": "Why.", "rebuild": "Rebuilt." }""";
        static CacheCatalog Load(string json) => CacheCatalog.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));

        Assert.Equal(2, CacheCatalog.SchemaVersion);
        Assert.Single(Load($$"""{ "version": 2, "rules": [{{Rule}}] }""").Rules);
        var old = Assert.Throws<InvalidDataException>(() => Load($$"""{ "version": 1, "rules": [{{Rule}}] }"""));
        Assert.Contains("format version 1", old.Message);
        Assert.Throws<InvalidDataException>(() => Load("""{ "version": 2, "rules": [{ "id": "x", "name": "X", "location": "{LocalAppData}/X", "why": "W", "rebuild": "R", "closeFirst": [{ "process": "C:\\x\\x.exe", "name": "X" }] }] }"""));
        Assert.Throws<InvalidDataException>(() => Load("not json"));
    }

    [Fact]
    public void File_patterns_limit_what_counts()
    {
        using var fixture = new Fixture();
        fixture.File("LocalAppData/Microsoft/Windows/Explorer/thumbcache_256.db", 80, ageDays: 3);
        fixture.File("LocalAppData/Microsoft/Windows/Explorer/iconcache_16.db", 500, ageDays: 3);

        var finding = Assert.Single(fixture.Evaluate().Findings, f => f.Title == "Explorer thumbnail cache");

        Assert.Equal(80, finding.Evidence.OfType<MeasurementEvidence>().Single().Size.Bytes);
    }

    [Fact]
    public void Empty_or_missing_locations_produce_no_findings()
    {
        using var fixture = new Fixture();
        fixture.Root.CreateDirectory("LocalAppData/D3DSCache");

        var result = fixture.Evaluate();

        Assert.Empty(result.Findings);
        Assert.Contains(result.Skipped, s => s.Rule.Id == "directx-shader-cache" && s.Reason.Contains("Nothing matching"));
    }

    [Fact]
    public void Unknown_tokens_are_skipped_with_a_reason()
    {
        using var fixture = new Fixture(includeTemp: false);
        fixture.File("LocalAppData/CrashDumps/a.dmp", 1, ageDays: 1);

        var result = fixture.Evaluate();

        Assert.Contains(result.Skipped, s => s.Rule.Id == "user-temp" && s.Reason.Contains("Temp"));
    }

    [Fact]
    public void Protected_locations_are_skipped_with_the_protection_reason()
    {
        using var fixture = new Fixture();
        fixture.File("LocalAppData/CrashDumps/a.dmp", 10, ageDays: 1);
        var protection = new ProtectionPolicy([new(fixture.Root.Resolve("LocalAppData/CrashDumps"), ProtectionKind.PersonalFolder, "test protection.")]);

        var result = fixture.Evaluate(protection);

        Assert.DoesNotContain(result.Findings, f => f.Title == "Application crash dumps");
        Assert.Contains(result.Skipped, s => s.Rule.Id == "crash-dumps" && s.Reason.Contains("test protection."));
    }

    [Fact]
    public void Program_binaries_inside_a_cache_are_never_counted()
    {
        using var fixture = new Fixture();
        fixture.File("Temp/setup.exe", 5000, ageDays: 30);
        fixture.File("Temp/old.tmp", 5, ageDays: 30);

        var finding = Assert.Single(fixture.Evaluate().Findings, f => f.Title == "Temporary files");

        Assert.Equal(5, finding.Evidence.OfType<MeasurementEvidence>().Single().Size.Bytes);
    }

    [Fact]
    public void User_exclusions_are_respected()
    {
        using var fixture = new Fixture();
        fixture.File("LocalAppData/CrashDumps/a.dmp", 10, ageDays: 1);
        fixture.File("Temp/keep/old.tmp", 10, ageDays: 30);
        fixture.File("Temp/other.tmp", 7, ageDays: 30);
        var exclusions = new ExclusionList();
        exclusions.ExcludeFolder(fixture.Root.Resolve("LocalAppData/CrashDumps"), Now);
        exclusions.ExcludeFolder(fixture.Root.Resolve("Temp/keep"), Now);

        var result = fixture.Evaluate(exclusions: exclusions);

        Assert.Contains(result.Skipped, s => s.Rule.Id == "crash-dumps" && s.Reason.Contains("You excluded"));
        Assert.Equal(7, Assert.Single(result.Findings).Evidence.OfType<MeasurementEvidence>().Single().Size.Bytes);
    }

    [Fact]
    public void Excluding_the_cache_category_skips_every_rule()
    {
        using var fixture = new Fixture();
        fixture.File("LocalAppData/CrashDumps/a.dmp", 10, ageDays: 1);
        var exclusions = new ExclusionList();
        exclusions.ExcludeCategory(FindingCategory.Cache, Now);

        var result = fixture.Evaluate(exclusions: exclusions);

        Assert.Empty(result.Findings);
        Assert.Equal(CacheCatalog.LoadDefault().Rules.Count, result.Skipped.Count);
    }

    [Theory]
    [InlineData("{LocalAppData}/x", "")]
    [InlineData("LocalAppData/x", "rebuilt")]
    [InlineData("{LocalAppData}/../x", "rebuilt")]
    [InlineData("{LocalAppData}/*/a/*", "rebuilt")]
    [InlineData("{LocalAppData}/ca*che", "rebuilt")]
    public void Invalid_rules_are_rejected(string location, string rebuild)
    {
        Assert.Throws<ArgumentException>(() => new CacheCatalog([new CacheRule("id", "Name", location, "why", rebuild)]));
    }

    private sealed class Fixture : IKnownLocations, IDisposable
    {
        private readonly bool includeTemp;

        public Fixture(bool includeTemp = true)
        {
            this.includeTemp = includeTemp;
            Root.CreateDirectory("LocalAppData");
            Root.CreateDirectory("Temp");
        }

        public TempRoot Root { get; } = new();

        public string? Resolve(string token) => token switch
        {
            "Temp" when includeTemp => Root.Resolve("Temp"),
            "LocalAppData" => Root.Resolve("LocalAppData"),
            _ => null,
        };

        public void File(string relative, int bytes, int ageDays)
        {
            var path = Root.CreateFile(relative, new string('x', bytes));
            System.IO.File.SetLastWriteTimeUtc(path, Now.AddDays(-ageDays).UtcDateTime);
        }

        public CatalogResult Evaluate(ProtectionPolicy? protection = null, ExclusionList? exclusions = null) =>
            CacheCatalog.LoadDefault().Evaluate(
                this,
                new RecursiveScanner(),
                protection ?? new ProtectionPolicy([]),
                exclusions ?? new ExclusionList(),
                Now,
                TestContext.Current.CancellationToken);

        public void Dispose() => Root.Dispose();
    }
}

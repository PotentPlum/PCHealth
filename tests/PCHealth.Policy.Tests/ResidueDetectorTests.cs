using PCHealth.Core;
using PCHealth.Inventory;
using PCHealth.Policy.Catalog;
using PCHealth.Policy.Residue;
using PCHealth.Testing;

namespace PCHealth.Policy.Tests;

/// <summary>Residue detection against fixture trees and fake registrations.</summary>
public class ResidueDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_folder_no_registration_claims_is_likely_orphaned()
    {
        using var fixture = new Fixture();
        fixture.File("RoamingAppData/OldEditor/settings.json", 300, ageDays: 200);
        fixture.File("RoamingAppData/OldEditor/cache/a.bin", 700, ageDays: 190);

        var finding = Assert.Single(fixture.Evaluate().Findings);

        Assert.Equal(FindingCategory.AppResidue, finding.Category);
        Assert.Equal("Possible leftover data: OldEditor", finding.Title);
        Assert.False(finding.ReviewOnly);
        Assert.Equal(ConfidenceLevel.Medium, finding.Confidence.Level);
        Assert.Equal(1000, finding.Evidence.OfType<MeasurementEvidence>().Single().Size.Bytes);
        Assert.Contains(finding.Evidence, e => e is CheckEvidence { Check: "registrations" } c && c.Description.Contains("2 uninstall entries and 1 packages"));
        Assert.Equal(1000, finding.Impact.PotentiallyReclaimable!.Value.Bytes);
        Assert.Equal(RecoveryMethod.Quarantine, finding.Recovery.Method);
    }

    [Theory]
    [InlineData("Contoso Notes")]
    [InlineData("Contoso")]
    [InlineData("ContosoNotes")]
    [InlineData("Northwind")]
    [InlineData("Tailspin")]
    public void Folders_claimed_by_a_registration_produce_no_finding(string folderName)
    {
        using var fixture = new Fixture();
        fixture.File($"RoamingAppData/{folderName}/state.dat", 10, ageDays: 300);

        var result = fixture.Evaluate();

        Assert.Empty(result.Findings);
        Assert.Contains(result.Skipped, s => s.Path.EndsWith(folderName, StringComparison.Ordinal) && s.Reason.StartsWith("Belongs to", StringComparison.Ordinal));
    }

    [Fact]
    public void A_registration_installed_inside_the_folder_claims_it()
    {
        using var fixture = new Fixture();
        fixture.File("LocalAppData/SelfUpdater/data.bin", 10, ageDays: 300);
        fixture.Registrations.Add(new(RegistrationSource.UninstallEntry, "x", "Something Else", InstallLocation: fixture.Root.Resolve("LocalAppData/SelfUpdater/app-1.2")));

        Assert.Empty(fixture.Evaluate().Findings);
    }

    [Fact]
    public void A_similar_name_is_contradictory_evidence_and_makes_the_finding_uncertain()
    {
        using var fixture = new Fixture();
        fixture.File("RoamingAppData/Fabrikam Sync/db.sqlite", 50, ageDays: 300);

        var finding = Assert.Single(fixture.Evaluate().Findings);

        Assert.True(finding.ReviewOnly);
        Assert.Equal(ConfidenceLevel.Low, finding.Confidence.Level);
        Assert.Contains("Fabrikam Photo Studio", finding.Confidence.Rationale);
        Assert.Contains(finding.Evidence, e => e is RegistrationEvidence { Name: "Fabrikam Photo Studio" });
        Assert.StartsWith("Unclear ownership", finding.Title);
        Assert.StartsWith("Nothing is selected for cleanup", finding.Impact.WhatChanges);
    }

    [Fact]
    public void Incomplete_registrations_make_every_finding_uncertain()
    {
        using var fixture = new Fixture();
        fixture.Gaps.Add("the package list could not be read.");
        fixture.File("RoamingAppData/OldEditor/settings.json", 300, ageDays: 200);

        var finding = Assert.Single(fixture.Evaluate().Findings);

        Assert.True(finding.ReviewOnly);
        Assert.Contains("the package list could not be read.", finding.Confidence.Rationale);
    }

    [Fact]
    public void Recent_activity_makes_the_finding_uncertain()
    {
        using var fixture = new Fixture();
        fixture.File("ProgramData/OldService/log.txt", 20, ageDays: 3);
        fixture.File("ProgramData/OldService/old.txt", 20, ageDays: 400);

        var finding = Assert.Single(fixture.Evaluate().Findings);

        Assert.True(finding.ReviewOnly);
        Assert.Contains("written 3 days ago", finding.Confidence.Rationale);
    }

    [Fact]
    public void Program_files_inside_make_the_finding_uncertain()
    {
        using var fixture = new Fixture();
        fixture.File("LocalAppData/PortableTool/tool.exe", 20, ageDays: 400);
        fixture.File("LocalAppData/PortableTool/tool.ini", 20, ageDays: 400);

        var finding = Assert.Single(fixture.Evaluate().Findings);

        Assert.True(finding.ReviewOnly);
        Assert.Contains(finding.Evidence, e => e is CheckEvidence { Check: "program files" });
    }

    [Fact]
    public void Unreadable_contents_make_the_finding_uncertain()
    {
        using var fixture = new Fixture(new IncompleteScanner());
        fixture.File("LocalAppData/OldEditor/a.txt", 20, ageDays: 400);

        var finding = Assert.Single(fixture.Evaluate().Findings);

        Assert.True(finding.ReviewOnly);
        Assert.Contains(finding.Evidence, e => e is InaccessibleEvidence);
    }

    [Fact]
    public void Package_data_folders_are_matched_to_package_families()
    {
        using var fixture = new Fixture();
        fixture.File("LocalAppData/Packages/Contoso.Notes_abc123/Settings/settings.dat", 10, ageDays: 300);
        fixture.File("LocalAppData/Packages/Gone.App_zzz999/LocalState/state.dat", 40, ageDays: 300);
        fixture.File("LocalAppData/Packages/Contoso.Notes_other99/LocalState/state.dat", 30, ageDays: 300);

        var findings = fixture.Evaluate().Findings;

        Assert.Equal(2, findings.Count);
        var gone = Assert.Single(findings, f => f.Title.EndsWith("Gone.App_zzz999", StringComparison.Ordinal));
        Assert.False(gone.ReviewOnly);
        var sameName = Assert.Single(findings, f => f.Title.EndsWith("Contoso.Notes_other99", StringComparison.Ordinal));
        Assert.True(sameName.ReviewOnly);
    }

    [Fact]
    public void Folders_windows_uses_are_never_candidates()
    {
        using var fixture = new Fixture();
        fixture.File("LocalAppData/Microsoft/Windows/x.dat", 10, ageDays: 300);
        fixture.File("LocalAppData/Temp/x.tmp", 10, ageDays: 300);
        fixture.File("ProgramData/Package Cache/x.bin", 10, ageDays: 300);

        var result = fixture.Evaluate();

        Assert.Empty(result.Findings);
        Assert.Equal(3, result.Skipped.Count(s => s.Reason == "Windows creates and uses this folder."));
    }

    [Fact]
    public void Empty_folders_and_links_produce_no_findings()
    {
        using var fixture = new Fixture();
        fixture.Root.CreateDirectory("RoamingAppData/Empty");
        var target = fixture.Root.CreateDirectory("elsewhere/OldEditor");
        fixture.File("elsewhere/OldEditor/a.txt", 10, ageDays: 300);
        Directory.CreateSymbolicLink(fixture.Root.Resolve("RoamingAppData/Linked"), target);

        var result = fixture.Evaluate();

        Assert.Empty(result.Findings);
        Assert.Contains(result.Skipped, s => s.Reason == "The folder holds no files.");
        Assert.DoesNotContain(result.Skipped, s => s.Path.EndsWith("Linked", StringComparison.Ordinal));
    }

    [Fact]
    public void Protected_and_excluded_folders_are_skipped_with_the_reason()
    {
        using var fixture = new Fixture();
        fixture.File("LocalAppData/Shielded/a.txt", 10, ageDays: 300);
        fixture.File("RoamingAppData/Kept/a.txt", 10, ageDays: 300);
        var protection = new ProtectionPolicy([new(fixture.Root.Resolve("LocalAppData/Shielded"), ProtectionKind.ProgramFiles, "test protection.")]);
        var exclusions = new ExclusionList();
        exclusions.ExcludeFolder(fixture.Root.Resolve("RoamingAppData/Kept"), Now);

        var result = fixture.Evaluate(protection, exclusions);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Skipped, s => s.Reason.Contains("test protection."));
        Assert.Contains(result.Skipped, s => s.Reason.StartsWith("You excluded", StringComparison.Ordinal));
    }

    [Fact]
    public void Excluding_the_category_skips_everything_without_reading_registrations()
    {
        using var fixture = new Fixture();
        fixture.File("RoamingAppData/OldEditor/a.txt", 10, ageDays: 300);
        var exclusions = new ExclusionList();
        exclusions.ExcludeCategory(FindingCategory.AppResidue, Now);

        var result = fixture.Evaluate(exclusions: exclusions);

        Assert.Empty(result.Findings);
        Assert.Equal(0, fixture.Reads);
        Assert.Equal(3, result.Skipped.Count);
    }

    [Fact]
    public void Missing_roots_are_reported()
    {
        using var fixture = new Fixture(includeProgramData: false);

        Assert.Contains(fixture.Evaluate().Skipped, s => s.Path == "{ProgramData}");
    }

    private sealed class IncompleteScanner : IInventoryScanner
    {
        private readonly RecursiveScanner inner = new();

        public ScanResult Scan(string rootPath, IProgress<ScanProgress>? progress = null, PauseToken pause = default, CancellationToken cancellationToken = default)
        {
            var result = inner.Scan(rootPath, progress, pause, cancellationToken);
            return result with { Inaccessible = [new InaccessibleEvidence(Path.Combine(rootPath, "locked"), InaccessibleReason.AccessDenied, "Access denied.")] };
        }
    }

    private sealed class Fixture : IKnownLocations, IAppRegistrations, IDisposable
    {
        private readonly bool includeProgramData;
        private readonly IInventoryScanner scanner;

        public Fixture(IInventoryScanner? scanner = null, bool includeProgramData = true)
        {
            this.scanner = scanner ?? new RecursiveScanner();
            this.includeProgramData = includeProgramData;
            Root.CreateDirectory("LocalAppData");
            Root.CreateDirectory("RoamingAppData");
            Root.CreateDirectory("ProgramData");
            Registrations =
            [
                new(RegistrationSource.UninstallEntry, "{1111}", "Contoso Notes 4.2 (x64)", Publisher: "Contoso Ltd."),
                new(RegistrationSource.UninstallEntry, "Fabrikam", "Fabrikam Photo Studio", Publisher: "Northwind Traders", InstallLocation: Root.Resolve("installs/Tailspin") + Path.DirectorySeparatorChar),
                new(RegistrationSource.Package, "Contoso.Notes_abc123", "Contoso Notes"),
            ];
        }

        public TempRoot Root { get; } = new();

        public List<AppRegistration> Registrations { get; }

        public List<string> Gaps { get; } = [];

        public int Reads { get; private set; }

        public string? Resolve(string token) => token switch
        {
            "LocalAppData" or "RoamingAppData" => Root.Resolve(token),
            "ProgramData" when includeProgramData => Root.Resolve(token),
            _ => null,
        };

        public RegistrationSnapshot Read()
        {
            Reads++;
            return new RegistrationSnapshot(Registrations.ToArray(), Gaps.ToArray());
        }

        public void File(string relative, int bytes, int ageDays)
        {
            var path = Root.CreateFile(relative, new string('x', bytes));
            System.IO.File.SetLastWriteTimeUtc(path, Now.AddDays(-ageDays).UtcDateTime);
        }

        public ResidueResult Evaluate(ProtectionPolicy? protection = null, ExclusionList? exclusions = null) =>
            ResidueDetector.Evaluate(
                this,
                this,
                scanner,
                protection ?? new ProtectionPolicy([]),
                exclusions ?? new ExclusionList(),
                Now,
                TestContext.Current.CancellationToken);

        public void Dispose() => Root.Dispose();
    }
}

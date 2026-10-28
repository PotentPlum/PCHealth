using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Core;
using PCHealth.Execution;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Testing;

namespace PCHealth.App.Tests;

/// <summary>The Organize page (T-52) on a fixture Desktop, Downloads and Documents, through the real policy and gateway.</summary>
public sealed class OrganizePageTests : IDisposable
{
    private readonly TempRoot root = new();

    public OrganizePageTests()
    {
        root.CreateFile("user/Downloads/App.msi", "msi");
        root.CreateFile("user/Downloads/Factuur2024-03.pdf", "%PDF-1.7\n");
        root.CreateFile("user/Desktop/Screenshot 2026-01-01 101010.png", "png");
        root.CreateFile("user/Documents/Code/Clean/package.json", """{ "name": "clean" }""");
        root.CreateFile("user/Documents/Code/Clean/Screenshot of the app.png", "png");
        root.CreateFile("user/Documents/Code/Tied/Tied.csproj", """<Project><ItemGroup><ProjectReference Include="..\..\..\Shared\Shared.csproj" /></ItemGroup></Project>""");
    }

    public void Dispose() => root.Dispose();

    [Fact]
    public async Task Suggestions_show_their_evidence_and_sensitive_ones_start_unticked()
    {
        using var services = TestServices.Build(root);
        var page = services.GetRequiredService<OrganizeViewModel>();

        await page.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal(["Installers (1)", "Screenshots (1)", "Possible financial or tax documents (1)"], page.Groups.Select(g => g.Title));
        Assert.StartsWith("Looked in Desktop, Downloads, Documents: 3 suggested files in 3 categories and 2 projects.", page.Summary);
        var installer = page.Groups[0].Items.Single();
        Assert.True(installer.IsSelected);
        Assert.StartsWith("Confidence: High.", installer.Confidence);
        Assert.Contains(installer.Evidence, e => e.Contains(".msi installer package", StringComparison.Ordinal));

        var financial = page.Groups[2];
        Assert.True(financial.Sensitive);
        Assert.NotNull(financial.SensitiveNote);
        Assert.False(financial.Items.Single().IsSelected);
        Assert.Contains(financial.Items.Single().Evidence, e => e == "The name contains \"factuur\".");

        // The screenshot inside a project stays with its project.
        Assert.DoesNotContain(page.Groups.SelectMany(g => g.Items), i => i.Path.Contains("Clean", StringComparison.Ordinal));
    }

    [Fact]
    public void A_project_with_paths_outside_it_cannot_be_selected_and_the_page_says_why()
    {
        using var services = TestServices.Build(root);
        var page = Open(services);

        var tied = page.Projects.Single(p => p.Name == "Tied");
        var clean = page.Projects.Single(p => p.Name == "Clean");
        Assert.False(tied.CanSelect);
        Assert.Equal("Can't be moved: files in it point outside the folder, so moving it could break it.", tied.Blocked);
        Assert.Contains(tied.Reasons, r => r.Contains(@"..\..\..\Shared\Shared.csproj", StringComparison.Ordinal));
        tied.IsSelected = true;
        Assert.False(tied.IsSelected);

        Assert.True(clean.CanSelect);
        Assert.StartsWith("Not changed since ", clean.Activity);
        Assert.Contains("Programs and settings elsewhere may still refer to this folder.", clean.Dependencies);
    }

    [Fact]
    public void A_sensitive_category_needs_a_second_confirmation()
    {
        using var services = TestServices.Build(root);
        var main = services.GetRequiredService<MainViewModel>();
        var page = Open(services);
        main.Current = page;
        page.Destination = root.CreateDirectory("user/Documents/Admin");
        var financial = page.Groups.Single(g => g.Sensitive);
        financial.Items.Single().IsSelected = true;

        page.MoveGroupCommand.Execute(financial);

        Assert.Equal($"These may be personal documents. Tick \"{financial.ConfirmText}\" first.", financial.Message);
        Assert.Same(page, main.Current);

        financial.Confirmed = true;
        page.MoveGroupCommand.Execute(financial);

        Assert.Null(financial.Message);
        Assert.IsType<PreviewViewModel>(main.Current);
    }

    [Fact]
    public void A_move_is_previewed_in_full_runs_through_the_gateway_and_can_be_undone_from_activity()
    {
        using var services = TestServices.Build(root);
        var main = services.GetRequiredService<MainViewModel>();
        var page = Open(services);
        main.Current = page;
        var installers = page.Groups[0];

        page.MoveGroupCommand.Execute(installers);
        Assert.Equal("Choose the folder to move them to first.", installers.Message);

        var archive = root.CreateDirectory("user/Documents/Installers");
        ((TestServices.FixturePicker)services.GetRequiredService<IFolderPicker>()).Next = archive;
        page.BrowseCommand.Execute(null);
        Assert.Equal($"Move to: {archive}", page.DestinationText);
        page.MoveGroupCommand.Execute(installers);

        var preview = Assert.IsType<PreviewViewModel>(main.Current);
        var item = Assert.Single(preview.Items);
        Assert.Equal(root.Resolve("user/Downloads/App.msi"), item.Path);
        Assert.Equal($"→ {Path.Combine(archive, "App.msi")}", item.MovesTo);
        Assert.StartsWith("1 of 1 items selected. A move within one drive frees no space.", preview.ExpectedEffect);
        preview.ApproveCommand.Execute(null);

        Assert.Equal(ExecutionStatus.Completed, preview.Execution!.Status);
        Assert.True(File.Exists(Path.Combine(archive, "App.msi")));
        preview.BackCommand.Execute(null);
        Assert.Same(page, main.Current);
        Assert.DoesNotContain(page.Groups, g => g.Group.Category == TriageCategory.Installers);

        var activity = services.GetRequiredService<ActivityViewModel>();
        activity.Refresh();
        var entry = Assert.Single(activity.Entries);
        Assert.Equal("Moved 1 item to another folder", entry.WhatChanged);
        entry.UndoCommand.Execute(null);

        Assert.True(File.Exists(root.Resolve("user/Downloads/App.msi")));
        Assert.False(File.Exists(Path.Combine(archive, "App.msi")));
        Assert.Contains(services.GetRequiredService<TransactionLedger>().All(), t => t.Operation == LedgerOperation.Restore);
    }

    [Fact]
    public void A_project_folder_moves_whole()
    {
        using var services = TestServices.Build(root);
        var main = services.GetRequiredService<MainViewModel>();
        var page = Open(services);
        main.Current = page;
        page.Destination = root.CreateDirectory("user/Documents/Archive");
        page.Projects.Single(p => p.Name == "Clean").IsSelected = true;

        page.MoveProjectsCommand.Execute(null);
        var preview = Assert.IsType<PreviewViewModel>(main.Current);
        preview.ApproveCommand.Execute(null);
        preview.BackCommand.Execute(null);

        Assert.Equal(ExecutionStatus.Completed, preview.Execution!.Status);
        Assert.True(File.Exists(root.Resolve("user/Documents/Archive/Clean/package.json")));
        Assert.False(Directory.Exists(root.Resolve("user/Documents/Code/Clean")));
        Assert.DoesNotContain(page.Projects, p => p.Name == "Clean");
    }

    [Fact]
    public void Excluded_items_and_the_whole_category_are_respected()
    {
        using var services = TestServices.Build(root);
        var exclusions = services.GetRequiredService<ExclusionList>();
        exclusions.ExcludeFile(root.Resolve("user/Downloads/App.msi"), DateTimeOffset.UtcNow);
        var page = Open(services);

        Assert.DoesNotContain(page.Groups, g => g.Group.Category == TriageCategory.Installers);
        Assert.Contains("1 suggestions are hidden by your exclusions.", page.Summary);

        exclusions.ExcludeCategory(FindingCategory.Organization, DateTimeOffset.UtcNow);
        page.Show(services.GetRequiredService<OrganizeAnalysis>().Run(TestContext.Current.CancellationToken));
        Assert.Empty(page.Groups);
        Assert.Equal("You excluded organizing in Settings, so nothing is suggested.", page.Summary);
    }

    [Fact]
    public void Inactive_projects_follow_the_threshold_and_a_restricted_one_is_never_archived()
    {
        Age("user/Documents/Code/Clean", months: 20);
        Age("user/Documents/Code/Tied", months: 20);
        root.CreateFile("user/Documents/Code/Fresh/Cargo.toml", "[package]");
        using var services = TestServices.Build(root);
        var main = services.GetRequiredService<MainViewModel>();
        var page = Open(services);
        main.Current = page;
        OrganizeProjectViewModel Project(string name) => page.Projects.Single(p => p.Name == name);

        Assert.True(Project("Clean").IsInactive && Project("Tied").IsInactive);
        Assert.False(Project("Fresh").IsInactive);
        Assert.Null(Project("Fresh").Inactive);
        Assert.Equal("2 projects have not changed for 12 months or more.", page.ArchiveSummary);
        Assert.Contains("Archiving moves the whole folder into your archive folder", Project("Clean").Inactive);

        page.ArchiveMonths = "24";
        page.SaveArchiveMonthsCommand.Execute(null);
        Assert.False(Project("Clean").IsInactive);
        Assert.Equal("0 projects have not changed for 24 months or more.", page.ArchiveSummary);
        Assert.Equal(24, AppSettings.Load(services.GetRequiredService<AppPaths>().Settings).ArchiveAfterMonths);

        page.ArchiveMonths = "0";
        page.SaveArchiveMonthsCommand.Execute(null);
        Assert.Equal("Enter a whole number of months from 1 to 120.", page.ArchiveMessage);
        page.ArchiveMonths = "12";
        page.SaveArchiveMonthsCommand.Execute(null);
        Assert.Null(page.ArchiveMessage);

        Project("Tied").IsSelected = true;
        Project("Fresh").IsSelected = true;
        page.ArchiveCommand.Execute(null);
        Assert.False(Project("Tied").IsSelected);
        Assert.Equal("Tick at least one project that has not changed for the set number of months.", page.ArchiveMessage);

        Project("Clean").IsSelected = true;
        page.ArchiveCommand.Execute(null);
        Assert.Equal("Choose your archive folder first.", page.ArchiveMessage);

        var archive = root.CreateDirectory("user/Documents/Archive");
        ((TestServices.FixturePicker)services.GetRequiredService<IFolderPicker>()).Next = archive;
        page.BrowseArchiveCommand.Execute(null);
        page.ArchiveCommand.Execute(null);

        var preview = Assert.IsType<PreviewViewModel>(main.Current);
        Assert.Equal(root.Resolve("user/Documents/Code/Clean"), Assert.Single(preview.Items).Path);
        Assert.Contains("It is moved whole, not deleted.", preview.Recommendation.WhyItMatters);
        preview.ApproveCommand.Execute(null);

        Assert.Equal(ExecutionStatus.Completed, preview.Execution!.Status);
        Assert.True(File.Exists(Path.Combine(archive, "Clean", "package.json")));
        Assert.True(Directory.Exists(root.Resolve("user/Documents/Code/Fresh")));
    }

    private void Age(string folder, int months)
    {
        foreach (var file in Directory.EnumerateFiles(root.Resolve(folder), "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMonths(-months));
        }
    }

    private static OrganizeViewModel Open(ServiceProvider services)
    {
        var page = services.GetRequiredService<OrganizeViewModel>();
        page.Show(services.GetRequiredService<OrganizeAnalysis>().Run(TestContext.Current.CancellationToken));
        return page;
    }
}

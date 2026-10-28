using System.Text;
using PCHealth.Inventory;
using PCHealth.Testing;

namespace PCHealth.Organization.Tests;

public sealed class DependencyScannerTests : IDisposable
{
    private readonly TempRoot root = new();

    public void Dispose() => root.Dispose();

    [Theory]
    [InlineData(@"..\..\x", DependencyKind.LeavesFolder)]
    [InlineData(@"a\..\..\..\x", DependencyKind.LeavesFolder)]
    [InlineData(@"..\\/..//x", DependencyKind.LeavesFolder)]
    [InlineData(@"""..\..\quoted""", DependencyKind.LeavesFolder)]
    [InlineData(@"..", null)]
    [InlineData(@"..\x", null)]
    [InlineData(@"a/../../x", null)]
    [InlineData(@"./x/./y/", null)]
    [InlineData(@"C:\x", DependencyKind.AbsolutePath)]
    [InlineData(@"c:/x", DependencyKind.AbsolutePath)]
    [InlineData(@"C:x", DependencyKind.AbsolutePath)]
    [InlineData(@"\x", DependencyKind.AbsolutePath)]
    [InlineData(@"/x", DependencyKind.AbsolutePath)]
    [InlineData(@"\\?\C:\x", DependencyKind.AbsolutePath)]
    [InlineData(@"\\server\share\x", DependencyKind.NetworkPath)]
    [InlineData(@"//server/share", DependencyKind.NetworkPath)]
    [InlineData(@"\/server\share", DependencyKind.NetworkPath)]
    [InlineData(@"\\?\UNC\server\share", DependencyKind.NetworkPath)]
    [InlineData(@"%USERPROFILE%\x", DependencyKind.Variable)]
    [InlineData(@"$env:TEMP\x", DependencyKind.Variable)]
    [InlineData(@"${env:TEMP}/x", DependencyKind.Variable)]
    [InlineData(@"$(SomeRoot)\x", DependencyKind.Variable)]
    [InlineData(@"~/x", DependencyKind.Variable)]
    [InlineData("file:///C:/x", DependencyKind.AbsolutePath)]
    [InlineData("file://server/share/x", DependencyKind.NetworkPath)]
    [InlineData("https://example.com/a/../../..", null)]
    [InlineData("", null)]
    public void Paths_are_classified_with_windows_rules(string reference, DependencyKind? expected)
    {
        Assert.Equal(expected, PathReferences.Classify(reference, ["src"]));
    }

    [Theory]
    [InlineData(@"lib\%CONFIG%\..\..\x", null)]
    [InlineData(@"lib\$(Configuration)\..\..\..\x", DependencyKind.LeavesFolder)]
    [InlineData(@"%ROOT%\lib", DependencyKind.Variable)]
    public void A_variable_inside_a_path_counts_as_one_folder(string reference, DependencyKind? expected)
    {
        Assert.Equal(expected, PathReferences.Classify(PathReferences.MiddleVariablesToPlaceholders(reference), []));
    }

    [Fact]
    public void Every_format_is_checked_and_each_dependency_is_listed()
    {
        root.CreateFile("Proj/Proj.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Other", "..\Other\Other.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);
        root.CreateFile("Proj/Proj.slnx", """
            <Solution>
              <Project Path="src/App/App.csproj" />
              <Project Path="C:/Elsewhere/X.csproj" />
            </Solution>
            """);
        root.CreateFile("Proj/src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$(MSBuildExtensionsPath)\x.targets" />
              <Import Sdk="Microsoft.NET.Sdk" Project="Sdk.props" />
              <Import Project="$(MSBuildThisFileDirectory)..\..\..\build.props" />
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj;..\..\..\Shared\Shared.csproj" />
                <Reference Include="X">
                  <HintPath>$(SolutionDir)packages\x.dll</HintPath>
                </Reference>
                <Reference Include="Y">
                  <HintPath>\\server\libs\y.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        root.CreateFile("Proj/web/package.json", """
            {
              "name": "web",
              "dependencies": { "local": "file:../shared", "far": "file:../../outside", "npm": "^1.0.0" },
              "devDependencies": { "linked": "link:C:/dev/x", "git": "git+https://example.com/x.git" }
            }
            """);
        root.CreateFile("Proj/Proj.code-workspace", """
            {
              // folders open together in VS Code
              "folders": [ { "path": "." }, { "path": "../sibling" }, ],
            }
            """);
        root.CreateFile("Proj/scripts/build.cmd", """
            @echo off
            REM C:\comment\ignored
            :: D:\also\ignored
            set OUT=%~dp0..\out
            xcopy /s %OUT%\* "%USERPROFILE%\Desktop\drop"
            for %%f in (*.txt) do echo %%f
            call %~dp0..\..\tool.cmd
            """);
        root.CreateFile("Proj/scripts/deploy.ps1", """
            # C:\comment\ignored
            <#
              \\nas\ignored
            #>
            $root = Join-Path $PSScriptRoot "..\.."
            Copy-Item -Path "$PSScriptRoot\..\dist" -Destination "$env:APPDATA\App"
            $half = $total / 2
            Get-Content \\nas\share\list.txt
            Invoke-WebRequest https://example.com/a/b
            """);
        File.WriteAllBytes(root.Resolve("Proj/tool.lnk"), LocalShortcut(@"C:\Tools\tool.exe", workingDirectory: @"C:\Tools"));
        root.CreateFile("Proj/node_modules/pkg/package.json", """{ "dependencies": { "x": "file:../../../../x" } }""");
        root.CreateFile("Proj/src/App/bin/Debug/copy.csproj", """<Project><Import Project="C:\x.props" /></Project>""");

        var report = Scan("Proj");

        Assert.True(report.MoveRestricted);
        Assert.Equal(
            [
                ("Proj.code-workspace", "../sibling", DependencyKind.LeavesFolder),
                ("Proj.sln", @"..\Other\Other.csproj", DependencyKind.LeavesFolder),
                ("Proj.slnx", "C:/Elsewhere/X.csproj", DependencyKind.AbsolutePath),
                ("build.cmd", @"%USERPROFILE%\Desktop\drop", DependencyKind.Variable),
                ("build.cmd", @"%~dp0..\..\tool.cmd", DependencyKind.LeavesFolder),
                ("deploy.ps1", @"..\..", DependencyKind.LeavesFolder),
                ("deploy.ps1", @"$env:APPDATA\App", DependencyKind.Variable),
                ("deploy.ps1", @"\\nas\share\list.txt", DependencyKind.NetworkPath),
                ("App.csproj", @"$(MSBuildThisFileDirectory)..\..\..\build.props", DependencyKind.LeavesFolder),
                ("App.csproj", @"..\..\..\Shared\Shared.csproj", DependencyKind.LeavesFolder),
                ("App.csproj", @"\\server\libs\y.dll", DependencyKind.NetworkPath),
                ("tool.lnk", @"C:\Tools\tool.exe", DependencyKind.AbsolutePath),
                ("tool.lnk", @"C:\Tools", DependencyKind.AbsolutePath),
                ("package.json", "far: file:../../outside", DependencyKind.LeavesFolder),
                ("package.json", "linked: link:C:/dev/x", DependencyKind.AbsolutePath),
            ],
            report.Dependencies.Select(d => (Path.GetFileName(d.SourceFile), d.Reference, d.Kind)));
        Assert.Equal("Proj.sln line 4 refers to a path that leads outside the folder: ..\\Other\\Other.csproj",
            report.Dependencies.Single(d => d.SourceFile.EndsWith("Proj.sln", StringComparison.Ordinal)).Reason);
        Assert.Equal(report.Dependencies.Count, report.Reasons.Count);
        Assert.Empty(report.NotChecked);
        Assert.StartsWith("Can't be moved here: 15 paths point", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Formats_that_were_not_read_are_listed_so_a_clean_result_never_claims_completeness()
    {
        root.CreateFile("Clean/package.json", """{ "dependencies": { "a": "^1.0.0" } }""");
        root.CreateFile("Clean/scene.blend", "x");
        root.CreateFile("Clean/build.sh", "cp ../x .");
        root.CreateFile("Clean/Broken/Broken.csproj", "<Project");
        root.CreateFile("Clean/broken.lnk", "not a shortcut");
        root.CreateFile("Clean/workspace.code-workspace", "{ not json");
        root.CreateFile("Clean/readme.txt", @"C:\not\a\format\we\read");

        var report = Scan("Clean");

        Assert.False(report.MoveRestricted);
        Assert.Equal(
            ["Broken.csproj", "broken.lnk", "build.sh", "scene.blend", "workspace.code-workspace"],
            report.NotChecked.Select(n => Path.GetFileName(n.Path)).Order(StringComparer.Ordinal));
        Assert.Contains("5 files were not checked", report.Summary, StringComparison.Ordinal);
        Assert.Contains("may still refer to this folder", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Even_a_fully_checked_clean_folder_says_it_is_not_a_guarantee()
    {
        root.CreateFile("Tidy/package.json", """{ "dependencies": { "local": "file:./packages/a" } }""");
        root.CreateFile("Tidy/run.cmd", @"call %~dp0tools\x.cmd");

        var report = Scan("Tidy");

        Assert.False(report.MoveRestricted);
        Assert.Empty(report.NotChecked);
        Assert.Contains("Programs and settings elsewhere may still refer to this folder.", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_detected_project_is_scanned_with_its_own_files()
    {
        root.CreateFile("Code/App/App.csproj", """<Project><ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup></Project>""");
        root.CreateFile("Code/Lib/Lib.csproj", "<Project />");

        var projects = ProjectDetector.Find(new RecursiveScanner().Scan(root.Path, cancellationToken: TestContext.Current.CancellationToken));
        var report = DependencyScanner.Scan(projects.Single(p => p.Name == "App"));

        Assert.Equal(DependencyKind.LeavesFolder, Assert.Single(report.Dependencies).Kind);
    }

    [Fact]
    public void Network_shortcuts_are_read_from_their_bytes()
    {
        var link = ShellLink.Parse(NetworkShortcut(@"\\nas\share", @"docs\a.txt"));

        Assert.Equal(@"\\nas\share\docs\a.txt", link.Target);
    }

    [Fact]
    public void A_truncated_shortcut_is_rejected()
    {
        var bytes = LocalShortcut(@"C:\Tools\tool.exe", workingDirectory: null);

        Assert.Throws<InvalidDataException>(() => ShellLink.Parse(bytes.AsSpan(0, bytes.Length - 12)));
    }

    private DependencyReport Scan(string folder)
    {
        var path = root.Resolve(folder);
        var files = new RecursiveScanner().Scan(path).Entries.OfType<PCHealth.Core.FileEntry>();
        return DependencyScanner.Scan(path, files);
    }

    private static readonly byte[] LinkClsid = new Guid("00021401-0000-0000-c000-000000000046").ToByteArray();

    /// <summary>A minimal [MS-SHLLINK] file with a LinkInfo local base path and, optionally, a working directory.</summary>
    internal static byte[] LocalShortcut(string target, string? workingDirectory)
    {
        var volumeId = new byte[0x11];
        BitConverter.GetBytes(0x11).CopyTo(volumeId, 0);
        BitConverter.GetBytes(0x10).CopyTo(volumeId, 12);
        var basePath = Encoding.Latin1.GetBytes(target + "\0");
        var info = LinkInfo(flags: 1, volumeId, basePath, offsetField: 16, suffix: [0]);
        return Shortcut(info, workingDirectory);
    }

    private static byte[] NetworkShortcut(string share, string suffix)
    {
        var name = Encoding.Latin1.GetBytes(share + "\0");
        var network = new byte[0x14 + name.Length];
        BitConverter.GetBytes(network.Length).CopyTo(network, 0);
        BitConverter.GetBytes(0x14).CopyTo(network, 8);
        name.CopyTo(network, 0x14);
        var info = LinkInfo(flags: 2, [], network, offsetField: 20, suffix: Encoding.Latin1.GetBytes(suffix + "\0"));
        return Shortcut(info, workingDirectory: null);
    }

    private static byte[] LinkInfo(uint flags, byte[] volumeId, byte[] body, int offsetField, byte[] suffix)
    {
        const int header = 0x1C;
        var info = new byte[header + volumeId.Length + body.Length + suffix.Length];
        BitConverter.GetBytes(info.Length).CopyTo(info, 0);
        BitConverter.GetBytes(header).CopyTo(info, 4);
        BitConverter.GetBytes(flags).CopyTo(info, 8);
        if (volumeId.Length > 0)
        {
            BitConverter.GetBytes(header).CopyTo(info, 12);
        }

        BitConverter.GetBytes(header + volumeId.Length).CopyTo(info, offsetField);
        BitConverter.GetBytes(header + volumeId.Length + body.Length).CopyTo(info, 24);
        volumeId.CopyTo(info, header);
        body.CopyTo(info, header + volumeId.Length);
        suffix.CopyTo(info, header + volumeId.Length + body.Length);
        return info;
    }

    private static byte[] Shortcut(byte[] linkInfo, string? workingDirectory)
    {
        var flags = 0x2u | 0x80u | (workingDirectory is null ? 0u : 0x10u);
        var header = new byte[0x4C];
        BitConverter.GetBytes(0x4C).CopyTo(header, 0);
        LinkClsid.CopyTo(header, 4);
        BitConverter.GetBytes(flags).CopyTo(header, 0x14);
        List<byte> data = [.. header, .. linkInfo];
        if (workingDirectory is not null)
        {
            data.AddRange(BitConverter.GetBytes((ushort)workingDirectory.Length));
            data.AddRange(Encoding.Unicode.GetBytes(workingDirectory));
        }

        return [.. data];
    }
}

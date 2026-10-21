using System.Runtime.InteropServices;
using System.Text;
using PCHealth.Testing;

namespace PCHealth.Execution.Windows.Tests;

/// <summary>Adversarial path cases for handle-based resolution (spec §6.3, §9).</summary>
public class WindowsPathResolverTests
{
    private readonly WindowsPathResolver resolver = new();

    [Fact]
    public void Resolves_a_file_to_its_canonical_path()
    {
        using var root = new TempRoot();
        root.CreateFile("Folder/MixedCase.TXT", "x");

        var result = Resolve(root.Resolve("Folder/MixedCase.TXT"));

        Assert.True(result.IsResolved, result.Message);
        Assert.EndsWith(@"\Folder\MixedCase.TXT", result.FinalPath, StringComparison.Ordinal);
        Assert.DoesNotContain("~", result.FinalPath);
        Assert.False(result.IsDirectory);
        Assert.False(result.IsReparsePoint);
    }

    [Fact]
    public void Resolves_directories()
    {
        using var root = new TempRoot();
        var directory = root.CreateDirectory("Sub");

        var result = Resolve(directory);

        Assert.True(result.IsResolved, result.Message);
        Assert.True(result.IsDirectory);
    }

    [Fact]
    public void Dot_dot_segments_resolve_to_where_they_really_lead()
    {
        using var root = new TempRoot();
        root.CreateFile("a/file.txt");
        root.CreateDirectory("b");
        var expected = Resolve(root.Resolve("a/file.txt")).FinalPath;

        var result = Resolve(root.Path + @"\b\..\b\..\a\.\file.txt");

        Assert.Equal(expected, result.FinalPath);
    }

    [Fact]
    public void Dot_dot_out_of_a_folder_yields_the_outside_location_not_the_folder()
    {
        using var root = new TempRoot();
        root.CreateDirectory("inside");
        root.CreateFile("outside.txt");

        var result = Resolve(root.Resolve("inside") + @"\..\outside.txt");

        Assert.Equal(Resolve(root.Resolve("outside.txt")).FinalPath, result.FinalPath);
        Assert.DoesNotContain(@"\inside", result.FinalPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mixed_case_input_resolves_to_the_true_case()
    {
        using var root = new TempRoot();
        var path = root.CreateFile("Folder/MixedCase.TXT");

        var upper = Resolve(path.ToUpperInvariant());
        var lower = Resolve(path.ToLowerInvariant());

        Assert.Equal(Resolve(path).FinalPath, upper.FinalPath);
        Assert.Equal(Resolve(path).FinalPath, lower.FinalPath);
    }

    [Fact]
    public void Short_8_3_names_expand_to_long_names()
    {
        using var root = new TempRoot();
        var path = root.CreateFile("A Long Directory Name/Another Long File Name.txt");
        var shortPath = ShortPath(path);
        if (shortPath is null || !shortPath.Contains('~'))
        {
            Assert.Skip("8.3 name generation is disabled on this volume.");
        }

        var result = Resolve(shortPath);

        Assert.Equal(Resolve(path).FinalPath, result.FinalPath);
        Assert.EndsWith(@"\A Long Directory Name\Another Long File Name.txt", result.FinalPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_path_prefix_resolves_to_the_same_path()
    {
        using var root = new TempRoot();
        var path = root.CreateFile("file.txt");

        var result = Resolve(@"\\?\" + path);

        Assert.Equal(Resolve(path).FinalPath, result.FinalPath);
        Assert.False(result.FinalPath!.StartsWith(@"\\?\", StringComparison.Ordinal));
    }

    [Fact]
    public void Paths_longer_than_260_characters_resolve()
    {
        using var root = new TempRoot();
        var path = root.CreateFile(string.Join('/', Enumerable.Repeat(new string('d', 60), 5)) + "/long.txt");

        var result = Resolve(path);

        Assert.True(result.IsResolved, result.Message);
        Assert.True(result.FinalPath!.Length > 260);
    }

    [Theory]
    [InlineData(@"\\.\C:\Windows")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\Windows")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Windows")]
    [InlineData(@"\??\C:\Windows")]
    public void Device_namespaces_are_rejected(string path)
    {
        AssertRejected(path, PathRejectionReason.UnsupportedPrefix);
    }

    [Theory]
    [InlineData(@"\\localhost\C$\Windows")]
    [InlineData(@"\\?\UNC\localhost\C$\Windows")]
    [InlineData(@"\\server\share\file.txt")]
    public void Unc_paths_are_rejected(string path)
    {
        AssertRejected(path, PathRejectionReason.NetworkPath);
    }

    [Fact]
    public void Alternate_data_streams_are_rejected()
    {
        using var root = new TempRoot();
        var path = root.CreateFile("file.txt", "x");

        AssertRejected(path + ":hidden", PathRejectionReason.AlternateDataStream);
        AssertRejected(path + "::$DATA", PathRejectionReason.AlternateDataStream);
        AssertRejected(@"\\?\" + path + ":hidden:$DATA", PathRejectionReason.AlternateDataStream);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("Aux.log")]
    [InlineData("COM1")]
    [InlineData("lpt9.dat")]
    [InlineData("CON .txt")]
    [InlineData("COM¹")]
    public void Reserved_device_names_are_rejected(string name)
    {
        using var root = new TempRoot();

        AssertRejected(root.Path + @"\" + name, PathRejectionReason.ReservedDeviceName);
        AssertRejected(root.Path + @"\" + name + @"\child.txt", PathRejectionReason.ReservedDeviceName);
    }

    [Fact]
    public void Names_that_only_start_like_device_names_are_allowed()
    {
        using var root = new TempRoot();
        var path = root.CreateFile("CONSOLE.txt");

        Assert.True(Resolve(path).IsResolved);
    }

    [Theory]
    [InlineData("file.txt.")]
    [InlineData("file.txt ")]
    [InlineData("file.txt. . ")]
    public void Trailing_dots_and_spaces_are_rejected(string name)
    {
        using var root = new TempRoot();
        root.CreateFile("file.txt");

        AssertRejected(root.Path + @"\" + name, PathRejectionReason.TrailingDotOrSpace);
        AssertRejected(@"\\?\" + root.Path + @"\" + name, PathRejectionReason.TrailingDotOrSpace);
    }

    [Fact]
    public void Trailing_dot_folders_are_rejected()
    {
        using var root = new TempRoot();
        root.CreateFile("dir/file.txt");

        AssertRejected(root.Path + @"\dir.\file.txt", PathRejectionReason.TrailingDotOrSpace);
    }

    [Theory]
    [InlineData("relative\\file.txt")]
    [InlineData("C:relative.txt")]
    [InlineData("\\rooted\\without\\drive.txt")]
    [InlineData("C:")]
    public void Paths_that_are_not_fully_qualified_are_rejected(string path)
    {
        AssertRejected(path, PathRejectionReason.NotFullyQualified);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\bad<name>.txt")]
    [InlineData("C:\\bad|name.txt")]
    public void Invalid_syntax_is_rejected(string path)
    {
        AssertRejected(path, PathRejectionReason.InvalidSyntax);
    }

    [Fact]
    public void Wildcards_fail_closed()
    {
        using var root = new TempRoot();
        root.CreateFile("file.txt");

        var result = Resolve(root.Path + @"\*.txt");

        Assert.False(result.IsResolved);
        Assert.Equal(PathRejectionReason.InvalidSyntax, result.Rejection);
    }

    [Fact]
    public void Missing_paths_fail_closed()
    {
        using var root = new TempRoot();

        AssertRejected(root.Resolve("missing.txt"), PathRejectionReason.NotFound);
        AssertRejected(root.Resolve("missing/child.txt"), PathRejectionReason.NotFound);
    }

    private PathResolution Resolve(string path) => resolver.Resolve(path);

    private void AssertRejected(string path, PathRejectionReason reason)
    {
        var result = Resolve(path);
        Assert.False(result.IsResolved, $"'{path}' resolved to '{result.FinalPath}'.");
        Assert.Null(result.FinalPath);
        Assert.Equal(reason, result.Rejection);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    private static string? ShortPath(string longPath)
    {
        var buffer = new StringBuilder(1024);
        var length = GetShortPathNameW(longPath, buffer, (uint)buffer.Capacity);
        return length == 0 || length >= buffer.Capacity ? null : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);
}

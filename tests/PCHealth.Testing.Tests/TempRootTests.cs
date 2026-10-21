using PCHealth.Testing;

namespace PCHealth.Testing.Tests;

public class TempRootTests
{
    [Fact]
    public void Creates_root_under_system_temp_and_deletes_it_on_dispose()
    {
        string path;
        using (var root = new TempRoot())
        {
            path = root.Path;
            Assert.True(Directory.Exists(path));
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), path);
            root.CreateFile("a/b/file.txt", "x");
        }

        Assert.False(Directory.Exists(path));
    }

    [Theory]
    [InlineData("file.txt")]
    [InlineData("a/b/c.txt")]
    [InlineData("a/../b.txt")]
    [InlineData(".")]
    public void Resolve_accepts_paths_inside_the_root(string relative)
    {
        using var root = new TempRoot();

        var full = root.Resolve(relative);

        Assert.StartsWith(root.Path, full);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    public void Resolve_throws_for_relative_paths_that_escape(string relative)
    {
        using var root = new TempRoot();

        Assert.Throws<ArgumentException>(() => root.Resolve(relative));
    }

    [Fact]
    public void Resolve_throws_for_absolute_paths_outside_the_root()
    {
        using var root = new TempRoot();

        Assert.Throws<ArgumentException>(() => root.Resolve(Path.GetTempPath()));
        Assert.Throws<ArgumentException>(() => root.Resolve(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    [Fact]
    public void Resolve_throws_for_a_sibling_that_shares_the_root_name_as_prefix()
    {
        using var root = new TempRoot();

        Assert.Throws<ArgumentException>(() => root.Resolve(root.Path + "-sibling"));
    }

    [Fact]
    public void Create_methods_refuse_outside_paths_and_write_nothing()
    {
        using var root = new TempRoot();
        var outside = Path.Combine(Path.GetDirectoryName(root.Path)!, Guid.NewGuid().ToString("N"));

        Assert.Throws<ArgumentException>(() => root.CreateFile(Path.Combine(outside, "file.txt")));
        Assert.Throws<ArgumentException>(() => root.CreateDirectory(outside));
        Assert.False(Directory.Exists(outside));
    }
}

using PCHealth.Testing;

namespace PCHealth.Core.Tests;

public class EntryTests
{
    private static readonly DateTimeOffset Written = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void File_entry_keeps_path_name_size_and_attributes()
    {
        using var root = new TempRoot();
        var path = root.Resolve("dir/report.pdf");

        var entry = new FileEntry(path, new MeasuredSize(42), FileAttributes.Hidden, Written);

        Assert.Equal(path, entry.FullPath);
        Assert.Equal("report.pdf", entry.Name);
        Assert.Equal(42, entry.Size.Bytes);
        Assert.Equal(Written, entry.LastWriteTimeUtc);
        Assert.False(entry.IsReparsePoint);
    }

    [Fact]
    public void Directory_entry_name_ignores_a_trailing_separator()
    {
        using var root = new TempRoot();
        var path = root.Resolve("cache") + Path.DirectorySeparatorChar;

        var entry = new DirectoryEntry(path, FileAttributes.Directory, Written);

        Assert.Equal("cache", entry.Name);
    }

    [Fact]
    public void Reparse_points_are_flagged()
    {
        using var root = new TempRoot();

        var entry = new DirectoryEntry(root.Resolve("link"), FileAttributes.Directory | FileAttributes.ReparsePoint, Written);

        Assert.True(entry.IsReparsePoint);
    }

    [Theory]
    [InlineData("relative/file.txt")]
    [InlineData("")]
    public void Entries_require_a_fully_qualified_path(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => new FileEntry(path, MeasuredSize.Zero, FileAttributes.Normal, Written));
        Assert.ThrowsAny<ArgumentException>(() => new DirectoryEntry(path, FileAttributes.Directory, Written));
    }
}

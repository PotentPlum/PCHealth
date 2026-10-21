namespace PCHealth.Testing;

/// <summary>
/// A directory that a test creates under the system temp directory and deletes on dispose.
/// Every path a test builds goes through <see cref="Resolve"/>, which throws for anything outside the root,
/// so tests can never reach real user folders.
/// </summary>
public sealed class TempRoot : IDisposable
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public TempRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCHealth.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>The absolute path of the root directory.</summary>
    public string Path { get; }

    /// <summary>Returns the absolute path for <paramref name="relativePath"/>, or throws if it falls outside the root.</summary>
    public string Resolve(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
        EnsureInside(full);
        return full;
    }

    /// <summary>Throws if <paramref name="path"/> is not the root or a path below it.</summary>
    public void EnsureInside(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (string.Equals(full, Path, PathComparison)
            || full.StartsWith(Path + System.IO.Path.DirectorySeparatorChar, PathComparison))
        {
            return;
        }

        throw new ArgumentException($"Path '{path}' is outside the test root '{Path}'.", nameof(path));
    }

    /// <summary>Creates a directory below the root and returns its absolute path.</summary>
    public string CreateDirectory(string relativePath)
    {
        var full = Resolve(relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Creates a file below the root, including missing parent directories, and returns its absolute path.</summary>
    public string CreateFile(string relativePath, string contents = "")
    {
        var full = Resolve(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

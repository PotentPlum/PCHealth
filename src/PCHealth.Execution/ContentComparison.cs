namespace PCHealth.Execution;

/// <summary>
/// Byte-for-byte comparison of two files (D-006): the last check before a duplicate is removed, so a hash collision or
/// a change after the scan can never cost the user the copy they meant to keep. Both files are only read.
/// </summary>
internal static class ContentComparison
{
    private const int BufferBytes = 1024 * 1024;

    /// <exception cref="IOException">Either file could not be read; the caller then refuses (fail closed).</exception>
    public static bool Identical(string path, string otherPath)
    {
        using var first = Open(path);
        using var second = Open(otherPath);
        if (first.Length != second.Length)
        {
            return false;
        }

        var a = new byte[BufferBytes];
        var b = new byte[BufferBytes];
        while (true)
        {
            var read = first.ReadAtLeast(a, a.Length, throwOnEndOfStream: false);
            if (second.ReadAtLeast(b.AsSpan(0, read), read, throwOnEndOfStream: false) != read || !a.AsSpan(0, read).SequenceEqual(b.AsSpan(0, read)))
            {
                return false;
            }

            if (read < a.Length)
            {
                return second.ReadByte() == -1;
            }
        }
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
}

using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using PCHealth.Core;

namespace PCHealth.Duplicates;

/// <summary>Read-only access to file contents for hashing. Hashes are SHA-256 (D-006).</summary>
internal static class ContentReader
{
    public const string Algorithm = "SHA-256";

    private const int BufferSize = 1024 * 1024;

    /// <summary>Opens a file for reading only; other processes may keep reading, writing or deleting it.</summary>
    public static SafeFileHandle OpenRead(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);

    /// <summary>SHA-256 over the first and last <paramref name="sampleBytes"/> bytes, or the whole file if it is smaller than both.</summary>
    public static byte[] HeadTailDigest(SafeFileHandle handle, long length, int sampleBytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (length <= 2L * sampleBytes)
        {
            AppendRange(hash, handle, 0, length);
        }
        else
        {
            AppendRange(hash, handle, 0, sampleBytes);
            AppendRange(hash, handle, length - sampleBytes, sampleBytes);
        }

        return hash.GetHashAndReset();
    }

    public static byte[] FullDigest(SafeFileHandle handle, long length, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendRange(hash, handle, 0, length, cancellationToken);
        return hash.GetHashAndReset();
    }

    private static void AppendRange(IncrementalHash hash, SafeFileHandle handle, long offset, long count, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[(int)Math.Min(BufferSize, Math.Max(count, 1))];
        while (count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, count)), offset);
            if (read == 0)
            {
                throw new FileChangedException("File ended before its recorded length; it changed while being read.");
            }

            hash.AppendData(buffer, 0, read);
            offset += read;
            count -= read;
        }
    }
}

/// <summary>The file no longer matches the scanned entry, so earlier results about it cannot be trusted.</summary>
internal sealed class FileChangedException(string message) : IOException(message);

/// <summary>Why a candidate file was dropped while reading its contents.</summary>
internal static class ReadFailures
{
    /// <summary>Maps a read failure to evidence, or returns null for exceptions that are not file-access problems.</summary>
    public static InaccessibleEvidence? ToEvidence(FileEntry file, Exception ex) => ex switch
    {
        UnauthorizedAccessException => new(file.FullPath, InaccessibleReason.AccessDenied, Describe(ex)),
        FileNotFoundException or DirectoryNotFoundException => new(file.FullPath, InaccessibleReason.Vanished, Describe(ex)),
        IOException and not FileChangedException => new(file.FullPath, InaccessibleReason.IoError, Describe(ex)),
        _ => null,
    };

    private static string Describe(Exception ex) => $"Could not read file: {ex.GetType().Name}: {ex.Message}";
}

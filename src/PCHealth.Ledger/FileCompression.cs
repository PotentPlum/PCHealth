namespace PCHealth.Ledger;

/// <summary>
/// Compression of single files where they are (T-55, D-030). Windows implements it with WOF XPRESS8K; tests use a
/// fake. Every method takes a final path the caller has just resolved and checked; nothing here decides whether a
/// change is allowed.
/// </summary>
public interface IFileCompression
{
    /// <summary>Why the file can't be compressed (unsupported volume, encrypted, already compressed); null when it can.</summary>
    /// <exception cref="IOException">The check could not be made; the caller then leaves the file alone.</exception>
    string? CannotCompress(string path);

    /// <summary>True while the file is stored compressed.</summary>
    bool IsCompressed(string path);

    /// <summary>The space the file takes on disk now, measured.</summary>
    long SizeOnDisk(string path);

    /// <summary>Free space on the volume that holds <paramref name="path"/>, for the current user.</summary>
    long FreeSpace(string path);

    /// <exception cref="CompressionNotBeneficialException">Compressing would not save space; nothing changed.</exception>
    /// <exception cref="IOException">The file could not be compressed; nothing changed.</exception>
    void Compress(string path);

    /// <exception cref="IOException">The file could not be decompressed; it stays compressed.</exception>
    void Decompress(string path);
}

/// <summary>The file system declined to compress a file because it would not get smaller.</summary>
public sealed class CompressionNotBeneficialException(string message) : IOException(message);

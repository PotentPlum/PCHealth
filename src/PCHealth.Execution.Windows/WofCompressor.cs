using System.ComponentModel;
using System.Runtime.InteropServices;
using PCHealth.Core;
using PCHealth.Inventory.Compression;

namespace PCHealth.Execution.Windows;

/// <summary>
/// XPRESS Huffman compression of one chunk with the Windows Compression API (cabinet.dll), in raw mode with no headers:
/// the same format WOF stores compressed files in (D-030), so an estimate reflects what the file system would save.
/// Only compresses in memory; it never touches a file.
/// </summary>
public sealed class WofCompressor : IChunkCompressor, IDisposable
{
    private const uint CompressAlgorithmXpressHuff = 4;
    private const uint CompressRaw = 0x20000000;
    private const int ErrorInsufficientBuffer = 122;

    private readonly IntPtr handle;
    private readonly byte[] output;

    public WofCompressor(CompressionAlgorithm algorithm = CompressionAlgorithm.Xpress8K)
    {
        ChunkBytes = algorithm switch
        {
            CompressionAlgorithm.Xpress4K => 4096,
            CompressionAlgorithm.Xpress8K => 8192,
            CompressionAlgorithm.Xpress16K => 16384,
            _ => throw new NotSupportedException("Only XPRESS can be estimated in memory; Windows has no API for LZX compression of a buffer."),
        };
        Algorithm = algorithm;
        if (!CreateCompressor(CompressAlgorithmXpressHuff | CompressRaw, IntPtr.Zero, out handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The Windows compression API is not available.");
        }

        output = new byte[ChunkBytes];
    }

    public CompressionAlgorithm Algorithm { get; }

    public int ChunkBytes { get; }

    public int CompressedSize(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length == 0)
        {
            return 0;
        }

        var input = chunk.ToArray();

        // The output buffer is no larger than the input: a chunk that would not shrink is stored as it is.
        if (Compress(handle, input, (nuint)input.Length, output, (nuint)output.Length, out var size))
        {
            return (int)Math.Min((ulong)chunk.Length, size);
        }

        var error = Marshal.GetLastPInvokeError();
        return error == ErrorInsufficientBuffer ? chunk.Length : throw new Win32Exception(error, "A chunk could not be compressed in memory.");
    }

    public void Dispose() => CloseCompressor(handle);

    [DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool CreateCompressor(uint algorithm, IntPtr allocationRoutines, out IntPtr compressorHandle);

    [DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool Compress(IntPtr compressorHandle, byte[] uncompressedData, nuint uncompressedDataSize, byte[] compressedBuffer, nuint compressedBufferSize, out nuint compressedDataSize);

    [DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool CloseCompressor(IntPtr compressorHandle);
}

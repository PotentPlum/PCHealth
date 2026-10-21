using Microsoft.Win32.SafeHandles;

namespace PCHealth.Core;

/// <summary>
/// The physical file behind a path: volume plus file ID. Hard links share one identity,
/// so removing one of them frees no space.
/// </summary>
public readonly record struct FileIdentity(ulong VolumeSerialNumber, UInt128 FileId);

/// <summary>Reads file identities. Windows code implements this; without one, every path counts as its own file.</summary>
public interface IFileIdentityProvider
{
    /// <summary>
    /// The identity of the open file, or null if the file system cannot report one.
    /// Implementations should use <paramref name="handle"/>; <paramref name="path"/> is for diagnostics and test doubles.
    /// </summary>
    FileIdentity? TryGetIdentity(SafeFileHandle handle, string path);
}

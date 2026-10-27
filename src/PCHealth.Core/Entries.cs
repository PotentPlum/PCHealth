namespace PCHealth.Core;

/// <summary>A file or directory as seen by a read-only inventory.</summary>
public abstract record FileSystemEntry
{
    protected FileSystemEntry(string path, FileAttributes attributes, DateTimeOffset lastWriteTimeUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"Entry path must be fully qualified: '{path}'.", nameof(path));
        }

        FullPath = path;
        Attributes = attributes;
        LastWriteTimeUtc = lastWriteTimeUtc;
    }

    public string FullPath { get; }

    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(FullPath));

    public FileAttributes Attributes { get; }

    public DateTimeOffset LastWriteTimeUtc { get; }

    /// <summary>Symlinks, junctions and other reparse points. Scanners record these but never follow them.</summary>
    public bool IsReparsePoint => Attributes.HasFlag(FileAttributes.ReparsePoint);
}

public sealed record FileEntry : FileSystemEntry
{
    /// <param name="creationTimeUtc">When the file was created on this volume, if the inventory read it.</param>
    public FileEntry(string path, MeasuredSize size, FileAttributes attributes, DateTimeOffset lastWriteTimeUtc, DateTimeOffset? creationTimeUtc = null)
        : base(path, attributes, lastWriteTimeUtc)
    {
        Size = size;
        CreationTimeUtc = creationTimeUtc;
    }

    public MeasuredSize Size { get; }

    public DateTimeOffset? CreationTimeUtc { get; }
}

public sealed record DirectoryEntry : FileSystemEntry
{
    public DirectoryEntry(string path, FileAttributes attributes, DateTimeOffset lastWriteTimeUtc)
        : base(path, attributes, lastWriteTimeUtc)
    {
    }
}

using System.Buffers.Binary;
using System.Text;

namespace PCHealth.Inventory;

/// <summary>
/// Reads the target of a Windows shortcut (<c>.lnk</c>, [MS-SHLLINK]) from its bytes, without asking the shell to resolve
/// it, so reading never touches the target or the network.
/// </summary>
public static class ShellLink
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 0x1;
    private const uint HasLinkInfo = 0x2;
    private const uint HasName = 0x4;
    private const uint HasRelativePath = 0x8;
    private const uint HasWorkingDir = 0x10;
    private const uint IsUnicode = 0x80;

    private const uint VolumeIdAndLocalBasePath = 0x1;
    private const uint CommonNetworkRelativeLinkAndPathSuffix = 0x2;

    private static readonly Guid LinkClsid = new("00021401-0000-0000-c000-000000000046");

    /// <param name="Target">The full target path, or null when the shortcut points at a shell item without a path.</param>
    /// <param name="RelativePath">The target relative to the shortcut, if the shortcut stores one.</param>
    /// <param name="WorkingDirectory">The folder the target starts in, if set.</param>
    public sealed record Link(string? Target, string? RelativePath, string? WorkingDirectory);

    /// <exception cref="InvalidDataException">The bytes are not a shell link.</exception>
    public static Link Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != HeaderSize
            || new Guid(data.Slice(4, 16)) != LinkClsid)
        {
            throw new InvalidDataException("Not a Windows shortcut.");
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]);
        var offset = HeaderSize;
        if ((flags & HasLinkTargetIdList) != 0)
        {
            offset += 2 + U16(data, offset);
        }

        string? target = null;
        if ((flags & HasLinkInfo) != 0)
        {
            var info = Slice(data, offset, (int)U32(data, offset));
            target = LinkInfoTarget(info);
            offset += info.Length;
        }

        if ((flags & HasName) != 0)
        {
            ReadString(data, ref offset, flags);
        }

        var relative = (flags & HasRelativePath) != 0 ? ReadString(data, ref offset, flags) : null;
        var workingDir = (flags & HasWorkingDir) != 0 ? ReadString(data, ref offset, flags) : null;
        return new Link(target, relative, workingDir);
    }

    private static string? LinkInfoTarget(ReadOnlySpan<byte> info)
    {
        var headerSize = U32(info, 4);
        var infoFlags = U32(info, 8);
        var suffix = CString(info, (int)U32(info, 24));
        if (headerSize >= 0x24)
        {
            var unicodeSuffix = U32(info, 32);
            if (unicodeSuffix != 0)
            {
                suffix = WString(info, (int)unicodeSuffix);
            }
        }

        if ((infoFlags & VolumeIdAndLocalBasePath) != 0)
        {
            var basePath = CString(info, (int)U32(info, 16));
            if (headerSize >= 0x24 && U32(info, 28) is var unicodeBase and not 0)
            {
                basePath = WString(info, (int)unicodeBase);
            }

            return Join(basePath, suffix);
        }

        if ((infoFlags & CommonNetworkRelativeLinkAndPathSuffix) != 0)
        {
            var network = Slice(info, (int)U32(info, 20), 20);
            var netName = CString(info, (int)U32(info, 20) + (int)U32(network, 8));
            return Join(netName, suffix);
        }

        return null;
    }

    private static string Join(string start, string suffix) =>
        suffix.Length == 0 ? start : start.TrimEnd('\\') + "\\" + suffix;

    private static string ReadString(ReadOnlySpan<byte> data, ref int offset, uint flags)
    {
        var count = U16(data, offset);
        offset += 2;
        var bytes = (flags & IsUnicode) != 0 ? count * 2 : count;
        var text = Slice(data, offset, bytes);
        offset += bytes;
        return (flags & IsUnicode) != 0 ? Encoding.Unicode.GetString(text) : Encoding.Latin1.GetString(text);
    }

    private static string CString(ReadOnlySpan<byte> data, int offset)
    {
        var rest = Slice(data, offset, data.Length - offset);
        var end = rest.IndexOf((byte)0);
        if (end < 0)
        {
            throw new InvalidDataException("A shortcut string has no end.");
        }

        return Encoding.Latin1.GetString(rest[..end]);
    }

    private static string WString(ReadOnlySpan<byte> data, int offset)
    {
        var rest = Slice(data, offset, data.Length - offset);
        for (var i = 0; i + 1 < rest.Length; i += 2)
        {
            if (rest[i] == 0 && rest[i + 1] == 0)
            {
                return Encoding.Unicode.GetString(rest[..i]);
            }
        }

        throw new InvalidDataException("A shortcut string has no end.");
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(data, offset, 2));

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(data, offset, 4));

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, int offset, int length) =>
        offset < 0 || length < 0 || offset > data.Length - length
            ? throw new InvalidDataException("The shortcut is shorter than its own structure says.")
            : data.Slice(offset, length);
}

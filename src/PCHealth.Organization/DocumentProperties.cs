using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PCHealth.Organization;

/// <summary>The title, subject and keywords a document carries, and whether a PDF carries a digital signature.</summary>
internal sealed record DocumentProperties(IReadOnlyList<string> Text, bool HasSignature)
{
    public static readonly DocumentProperties None = new([], false);
}

/// <summary>
/// Reads document properties without changing the file (spec §3.4): a bounded head and tail of a PDF, and
/// <c>docProps/core.xml</c> of an Office Open XML file. Anything else has no properties.
/// </summary>
internal static partial class DocumentPropertyReader
{
    /// <summary>How much of a PDF's start and end is read. Properties and signatures past these bounds are missed.</summary>
    internal const int PdfHeadBytes = 64 * 1024;
    internal const int PdfTailBytes = 256 * 1024;

    private const long MaxCoreXmlBytes = 64 * 1024;

    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".xlsx", ".pptx" };

    public static bool Reads(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) || OfficeExtensions.Contains(Path.GetExtension(path));

    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    public static DocumentProperties Read(string path)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ReadPdf(path);
        }

        return OfficeExtensions.Contains(Path.GetExtension(path)) ? ReadOffice(path) : DocumentProperties.None;
    }

    private static DocumentProperties ReadPdf(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = RandomAccess.GetLength(handle);
        var head = ReadRange(handle, 0, (int)Math.Min(length, PdfHeadBytes));
        var tailStart = Math.Max(head.Length, length - PdfTailBytes);
        var tail = ReadRange(handle, tailStart, (int)(length - tailStart));

        // Latin-1 maps every byte to one char, so offsets and ASCII keywords survive binary content.
        var text = Encoding.Latin1.GetString(head) + "\n" + Encoding.Latin1.GetString(tail);
        var values = PdfInfoString().Matches(text)
            .Select(m => DecodePdfString(m.Groups["value"].Value))
            .Where(v => v.Length > 0)
            .ToList();
        values.AddRange(XmpValue().Matches(text).Select(m => m.Groups["value"].Value.Trim()).Where(v => v.Length > 0));
        return new DocumentProperties(values, PdfSignature().IsMatch(text));
    }

    private static DocumentProperties ReadOffice(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            return DocumentProperties.None;
        }

        using (zip)
        {
            var core = zip.GetEntry("docProps/core.xml");
            if (core is null || core.Length > MaxCoreXmlBytes)
            {
                return DocumentProperties.None;
            }

            try
            {
                using var xml = core.Open();
                var document = XDocument.Load(xml);
                var values = document.Root?.Elements()
                    .Where(e => e.Name.LocalName is "title" or "subject" or "keywords" or "category" or "description")
                    .Select(e => e.Value.Trim())
                    .Where(v => v.Length > 0)
                    .ToArray() ?? [];
                return new DocumentProperties(values, false);
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException)
            {
                return DocumentProperties.None;
            }
        }
    }

    private static byte[] ReadRange(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long offset, int count)
    {
        var buffer = new byte[count];
        var done = 0;
        while (done < count)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(done), offset + done);
            if (read == 0)
            {
                break;
            }

            done += read;
        }

        return done == count ? buffer : buffer[..done];
    }

    /// <summary>A literal PDF string: UTF-16BE when it starts with a byte order mark, otherwise PDFDocEncoding (close to Latin-1).</summary>
    private static string DecodePdfString(string raw)
    {
        var unescaped = PdfEscape().Replace(raw, m => m.Groups[1].Value switch
        {
            "n" => "\n",
            "r" => "\r",
            "t" => "\t",
            var c when c.Length == 3 => ((char)Convert.ToInt32(c, 8)).ToString(),
            var c => c,
        });
        if (unescaped.Length >= 2 && unescaped[0] == 'þ' && unescaped[1] == 'ÿ')
        {
            var bytes = Encoding.Latin1.GetBytes(unescaped[2..]);
            return Encoding.BigEndianUnicode.GetString(bytes).Trim();
        }

        return unescaped.Trim();
    }

    [GeneratedRegex(@"/(?:Title|Subject|Keywords)\s*\((?<value>(?:\\.|[^\\)]){1,400})\)")]
    private static partial Regex PdfInfoString();

    [GeneratedRegex(@"<(?:dc:title|dc:subject|dc:description|pdf:Keywords)\b[^>]*>(?:\s*<rdf:\w+>\s*<rdf:li[^>]*>)?(?<value>[^<]{1,400})<")]
    private static partial Regex XmpValue();

    [GeneratedRegex(@"\\([0-7]{3}|.)")]
    private static partial Regex PdfEscape();

    // A signature dictionary (/Type /Sig) or a signed byte range; either means the PDF carries a signature.
    [GeneratedRegex(@"/Type\s*/Sig\b|/ByteRange\s*\[")]
    private static partial Regex PdfSignature();
}

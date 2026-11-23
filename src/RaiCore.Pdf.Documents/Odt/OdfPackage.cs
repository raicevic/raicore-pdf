using System.Text;
using System.Xml.Linq;
using RaiCore.Pdf.Cfb;
using RaiCore.Pdf.Documents.Docx;

namespace RaiCore.Pdf.Documents.Odt;

/// <summary>
/// An OpenDocument text document: a ZIP package (<c>mimetype</c>, <c>META-INF/manifest.xml</c>, <c>content.xml</c>,
/// <c>styles.xml</c>, <c>meta.xml</c>, <c>settings.xml</c> and the files they refer to), or a flat XML document
/// (<c>.fodt</c>, one <c>office:document</c> with everything in it). Parts are read within the limits of
/// <see cref="DocxPackage"/>; malformed data throws <see cref="PdfFormatException"/>, encrypted documents
/// <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class OdfPackage : IDisposable
{
    private readonly DocxPackage _package;
    private readonly ConversionContext _context;
    private long _binaryBytes;

    private OdfPackage(DocxPackage package, ConversionContext context, XElement content, XElement? styles, XElement? meta, XElement? settings, bool hasScripts)
    {
        _package = package;
        _context = context;
        Content = content;
        Styles = styles;
        Meta = meta;
        Settings = settings;
        HasScripts = hasScripts;
    }

    /// <summary>The content: <c>office:document-content</c>, or the flat document's <c>office:document</c>.</summary>
    public XElement Content { get; }

    /// <summary>The styles: <c>office:document-styles</c>, the flat document's <c>office:document</c>, or <c>null</c>.</summary>
    public XElement? Styles { get; }

    /// <summary>The metadata root (holding <c>office:meta</c>), or <c>null</c>.</summary>
    public XElement? Meta { get; }

    /// <summary>The settings root (holding <c>office:settings</c>), or <c>null</c>.</summary>
    public XElement? Settings { get; }

    /// <summary>Whether the document has scripts (macros), which are not run.</summary>
    public bool HasScripts { get; }

    /// <summary>Opens a document.</summary>
    /// <exception cref="PdfFormatException">The data is not an OpenDocument text document.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    public static OdfPackage Open(byte[] data, ConversionContext context)
    {
        if (data.AsSpan().StartsWith("PK"u8))
        {
            return OpenZip(data, context);
        }

        if (CompoundFile.IsCompoundFile(data))
        {
            throw new PdfFormatException("The data is a Compound File (a Word 97-2003 document or an encrypted Office document), not an OpenDocument text document.");
        }

        if (data.AsSpan().StartsWith(@"{\rtf"u8))
        {
            throw new PdfFormatException("The data is an RTF document, not an OpenDocument text document.");
        }

        if (!LooksLikeXml(data))
        {
            throw new PdfFormatException("The data is not an OpenDocument text document (a ZIP package or a flat XML document).");
        }

        var package = DocxPackage.ForXml(context);
        var root = package.Parse(data, "document");
        if (root is null || !Odf.Is(root, Odf.Office, "document"))
        {
            throw new PdfFormatException("The XML document is not a flat OpenDocument document (office:document).");
        }

        CheckText(root, Odf.Attr(root, Odf.Office, "mimetype"));
        var hasScripts = Odf.Child(root, Odf.Office, "scripts")?.HasElements == true;
        return new OdfPackage(package, context, root, root, root, root, hasScripts);
    }

    private static OdfPackage OpenZip(byte[] data, ConversionContext context)
    {
        var package = DocxPackage.OpenZip(data, context);
        try
        {
            var mimetype = package.Read("mimetype") is { } bytes ? Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 200)).Trim() : null;
            if (mimetype is not null && !mimetype.StartsWith("application/vnd.oasis.opendocument.", StringComparison.Ordinal))
            {
                throw new PdfFormatException($"The package is not an OpenDocument document (its media type is '{mimetype}').");
            }

            if (!package.Contains("content.xml"))
            {
                throw new PdfFormatException(package.Contains("[Content_Types].xml")
                    ? "The package is an Office Open XML document (such as DOCX), not an OpenDocument text document."
                    : "The package has no content.xml: it is not an OpenDocument document.");
            }

            if (package.Load("META-INF/manifest.xml") is { } manifest
                && manifest.Descendants(Odf.Manifest + "encryption-data").Any())
            {
                throw new NotSupportedException("The document is password-protected (encrypted), which is not supported.");
            }

            var content = package.Load("content.xml");
            if (!Odf.Is(content, Odf.Office, "document-content"))
            {
                throw new PdfFormatException("The part 'content.xml' is not OpenDocument content (office:document-content).");
            }

            CheckText(content!, mimetype);
            var styles = package.Load("styles.xml");
            var meta = package.Load("meta.xml");
            var settings = package.Load("settings.xml");
            var hasScripts = package.Paths.Any(p => p.StartsWith("Basic/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase))
                || Odf.Child(content, Odf.Office, "scripts")?.HasElements == true;
            return new OdfPackage(package, context, content!, styles, meta, settings, hasScripts);
        }
        catch
        {
            package.Dispose();
            throw;
        }
    }

    /// <summary>Checks that a document is a text document (by media type and body), naming what it is otherwise.</summary>
    private static void CheckText(XElement root, string? mimetype)
    {
        const string prefix = "application/vnd.oasis.opendocument.";
        var body = Odf.Child(root, Odf.Office, "body");
        if (mimetype is not null && mimetype.StartsWith(prefix, StringComparison.Ordinal) && !mimetype.StartsWith(prefix + "text", StringComparison.Ordinal))
        {
            throw new PdfFormatException($"The document is an OpenDocument {mimetype[prefix.Length..]}, not a text document.");
        }

        if (Odf.Child(body, Odf.Office, "text") is null)
        {
            var kind = body?.Elements().FirstOrDefault()?.Name.LocalName;
            throw new PdfFormatException(kind is null
                ? "The document has no text body (office:text): it is not an OpenDocument text document."
                : $"The document's body is office:{kind}, not office:text: it is not an OpenDocument text document.");
        }
    }

    /// <summary>Whether data starts like an XML document (after a byte order mark and white space).</summary>
    private static bool LooksLikeXml(byte[] data)
    {
        var text = DocxPackage.Decode(data.Length > 1024 ? data[..1024] : data);
        return text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('<');
    }

    /// <summary>
    /// The bytes of a file a document refers to (<c>Pictures/image.png</c>, also as <c>./Pictures/image.png</c>), or
    /// <c>null</c> if the package does not have it (always in a flat document).
    /// </summary>
    public byte[]? ReadFile(string href)
    {
        var path = DocxPackage.ResolveTarget(string.Empty, href);
        return path.Length > 0 && _package.Contains(path) ? _package.Read(path) : null;
    }

    /// <summary>
    /// Decodes base64 data embedded in the document (<c>office:binary-data</c>), counted against
    /// <see cref="ResourceLimits.MaxDecompressedBytes"/>; <c>null</c> when it is not base64.
    /// </summary>
    public byte[]? Decode(string base64)
    {
        var limit = _context.Limits.MaxDecompressedBytes;
        var size = (long)base64.Length * 3 / 4;
        if (_binaryBytes + size > limit)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxDecompressedBytes), limit,
                $"The document's embedded data is larger than ResourceLimits.MaxDecompressedBytes ({limit} bytes).");
        }

        _binaryBytes += size;
        var buffer = new byte[size + 3];
        var clean = new StringBuilder(base64.Length);
        foreach (var c in base64)
        {
            if (!char.IsWhiteSpace(c))
            {
                clean.Append(c);
            }
        }

        return Convert.TryFromBase64String(clean.ToString(), buffer, out var written) ? buffer[..written] : null;
    }

    public void Dispose() => _package.Dispose();
}

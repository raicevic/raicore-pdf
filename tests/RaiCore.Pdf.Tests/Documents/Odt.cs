using System.IO.Compression;
using System.Text;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Documents.Odt;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>Builds small OpenDocument text documents for tests and reads what the converter makes of them.</summary>
internal static class Odt
{
    public const string MediaType = "application/vnd.oasis.opendocument.text";

    public const string Namespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" "
        + "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" "
        + "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" "
        + "xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" "
        + "xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\" xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" "
        + "xmlns:config=\"urn:oasis:names:tc:opendocument:xmlns:config:1.0\" xmlns:loext=\"urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0\"";

    /// <summary>A ZIP package: a stored <c>mimetype</c> first (unless <c>null</c>), then the given parts (text or bytes).</summary>
    public static byte[] Package(string? mimetype, params (string Name, object Content)[] parts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (mimetype is not null)
            {
                using var stream = zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.ASCII.GetBytes(mimetype));
            }

            foreach (var (name, content) in parts)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content is byte[] bytes ? bytes : Encoding.UTF8.GetBytes((string)content));
            }
        }

        return buffer.ToArray();
    }

    /// <summary>A <c>content.xml</c> with the given body (inside <c>office:text</c>) and automatic styles.</summary>
    public static string Content(string body, string automatic = "") =>
        $"<office:document-content {Namespaces}><office:automatic-styles>{automatic}</office:automatic-styles>"
        + $"<office:body><office:text>{body}</office:text></office:body></office:document-content>";

    /// <summary>A <c>styles.xml</c> with common styles, automatic styles (page layouts) and master pages.</summary>
    public static string Styles(string common = "", string automatic = "", string masters = "") =>
        $"<office:document-styles {Namespaces}><office:styles>{common}</office:styles><office:automatic-styles>{automatic}</office:automatic-styles>"
        + $"<office:master-styles>{masters}</office:master-styles></office:document-styles>";

    /// <summary>
    /// A document with the given body XML (prefixes <c>text:</c>, <c>table:</c>, <c>draw:</c>, ...), automatic styles of
    /// the content, and optionally common styles, page layouts, master pages and further parts.
    /// </summary>
    public static byte[] Document(
        string body, string automatic = "", string? common = null, string pageLayouts = "", string masters = "", string? meta = null,
        IEnumerable<(string Name, object Content)>? parts = null, string? settings = null)
    {
        var all = new List<(string, object)> { ("content.xml", Content(body, automatic)) };
        if (common is not null || pageLayouts.Length > 0 || masters.Length > 0)
        {
            all.Add(("styles.xml", Styles(common ?? string.Empty, pageLayouts, masters)));
        }

        if (meta is not null)
        {
            all.Add(("meta.xml", $"<office:document-meta {Namespaces}><office:meta>{meta}</office:meta></office:document-meta>"));
        }

        if (settings is not null)
        {
            all.Add(("settings.xml", $"<office:document-settings {Namespaces}><office:settings>{settings}</office:settings></office:document-settings>"));
        }

        all.AddRange(parts ?? []);
        return Package(MediaType, [.. all]);
    }

    /// <summary>A paragraph.</summary>
    public static string P(string text, string? style = null) =>
        style is null ? $"<text:p>{text}</text:p>" : $"<text:p text:style-name=\"{style}\">{text}</text:p>";

    /// <summary>An automatic or common style of a family with properties (<c>paragraph-properties</c>: attributes).</summary>
    public static string Style(string name, string family, string properties, string attributes = "") =>
        $"<style:style style:name=\"{name}\" style:family=\"{family}\" {attributes}>{properties}</style:style>";

    /// <summary>A paragraph style with paragraph and text properties.</summary>
    public static string ParagraphStyle(string name, string paragraph = "", string text = "", string attributes = "") =>
        Style(name, "paragraph", (paragraph.Length > 0 ? $"<style:paragraph-properties {paragraph}/>" : string.Empty)
            + (text.Length > 0 ? $"<style:text-properties {text}/>" : string.Empty), attributes);

    /// <summary>A text style with text properties.</summary>
    public static string TextStyle(string name, string text, string attributes = "") => Style(name, "text", $"<style:text-properties {text}/>", attributes);

    /// <summary>A page layout with page layout properties, and optional header and footer properties.</summary>
    public static string PageLayout(string name, string properties, string header = "", string footer = "") =>
        $"<style:page-layout style:name=\"{name}\"><style:page-layout-properties {properties}/>"
        + $"<style:header-style>{(header.Length > 0 ? $"<style:header-footer-properties {header}/>" : string.Empty)}</style:header-style>"
        + $"<style:footer-style>{(footer.Length > 0 ? $"<style:header-footer-properties {footer}/>" : string.Empty)}</style:footer-style></style:page-layout>";

    /// <summary>Reads a document into the model.</summary>
    public static WordDocument Read(byte[] odt, ResourceLimits? limits = null, List<DocumentDiagnostic>? diagnostics = null) =>
        OdtConverter.Read(odt, new ConversionContext(limits ?? ResourceLimits.Default, diagnostics is null ? null : diagnostics.Add, CancellationToken.None));

    /// <summary>The paragraphs of a model's body, in order (table cells' too).</summary>
    public static List<WordParagraph> Paragraphs(WordDocument document) => Docx.Paragraphs(document);

    /// <summary>The paragraph of a model with a text.</summary>
    public static WordParagraph Paragraph(WordDocument document, string text) => Paragraphs(document).First(p => Docx.Text(p).Contains(text, StringComparison.Ordinal));

    /// <summary>The run with a text in a model (exactly, else containing it).</summary>
    public static TextRun Run(WordDocument document, string text)
    {
        var runs = Paragraphs(document).SelectMany(p => Runs(p.Inlines)).ToList();
        return runs.FirstOrDefault(r => r.Text == text) ?? runs.First(r => r.Text.Contains(text, StringComparison.Ordinal));
    }

    public static IEnumerable<TextRun> Runs(IEnumerable<WordInline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is TextRun run)
            {
                yield return run;
            }
            else if (inline is HyperlinkInline link)
            {
                foreach (var nested in Runs(link.Inlines))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>A fixture of the Documents/Odt folder.</summary>
    public static byte[] Fixture(string name) => Fixtures.Read(Path.Combine("Documents", "Odt", name));

    /// <summary>Converts with diagnostics collected.</summary>
    public static (PdfDocument Pdf, List<DocumentDiagnostic> Diagnostics) Convert(byte[] odt, OdtConverterOptions? options = null)
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var pdf = OdtToPdf.Convert(odt, new OdtConverterOptions
        {
            Diagnostics = diagnostics.Add,
            Tagged = options?.Tagged ?? false,
            Title = options?.Title,
            HeadingBookmarks = options?.HeadingBookmarks ?? true,
            Fonts = options?.Fonts,
            FallbackFontFamilies = options?.FallbackFontFamilies ?? [],
            Limits = options?.Limits ?? ResourceLimits.Default,
        });
        return (pdf, diagnostics);
    }
}

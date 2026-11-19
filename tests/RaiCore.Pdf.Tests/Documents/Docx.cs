using System.IO.Compression;
using System.Text;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Documents.Docx;
using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Extraction;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>Builds small Word documents for tests and reads what the converter makes of them.</summary>
internal static class Docx
{
    public const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    public const string Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    public const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public const string Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    /// <summary>A ZIP package of the given parts (text or bytes).</summary>
    public static byte[] Package(params (string Name, object Content)[] parts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in parts)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content is byte[] bytes ? bytes : Encoding.UTF8.GetBytes((string)content));
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// A document with the given body XML (prefix <c>w:</c>, <c>r:</c>), optional parts of <c>word/</c> (name, XML or
    /// bytes) and relationships of the main part (id, type, target, external).
    /// </summary>
    public static byte[] Document(
        string body, string? styles = null, string? numbering = null, string? settings = null,
        IEnumerable<(string Name, object Content)>? parts = null, IEnumerable<(string Id, string Type, string Target, bool External)>? relationships = null)
    {
        var rels = new List<(string Id, string Type, string Target, bool External)>(relationships ?? []);
        var all = new List<(string, object)>
        {
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
            ("_rels/.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{R}/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/document.xml", $"<w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\" xmlns:wp=\"{Wp}\" xmlns:a=\"{A}\" xmlns:pic=\"{Pic}\"><w:body>{body}</w:body></w:document>"),
        };
        if (styles is not null)
        {
            all.Add(("word/styles.xml", $"<w:styles xmlns:w=\"{W}\">{styles}</w:styles>"));
            rels.Add(("rStyles", "styles", "styles.xml", false));
        }

        if (numbering is not null)
        {
            all.Add(("word/numbering.xml", $"<w:numbering xmlns:w=\"{W}\">{numbering}</w:numbering>"));
            rels.Add(("rNumbering", "numbering", "numbering.xml", false));
        }

        if (settings is not null)
        {
            all.Add(("word/settings.xml", $"<w:settings xmlns:w=\"{W}\">{settings}</w:settings>"));
            rels.Add(("rSettings", "settings", "settings.xml", false));
        }

        all.AddRange(parts ?? []);
        var xml = new StringBuilder($"<Relationships xmlns=\"{PackageRelationships}\">");
        foreach (var (id, type, target, external) in rels)
        {
            xml.Append($"<Relationship Id=\"{id}\" Type=\"{R}/{type}\" Target=\"{target}\"{(external ? " TargetMode=\"External\"" : string.Empty)}/>");
        }

        all.Add(("word/_rels/document.xml.rels", xml.Append("</Relationships>").ToString()));
        return Package([.. all]);
    }

    /// <summary>A header or footer part's XML.</summary>
    public static string Part(string root, string content) => $"<w:{root} xmlns:w=\"{W}\" xmlns:r=\"{R}\">{content}</w:{root}>";

    /// <summary>A paragraph of plain text.</summary>
    public static string P(string text, string pPr = "", string rPr = "") =>
        $"<w:p>{(pPr.Length > 0 ? $"<w:pPr>{pPr}</w:pPr>" : string.Empty)}<w:r>{(rPr.Length > 0 ? $"<w:rPr>{rPr}</w:rPr>" : string.Empty)}<w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    /// <summary>Reads a document into the model.</summary>
    public static WordDocument Read(byte[] docx, ResourceLimits? limits = null, List<DocumentDiagnostic>? diagnostics = null) =>
        DocxConverter.Read(docx, new ConversionContext(limits ?? ResourceLimits.Default, diagnostics is null ? null : diagnostics.Add, CancellationToken.None));

    /// <summary>All paragraphs of the model's body, in order (table cells' too).</summary>
    public static List<WordParagraph> Paragraphs(WordDocument document) => [.. document.Sections.SelectMany(s => Paragraphs(s.Blocks))];

    public static IEnumerable<WordParagraph> Paragraphs(IEnumerable<WordBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is WordParagraph paragraph)
            {
                yield return paragraph;
            }
            else if (block is WordTable table)
            {
                foreach (var nested in table.Rows.SelectMany(r => r.Cells).SelectMany(c => Paragraphs(c.Blocks)))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>A paragraph's text (runs, also in links), tabs as tab characters.</summary>
    public static string Text(WordParagraph paragraph) => Text(paragraph.Inlines);

    private static string Text(IEnumerable<WordInline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        TextRun run => run.Text,
        TabInline => "\t",
        BreakInline => "\n",
        HyperlinkInline link => Text(link.Inlines),
        FieldInline field => field.CachedResult ?? string.Empty,
        _ => string.Empty,
    }));

    /// <summary>The text of each page of a PDF.</summary>
    public static string[] Pages(byte[] pdf) => [.. new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)];

    /// <summary>The text of all pages.</summary>
    public static string AllText(byte[] pdf) => string.Join("\n", Pages(pdf));

    /// <summary>Converts with diagnostics collected.</summary>
    public static (PdfDocument Pdf, List<DocumentDiagnostic> Diagnostics) Convert(byte[] docx, DocxConverterOptions? options = null)
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var pdf = DocxToPdf.Convert(docx, new DocxConverterOptions
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

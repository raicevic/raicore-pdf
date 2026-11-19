using System.Text;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Documents.Rtf;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>Builds small RTF documents for tests and reads what the converter makes of them.</summary>
internal static class Rtf
{
    /// <summary>The font table of <see cref="Document"/>: Arial, Times New Roman, Courier New, Symbol, a Cyrillic font, a Japanese font.</summary>
    public const string Fonts = @"{\fonttbl{\f0\fswiss\fcharset0 Arial;}{\f1\froman\fcharset0 Times New Roman;}{\f2\fmodern Courier New;}{\f3\fcharset2 Symbol;}{\f4\fcharset204 Arial Cyr;}{\f5\fcharset128 MS Gothic;}}";

    /// <summary>The color table of <see cref="Document"/>: auto, black, red, green, blue, yellow.</summary>
    public const string Colors = @"{\colortbl;\red0\green0\blue0;\red255\green0\blue0;\red0\green128\blue0;\red0\green0\blue255;\red255\green255\blue0;}";

    /// <summary>An RTF document with the font and color tables, optional header destinations, and the body.</summary>
    public static byte[] Document(string body, string header = "") =>
        Encoding.Latin1.GetBytes(@"{\rtf1\ansi\ansicpg1252\deff0" + Fonts + Colors + header + "\n" + body + "}");

    /// <summary>Reads a document into the model.</summary>
    public static WordDocument Read(byte[] rtf, ResourceLimits? limits = null, List<DocumentDiagnostic>? diagnostics = null) =>
        RtfConverter.Read(rtf, new ConversionContext(limits ?? ResourceLimits.Default, diagnostics is null ? null : diagnostics.Add, CancellationToken.None));

    /// <summary>Reads the body of a <see cref="Document"/> into the model.</summary>
    public static WordDocument ReadBody(string body, string header = "", List<DocumentDiagnostic>? diagnostics = null) =>
        Read(Document(body, header), diagnostics: diagnostics);

    /// <summary>All paragraphs of the body, in order (table cells' too).</summary>
    public static List<WordParagraph> Paragraphs(WordDocument document) => Docx.Paragraphs(document);

    /// <summary>A paragraph's text.</summary>
    public static string Text(WordParagraph paragraph) => Docx.Text(paragraph);

    /// <summary>The runs of a paragraph (also in links).</summary>
    public static List<TextRun> Runs(WordParagraph paragraph) => [.. Runs(paragraph.Inlines)];

    private static IEnumerable<TextRun> Runs(IEnumerable<WordInline> inlines)
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

    /// <summary>The run whose text contains <paramref name="text"/>.</summary>
    public static TextRun Run(WordDocument document, string text) => Paragraphs(document).SelectMany(Runs).First(r => r.Text.Contains(text, StringComparison.Ordinal));

    /// <summary>The paragraph whose text contains <paramref name="text"/>.</summary>
    public static WordParagraph Paragraph(WordDocument document, string text) => Paragraphs(document).First(p => Text(p).Contains(text, StringComparison.Ordinal));

    /// <summary>The tables of the body (not nested ones).</summary>
    public static List<WordTable> Tables(WordDocument document) => [.. document.Sections.SelectMany(s => s.Blocks).OfType<WordTable>()];

    /// <summary>Converts with diagnostics collected.</summary>
    public static (PdfDocument Pdf, List<DocumentDiagnostic> Diagnostics) Convert(byte[] rtf, ResourceLimits? limits = null)
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var pdf = RtfToPdf.Convert(rtf, new RtfConverterOptions { Diagnostics = diagnostics.Add, Limits = limits ?? ResourceLimits.Default });
        return (pdf, diagnostics);
    }
}

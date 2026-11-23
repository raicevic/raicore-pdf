using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Documents.Doc;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>Reads the Word 97-2003 samples and what the converter makes of them.</summary>
internal static class Doc
{
    /// <summary>A sample of Fixtures/Documents/Doc.</summary>
    public static byte[] Sample(string name) => Fixtures.Read(Path.Combine("Documents", "Doc", name));

    /// <summary>Reads a document into the model.</summary>
    public static WordDocument Read(byte[] doc, ResourceLimits? limits = null, List<DocumentDiagnostic>? diagnostics = null) =>
        DocConverter.Read(doc, new ConversionContext(limits ?? ResourceLimits.Default, diagnostics is null ? null : diagnostics.Add, CancellationToken.None));

    /// <summary>Reads a sample into the model.</summary>
    public static WordDocument ReadSample(string name, List<DocumentDiagnostic>? diagnostics = null) => Read(Sample(name), diagnostics: diagnostics);

    /// <summary>All paragraphs of the body, in order (table cells' too).</summary>
    public static List<WordParagraph> Paragraphs(WordDocument document) => Docx.Paragraphs(document);

    /// <summary>All paragraphs of blocks, in order (table cells' and text boxes' too).</summary>
    public static IEnumerable<WordParagraph> AllParagraphs(IEnumerable<WordBlock> blocks)
    {
        foreach (var paragraph in Docx.Paragraphs(blocks))
        {
            yield return paragraph;
            foreach (var anchored in paragraph.Inlines.OfType<AnchoredBlocks>())
            {
                foreach (var nested in AllParagraphs(anchored.Blocks))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>A paragraph's text.</summary>
    public static string Text(WordParagraph paragraph) => Docx.Text(paragraph);

    /// <summary>The text of blocks, a paragraph a line.</summary>
    public static string Text(IEnumerable<WordBlock> blocks) => string.Join("\n", Docx.Paragraphs(blocks).Select(Text));

    /// <summary>The runs of a paragraph (also in links).</summary>
    public static List<TextRun> Runs(WordParagraph paragraph) => Rtf.Runs(paragraph);

    /// <summary>The run whose text contains <paramref name="text"/>.</summary>
    public static TextRun Run(WordDocument document, string text) => Paragraphs(document).SelectMany(Runs).First(r => r.Text.Contains(text, StringComparison.Ordinal));

    /// <summary>The paragraph whose text contains <paramref name="text"/>.</summary>
    public static WordParagraph Paragraph(WordDocument document, string text) => Paragraphs(document).First(p => Text(p).Contains(text, StringComparison.Ordinal));

    /// <summary>The inlines of a paragraph, those in links too.</summary>
    public static IEnumerable<WordInline> Inlines(IEnumerable<WordInline> inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is HyperlinkInline link)
            {
                foreach (var nested in Inlines(link.Inlines))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>Converts with diagnostics collected.</summary>
    public static (PdfDocument Pdf, List<DocumentDiagnostic> Diagnostics) Convert(byte[] doc, ResourceLimits? limits = null)
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var pdf = DocToPdf.Convert(doc, new DocConverterOptions { Diagnostics = diagnostics.Add, Limits = limits ?? ResourceLimits.Default });
        return (pdf, diagnostics);
    }
}

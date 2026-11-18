using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Extraction.Content;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Tests.Reading;

namespace RaiCore.Pdf.Tests.Extraction;

/// <summary>Records what the content interpreter reports.</summary>
internal sealed class RecordingSink : ContentSink
{
    public List<TextGlyph> Glyphs { get; } = [];

    public List<ImagePlacement> Images { get; } = [];

    /// <summary>Text objects, marked content and drawing in order: <c>BT</c>, <c>ET</c>, <c>glyph A</c>, <c>image</c>, <c>begin Span ActualText=fi</c>, <c>end</c>.</summary>
    public List<string> Events { get; } = [];

    public ExtractionDiagnostics Diagnostics { get; set; } = new();

    /// <summary>The text of all glyphs, concatenated.</summary>
    public string Text => string.Concat(Glyphs.Select(g => g.Text));

    public override void BeginText() => Events.Add("BT");

    public override void EndText() => Events.Add("ET");

    public override void Glyph(in TextGlyph glyph)
    {
        Glyphs.Add(glyph);
        Events.Add("glyph " + glyph.Text);
    }

    public override void Image(in ImagePlacement image)
    {
        Images.Add(image);
        Events.Add("image");
    }

    public override void BeginMarkedContent(string tag, PdfDictionary? properties)
    {
        var details = properties is null
            ? ""
            : " " + string.Join(' ', properties.Entries.Select(e => $"{e.Key.Value}={Describe(e.Value)}"));
        Events.Add($"begin {tag}{details}");
    }

    public override void EndMarkedContent() => Events.Add("end");

    private static string Describe(PdfObject value) => value switch
    {
        PdfString text => RaiCore.Pdf.Reading.PdfText.Decode(text.Bytes),
        PdfInteger number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PdfName name => "/" + name.Value,
        _ => value.GetType().Name,
    };
}

internal static class Interpret
{
    /// <summary>Runs one page of a PDF through the interpreter.</summary>
    public static RecordingSink Page(byte[] pdf, int page = 0, string? password = null, int? maxOperations = null)
    {
        var reader = PdfReader.Open(pdf, password);
        var sink = new RecordingSink();
        var interpreter = new ContentInterpreter(reader, sink) { MaxOperations = maxOperations ?? ContentInterpreter.MaxOperationsPerPage };
        interpreter.Run(reader.Pages[page]);
        sink.Diagnostics = interpreter.Diagnostics;
        return sink;
    }

    /// <summary>
    /// A one-page PDF (600 × 800 points unless <paramref name="pageEntries"/> says otherwise) with the content and
    /// resources given. Objects 1 to 4 are the catalog, the page tree, the page and the content; <paramref name="extra"/>
    /// objects are numbered from 5.
    /// </summary>
    public static byte[] OnePage(string content, string resources, string pageEntries = "/MediaBox [0 0 600 800]", params string[] extra) =>
        TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R {pageEntries} /Resources {resources} /Contents 4 0 R >>",
            TestPdf.Stream(content),
            .. extra,
        ]);

    /// <summary>Resources with one font, <c>/F1</c>: the font dictionary given.</summary>
    public static string Font(string font) => $"<< /Font << /F1 {font} >> >>";

    /// <summary>Helvetica in WinAnsiEncoding, with widths only for A (600) and B (500) and a space (250).</summary>
    public static readonly string Helvetica =
        $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 66 /Widths [250 {string.Concat(Enumerable.Repeat("0 ", 32))}600 500] >>";
}

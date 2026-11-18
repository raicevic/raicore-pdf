using System.Text.Json;
using RaiCore.Pdf.Extraction;
using static RaiCore.Pdf.Tests.Extraction.Interpret;

namespace RaiCore.Pdf.Tests.Extraction;

/// <summary>
/// Pages written by other producers (reportlab, PyMuPDF, fpdf2; see Fixtures/Extraction/make-fixtures.py) against what
/// MuPDF, an independent reader, finds on them: every character with its origin in display space and its size.
/// </summary>
public class OtherProducersTests
{
    /// <summary>How far an origin may be from MuPDF's, in points (MuPDF rounds glyph widths of the standard fonts).</summary>
    private const double Tolerance = 0.25;

    public static TheoryData<string, int> Pages()
    {
        var data = new TheoryData<string, int>();
        foreach (var (file, pages) in Expected())
        {
            for (var page = 0; page < pages.Count; page++)
            {
                data.Add(file, page);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Glyphs_match_MuPDF(string file, int page)
    {
        var expected = Expected()[file][page];
        var reader = PdfReader.Open(Fixtures.Read("Extraction/" + file));
        Assert.Equal(expected.Width, reader.Pages[page].Width, 3);
        Assert.Equal(expected.Height, reader.Pages[page].Height, 3);

        var sink = Page(Fixtures.Read("Extraction/" + file), page);
        var glyphs = sink.Glyphs.Where(g => g.Text.Length > 0).ToList();
        Assert.Equal(string.Concat(expected.Chars.Select(c => c.Text)), string.Concat(glyphs.Select(g => g.Text)));
        for (var i = 0; i < glyphs.Count; i++)
        {
            var (text, x, y, size) = expected.Chars[i];
            var glyph = glyphs[i];
            Assert.True(
                Math.Abs(glyph.Origin.X - x) <= Tolerance && Math.Abs(glyph.Origin.Y - y) <= Tolerance && SameSize(glyph.FontSize, size),
                $"'{text}' (#{i}): MuPDF has ({x}, {y}) at {size} pt, RaiCore ({glyph.Origin.X:F3}, {glyph.Origin.Y:F3}) at {glyph.FontSize:F3} pt.");
            Assert.False(glyph.IsGuess);
        }

        Assert.Empty(sink.Diagnostics.Messages);
    }

    public static TheoryData<string> Documents() => [.. Expected().Keys, "raicore-standard", "raicore-html"];

    /// <summary>
    /// The words of each document (in reading order) are those <c>pdftotext</c> (poppler) finds, as a multiset: the
    /// same text cut into the same words, wherever the two readers put them. Runs where pdftotext is installed (CI).
    /// </summary>
    [Theory]
    [MemberData(nameof(Documents))]
    public void Words_match_pdftotext(string document)
    {
        var pdf = document switch
        {
            "raicore-standard" => RaiCoreStandard(),
            "raicore-html" => RaiCore.Pdf.Html.HtmlToPdf.ConvertToBytes(
                "<h1>Invoice 2026-001</h1><p>Thank you for your order. Payment is due in <b>30 days</b>.</p>"
                + "<table><tr><th>Item</th><th>Qty</th><th>Price</th></tr><tr><td>Widget</td><td>2</td><td>10.00</td></tr></table>"),
            _ => Fixtures.Read("Extraction/" + document),
        };

        // Poppler breaks diagonal text apart, so it is left out (-nodiag): the line turned by 30 degrees in
        // reportlab-truetype.pdf is compared with MuPDF instead (Glyphs_match_MuPDF).
        var (exitCode, output) = ExternalTools.RunOnPdf("pdftotext", pdf, "-nodiag", "-enc", "UTF-8", "{pdf}", "-");
        Assert.Equal(0, exitCode);
        var ours = new TextExtractor(PdfReader.Open(pdf)).ExtractText();
        if (document == "reportlab-truetype.pdf")
        {
            ours = ours.Replace("Turned 30 degrees", string.Empty, StringComparison.Ordinal);
        }

        var (expected, actual) = (Words(output), Words(ours));
        Assert.True(
            expected.SequenceEqual(actual),
            $"Only pdftotext: {string.Join(' ', Except(expected, actual))}; only RaiCore: {string.Join(' ', Except(actual, expected))}");
    }

    private static byte[] RaiCoreStandard()
    {
        var document = new PdfDocument();
        var canvas = document.AddPage(PdfPageSize.A4).Canvas;
        canvas.DrawText("Standard fonts: Gr\u00FC\u00DFe \u2013 1,50 \u20AC", RaiCore.Pdf.Fonts.StandardFont.Helvetica, 12, 72, 700);
        canvas.DrawText("Second line in Times", RaiCore.Pdf.Fonts.StandardFont.TimesRoman, 12, 72, 680);
        return document.ToArray();
    }

    /// <summary>The words of <paramref name="a"/> that <paramref name="b"/> lacks, counting repeats.</summary>
    private static List<string> Except(List<string> a, List<string> b)
    {
        var rest = b.GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<string>();
        foreach (var word in a)
        {
            if (rest.TryGetValue(word, out var count) && count > 0)
            {
                rest[word] = count - 1;
            }
            else
            {
                result.Add(word);
            }
        }

        return result;
    }

    private static List<string> Words(string text) =>
        [.. text.Normalize(System.Text.NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Whether the sizes agree. RaiCore's is the em height on the page; MuPDF's is the geometric mean of the horizontal
    /// and vertical scales, smaller for horizontally scaled text (Tz &lt; 100): the square root of the scaling smaller.
    /// </summary>
    private static bool SameSize(double raiCore, double muPdf) =>
        Math.Abs(raiCore - muPdf) <= 0.01 || Math.Abs((raiCore * Math.Sqrt(HorizontalScaling)) - muPdf) <= 0.01;

    /// <summary>The horizontal scaling of the one horizontally scaled line in the fixtures (reportlab-truetype.pdf).</summary>
    private const double HorizontalScaling = 0.8;

    private static Dictionary<string, List<ExpectedPage>> Expected()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Fixtures.Path("Extraction/expected.json")));
        return json.RootElement.EnumerateObject().ToDictionary(
            file => file.Name,
            file => file.Value.EnumerateArray().Select(page => new ExpectedPage(
                page[0].GetDouble(),
                page[1].GetDouble(),
                [.. page[2].EnumerateArray().Select(c => (c[0].GetString()!, c[1].GetDouble(), c[2].GetDouble(), c[3].GetDouble()))])).ToList());
    }

    private sealed record ExpectedPage(double Width, double Height, List<(string Text, double X, double Y, double Size)> Chars);
}

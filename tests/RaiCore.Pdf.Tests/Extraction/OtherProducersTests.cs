using System.Text.Json;
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

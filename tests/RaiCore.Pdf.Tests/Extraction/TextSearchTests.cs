using System.Text.RegularExpressions;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using static RaiCore.Pdf.Tests.Extraction.Interpret;

namespace RaiCore.Pdf.Tests.Extraction;

public class TextSearchTests
{
    /// <summary>Glyph boxes span the font's ascent and descent; this is the most they may miss a drawn position by.</summary>
    private const double Tolerance = 0.5;

    private static TextExtractor Extractor(string content, string pageEntries = "/MediaBox [0 0 600 800]", string? resources = null) =>
        new(PdfReader.Open(OnePage(content, resources ?? Font(Helvetica), pageEntries)));

    private static void AssertNear(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) <= Tolerance, $"Expected {expected}, got {actual}.");

    /// <summary>Asserts that a rectangle starts at <paramref name="x"/>, is <paramref name="width"/> wide and holds the baseline at <paramref name="baseline"/> for text of <paramref name="size"/>.</summary>
    private static void AssertText(PdfRectangle rectangle, double x, double width, double baseline, double size)
    {
        AssertNear(x, rectangle.X);
        AssertNear(width, rectangle.Width);
        Assert.InRange(baseline - rectangle.Y, 0.1 * size, 0.35 * size); // The descent.
        Assert.InRange(rectangle.Top - baseline, 0.6 * size, 1.0 * size); // The ascent.
    }

    [Fact]
    public void A_placeholder_is_found_where_it_was_drawn()
    {
        var document = new PdfDocument();
        var font = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
        var page = document.AddPage(PdfPageSize.A4);
        page.Canvas.DrawText("Dear {{name}}, welcome", font, 12, 72, 700);
        page.Canvas.DrawText("Signed: {{signature}}", font, 9, 300, 120.5);

        var extractor = new TextExtractor(PdfReader.Open(document.ToArray()));
        var name = Assert.Single(extractor.Find("{{name}}"));
        var signature = Assert.Single(extractor.Find("{{signature}}"));

        Assert.Equal(0, name.PageIndex);
        Assert.Equal("{{name}}", name.Text);
        AssertText(Assert.Single(name.Rectangles), 72 + font.MeasureText("Dear ", 12), font.MeasureText("{{name}}", 12), 700, 12);
        Assert.Equal(name.Rectangles[0], name.Bounds);
        AssertText(signature.Bounds, 300 + font.MeasureText("Signed: ", 9), font.MeasureText("{{signature}}", 9), 120.5, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Rectangles_are_in_user_space_on_rotated_and_cropped_pages(int rotate)
    {
        // A: 600, B: 500, space: 250 per 1000 em. "BA" starts after "AB " = 13.5 points at 10 points.
        var extractor = Extractor("BT /F1 10 Tf 100 700 Td (AB BA) Tj ET", $"/MediaBox [0 0 600 800] /CropBox [50 100 550 750] /Rotate {rotate}");

        var match = Assert.Single(extractor.Find("BA"));

        AssertText(Assert.Single(match.Rectangles), 113.5, 11, 700, 10);
    }

    [Fact]
    public void A_match_that_wraps_has_a_rectangle_per_line()
    {
        var extractor = Extractor("BT /F1 10 Tf 12 TL 100 700 Td (AA AB) Tj T* (BA BB) Tj ET");

        var match = Assert.Single(extractor.Find("AB BA"));

        Assert.Equal("AB\nBA", match.Text);
        Assert.Equal(2, match.Rectangles.Count);
        AssertText(match.Rectangles[0], 114.5, 11, 700, 10);
        AssertText(match.Rectangles[1], 100, 11, 688, 10);
        AssertNear(100, match.Bounds.X);
        AssertNear(25.5, match.Bounds.Width);
        Assert.Equal(match.Rectangles[1].Y, match.Bounds.Y);
        Assert.Equal(match.Rectangles[0].Top, match.Bounds.Top);

        // A later line that reaches further right widens the bounds.
        var longer = Assert.Single(Extractor("BT /F1 10 Tf 12 TL 100 700 Td (AA AB) Tj T* (BA BBBB) Tj ET").Find("AB BA BBBB"));
        AssertNear(33.5, longer.Bounds.Width);
    }

    [Fact]
    public void Text_matches_across_pieces_and_fonts_of_a_line()
    {
        var resources = $"<< /Font << /F1 {Helvetica} /F2 {Helvetica.Replace("/Helvetica", "/Helvetica-Bold", StringComparison.Ordinal)} >> >>";
        var extractor = Extractor("BT /F1 10 Tf 100 700 Td (AB) Tj /F2 10 Tf (BA) Tj ET BT /F1 10 Tf 122 700 Td ( AA) Tj ET", resources: resources);

        var match = Assert.Single(extractor.Find("ABBA AA"));

        AssertText(Assert.Single(match.Rectangles), 100, 36.5, 700, 10);
    }

    [Fact]
    public void Html_placeholders_are_found_across_elements()
    {
        var pdf = HtmlToPdf.ConvertToBytes("<p>Dear <b>{{first}}</b> <i>{{last}}</i>,</p><p style='break-before: page'>Total: {{total}}</p>");
        var extractor = new TextExtractor(PdfReader.Open(pdf));

        var name = Assert.Single(extractor.Find("{{first}} {{last}}"));
        var total = Assert.Single(extractor.Find("{{total}}"));

        Assert.Equal(0, name.PageIndex);
        Assert.Equal(1, total.PageIndex);
        Assert.Single(name.Rectangles);
        Assert.True(name.Bounds.X > 56 && name.Bounds.Top < 842 - 56, $"Inside the page margins: {name.Bounds}");
        Assert.True(total.Bounds.X > 56 + 20, $"After the label: {total.Bounds}");
    }

    [Fact]
    public void Spaces_between_table_cells_have_no_place()
    {
        var extractor = Extractor("BT /F1 10 Tf 100 700 Td (AB) Tj 30 0 Td (BA) Tj ET");

        var match = Assert.Single(extractor.Find(new Regex("AB ")));

        AssertText(Assert.Single(match.Rectangles), 100, 11, 700, 10);
    }

    [Fact]
    public void Case_is_matched_unless_ignored()
    {
        var extractor = Extractor("BT /F1 10 Tf 100 700 Td (AB BA ab) Tj ET");

        Assert.Equal(["ab"], extractor.Find("ab").Select(m => m.Text));
        Assert.Equal(["AB", "ab"], extractor.Find("ab", ignoreCase: true).Select(m => m.Text));
        Assert.Empty(extractor.FindOnPage(0, "Ab"));
        Assert.Equal(2, extractor.FindOnPage(0, "Ab", ignoreCase: true).Count);
    }

    [Fact]
    public void Plain_text_is_not_a_pattern_and_its_whitespace_matches_any_whitespace()
    {
        var extractor = Extractor("BT /F1 10 Tf 12 TL 100 700 Td (A.B A*B) Tj T* (AB) Tj ET");

        Assert.Equal(["A.B"], extractor.Find("A.B").Select(m => m.Text));
        Assert.Equal(["A*B"], extractor.Find("A*B").Select(m => m.Text));
        Assert.Equal(["A*B\nAB"], extractor.Find("  A*B \t AB ").Select(m => m.Text));
    }

    [Fact]
    public void Regular_expressions_are_matched_against_the_page_text()
    {
        var extractor = Extractor("BT /F1 10 Tf 12 TL 100 700 Td (AB BBA) Tj T* (BAB) Tj ET");

        Assert.Equal(["AB", "BBA", "BAB"], extractor.Find(new Regex(@"\b[AB]+\b")).Select(m => m.Text));
        Assert.Equal(["AB", "BAB"], extractor.FindOnPage(0, new Regex("^[AB]+", RegexOptions.Multiline)).Select(m => m.Text));

        // Empty matches and matches of only whitespace have no place on the page.
        Assert.Equal(["A", "A", "A"], extractor.Find(new Regex("A*")).Select(m => m.Text));
        Assert.Empty(extractor.Find(new Regex(@"\s+")));
    }

    [Fact]
    public void Every_page_is_searched_in_order()
    {
        var document = new PdfDocument();
        foreach (var text in new[] { "one two", "", "two one one" })
        {
            var page = document.AddPage(PdfPageSize.A4);
            if (text.Length > 0)
            {
                page.Canvas.DrawText(text, StandardFont.Helvetica, 12, 72, 700);
            }
        }

        var extractor = new TextExtractor(PdfReader.Open(document.ToArray()));

        Assert.Equal([0, 2, 2], extractor.Find("one").Select(m => m.PageIndex));
        Assert.Equal([0, 2], extractor.Find(new Regex("two")).Select(m => m.PageIndex));
        Assert.Empty(extractor.FindOnPage(1, "one"));
        Assert.Equal(2, extractor.FindOnPage(2, new Regex("one")).Count);
        Assert.Equal(extractor.Find("one").First().Bounds, extractor.FindOnPage(0, "one")[0].Bounds);
    }

    [Fact]
    public void Ligatures_match_their_letters()
    {
        var font = "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding << /Differences [65 /fi] >> >>";
        var extractor = Extractor("BT /F1 10 Tf 100 700 Td (A) Tj ET", resources: Font(font));

        Assert.Single(extractor.Find("fi"));
        Assert.Single(extractor.Find("\uFB01"));
        Assert.Single(extractor.Find(new Regex("i")));
    }

    [Fact]
    public void Actual_text_is_found_where_its_glyphs_are()
    {
        var match = Assert.Single(Extractor("BT /F1 10 Tf 100 700 Td (BA) Tj /Span << /ActualText (fi) >> BDC (AB) Tj EMC ET").Find("fi"));

        AssertText(Assert.Single(match.Rectangles), 111, 11, 700, 10);
    }

    [Fact]
    public void Right_to_left_text_is_found_in_logical_order()
    {
        // Drawn as displayed: "Web" then bet, alef (every glyph 5 points wide); read as alef, bet, "Web".
        var pdf = OnePage("BT /F1 10 Tf 100 700 Td (Web BA) Tj ET", Font(TextExtractorTests.HebrewFont), extra: [TextExtractorTests.HebrewToUnicode]);
        var extractor = new TextExtractor(PdfReader.Open(pdf));

        AssertText(Assert.Single(Assert.Single(extractor.Find("\u05D0\u05D1")).Rectangles), 120, 10, 700, 10);
        AssertText(Assert.Single(Assert.Single(extractor.Find("\u05D0\u05D1 Web")).Rectangles), 100, 30, 700, 10);
        Assert.Empty(extractor.Find("\u05D1\u05D0"));
    }

    [Fact]
    public void Clipped_and_invisible_text_follow_the_options()
    {
        var content = "BT /F1 10 Tf 100 700 Td (AB) Tj ET BT 3 Tr /F1 10 Tf 100 600 Td (BA) Tj ET q 0 0 10 10 re W n BT /F1 10 Tf 100 500 Td (AA) Tj ET Q";
        var pdf = OnePage(content, Font(Helvetica));

        Assert.Single(new TextExtractor(PdfReader.Open(pdf)).Find("BA"));
        Assert.Empty(new TextExtractor(PdfReader.Open(pdf), new TextExtractionOptions { IncludeInvisibleText = false }).Find("BA"));
        Assert.Empty(new TextExtractor(PdfReader.Open(pdf)).Find("AA"));
    }

    [Fact]
    public void Arguments_are_checked()
    {
        var extractor = Extractor("BT /F1 10 Tf 100 700 Td (AB) Tj ET");

        Assert.Throws<ArgumentNullException>(() => extractor.Find((string)null!));
        Assert.Throws<ArgumentNullException>(() => extractor.Find((Regex)null!));
        Assert.Throws<ArgumentException>(() => extractor.Find(""));
        Assert.Throws<ArgumentException>(() => extractor.Find(" \n "));
        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.FindOnPage(1, "AB"));
        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.FindOnPage(-1, new Regex("AB")));
        Assert.Throws<ArgumentNullException>(() => extractor.FindOnPage(0, (string)null!));
    }

    [Fact]
    public void A_match_describes_itself()
    {
        var match = Assert.Single(Extractor("BT /F1 10 Tf 100 700 Td (AB) Tj ET").Find("AB"));

        Assert.StartsWith("\"AB\" on page 1 at PdfRectangle { X = 100, Y = ", match.ToString(), StringComparison.Ordinal);
    }
}

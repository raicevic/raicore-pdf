using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Extraction.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Tests.Reading;
using static RaiCore.Pdf.Tests.Extraction.Interpret;

namespace RaiCore.Pdf.Tests.Extraction;

public class TextExtractorTests
{
    private static PageText Extract(byte[] pdf, TextExtractionOptions? options = null, int page = 0) =>
        new TextExtractor(PdfReader.Open(pdf), options).ExtractPage(page);

    private static string Text(string content, string? resources = null, string pageEntries = "/MediaBox [0 0 600 800]", TextExtractionOptions? options = null, params string[] extra) =>
        Extract(OnePage(content, resources ?? Font(Helvetica), pageEntries, extra), options).Text;

    private static byte[] Pages(params string[] texts)
    {
        var document = new PdfDocument();
        foreach (var text in texts)
        {
            var page = document.AddPage(PdfPageSize.A4);
            if (text.Length > 0)
            {
                page.Canvas.DrawText(text, StandardFont.Helvetica, 12, 72, 700);
            }
        }

        return document.ToArray();
    }

    [Fact]
    public void Pages_are_extracted_one_at_a_time_in_any_order()
    {
        var extractor = new TextExtractor(PdfReader.Open(Pages("one", "two", "three")));

        Assert.Equal(["one", "two", "three"], extractor.ExtractPages().Select(p => p.Text));
        Assert.Equal([2, 0], extractor.ExtractPages([2, 0]).Select(p => p.PageIndex));
        Assert.Equal("two", extractor.ExtractPage(1).ToString());
        Assert.Equal("one\ftwo\fthree", extractor.ExtractText());
        Assert.Equal("one\n---\ntwo\n---\nthree", new TextExtractor(extractor.Reader, new TextExtractionOptions { PageSeparator = "\n---\n" }).ExtractText());
    }

    [Fact]
    public void Page_indexes_are_checked()
    {
        var extractor = new TextExtractor(PdfReader.Open(Pages("one")));

        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.ExtractPage(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.ExtractPage(1));
        Assert.Throws<ArgumentNullException>(() => extractor.ExtractPages(null!));
        var lazy = extractor.ExtractPages([0, 5]);
        Assert.Throws<ArgumentOutOfRangeException>(() => lazy.ToList());
        Assert.Throws<ArgumentNullException>(() => new TextExtractor(null!));
    }

    [Fact]
    public void Pages_without_text_say_so()
    {
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).Canvas.DrawImage(PdfImage.Load(Fixtures.Path("Images/gray.jpg")), 50, 50, 300, 400);
        document.AddPage(PdfPageSize.A4);
        document.AddPage(PdfPageSize.A4).Canvas.DrawText(" ", StandardFont.Helvetica, 12, 72, 700);
        document.AddPage(PdfPageSize.A4).Canvas.DrawText("Words", StandardFont.Helvetica, 12, 72, 700);
        var pages = new TextExtractor(PdfReader.Open(document.ToArray())).ExtractPages().ToList();

        Assert.Equal([false, false, false, true], pages.Select(p => p.HasText));
        Assert.Equal("", pages[0].Text);
    }

    [Fact]
    public void Diagnostics_belong_to_the_page_that_has_the_problem()
    {
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 600 800] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << >> /Contents 5 0 R >>",
            $"<< /Type /Page /Parent 2 0 R /Resources {Font(Helvetica)} /Contents 6 0 R >>",
            TestPdf.Stream("BT /F1 12 Tf 10 10 Td (A) Tj ET"),
            TestPdf.Stream("BT /F1 12 Tf 10 10 Td (B) Tj ET"),
        ]);
        var pages = new TextExtractor(PdfReader.Open(pdf)).ExtractPages().ToList();

        Assert.Contains(pages[0].Diagnostics, d => d.Contains("/F1 is missing", StringComparison.Ordinal));
        Assert.Empty(pages[1].Diagnostics);
        Assert.Equal(["A", "B"], pages.Select(p => p.Text));
    }

    [Fact]
    public void Invisible_text_is_included_unless_asked_otherwise()
    {
        var content = "BT /F1 12 Tf 3 Tr 100 700 Td (AB) Tj 0 Tr 0 -20 Td (BA) Tj ET";

        Assert.Equal("AB\nBA", Text(content));
        Assert.Equal("BA", Text(content, options: new TextExtractionOptions { IncludeInvisibleText = false }));
    }

    [Fact]
    public void Clipped_text_and_text_outside_the_page_are_left_out()
    {
        var form = TestPdf.Stream("BT /F1 12 Tf 10 10 Td (A) Tj 200 0 Td (B) Tj ET", $"/Subtype /Form /BBox [0 0 100 100] /Resources {Font(Helvetica)}");
        var content = "q 0 0 0 0 re W n BT /F1 12 Tf 100 700 Td (BBB) Tj ET Q "
            + "q 90 690 50 30 re W n BT /F1 12 Tf 100 700 Td (AA) Tj 100 0 Td (BB) Tj ET Q "
            + "BT /F1 12 Tf -500 650 Td (B) Tj 500 -1000 Td (B) Tj ET "
            + "q 1 0 0 1 100 400 cm /Fm1 Do Q";
        var text = Text(content, $"<< /Font << /F1 {Helvetica} >> /XObject << /Fm1 5 0 R >> >>", extra: [form]);

        Assert.Equal("AA\n\nA", text);
    }

    [Fact]
    public void Word_spaces_come_from_space_glyphs_and_from_gaps()
    {
        Assert.Equal("AB AB AB", Text("BT /F1 10 Tf 100 700 Td [(AB) -300 (AB) -50 ( ) -400 (AB)] TJ ET"));
        Assert.Equal("ABAB", Text("BT /F1 10 Tf 100 700 Td [(AB) -100 (AB)] TJ ET"));

        // A space glyph whose width the TJ array takes back still separates the words.
        Assert.Equal("A B", Text("BT /F1 10 Tf 100 700 Td [(A) ( ) 250 (B)] TJ ET"));
    }

    [Fact]
    public void Text_drawn_out_of_order_on_a_line_is_read_left_to_right()
    {
        Assert.Equal("A B", Text("BT /F1 10 Tf 200 700 Td (B) Tj -100 0 Td (A) Tj ET"));
    }

    [Fact]
    public void Label_and_value_rows_are_read_row_by_row()
    {
        // Long values after short labels: a gap like a column's, but the labels are no column of text.
        var value = string.Concat(Enumerable.Repeat("BA", 12));
        var rows = string.Concat(Enumerable.Range(0, 4).Select(i => $"BT /F1 10 Tf 50 {700 - (i * 14)} Td (AB) Tj 100 0 Td ({value}) Tj ET "));

        Assert.Equal(string.Join('\n', Enumerable.Repeat("AB " + value, 4)), Text(rows));
    }

    [Fact]
    public void Lines_and_paragraphs_are_separated()
    {
        var text = Text("BT /F1 10 Tf 12 TL 100 700 Td (AB) Tj T* (BA) Tj 0 -40 Td (A) Tj ET");

        Assert.Equal("AB\nBA\n\nA", text);
    }

    [Fact]
    public void A_large_segment_beside_two_small_lines_does_not_merge_them()
    {
        // "BB" in a large font spans the height of both small lines to its left.
        var text = Text("BT /F1 8 Tf 100 700 Td (AA) Tj 0 -10 Td (AB) Tj ET BT /F1 30 Tf 200 690 Td (BB) Tj ET");

        Assert.Equal("AA BB\nAB", text);
    }

    [Fact]
    public void Superscripts_and_subscripts_stay_with_their_word()
    {
        Assert.Equal("AB BA", Text("BT /F1 10 Tf 100 700 Td (A) Tj /F1 6 Tf 4 Ts (B) Tj /F1 10 Tf 0 Ts ( BA) Tj ET"));
    }

    [Fact]
    public void Text_drawn_twice_for_fake_bold_is_read_once()
    {
        Assert.Equal("AB", Text("BT /F1 10 Tf 100 700 Td (AB) Tj ET BT /F1 10 Tf 100.3 700 Td (AB) Tj ET"));
    }

    [Fact]
    public void Ligatures_and_presentation_forms_become_letters()
    {
        var font = "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding << /Differences [65 /fi /f_f_l /uniFEFB /uni00A0] >> >>";

        Assert.Equal("fi ffl \u0644\u0627 fi", Text("BT /F1 10 Tf 100 700 Td (A B C D A) Tj ET", Font(font)));
    }

    [Fact]
    public void Actual_text_replaces_the_glyphs_it_covers()
    {
        Assert.Equal("BAfi", Text("BT /F1 10 Tf 100 700 Td (BA) Tj /Span << /ActualText (fi) >> BDC (AB) Tj EMC ET"));
        Assert.Equal("a b", Text("BT /F1 10 Tf 100 700 Td /Span << /ActualText ( a   b ) >> BDC (AB) Tj EMC ET"));
    }

    [Fact]
    public void Columns_are_read_one_after_the_other_and_tables_row_by_row()
    {
        var text = Extract(HtmlToPdf.ConvertToBytes(ColumnsHtml)).Text;

        Assert.Equal(
            """
            Report Heading Across Columns

            LEFT1 alpha beta gamma delta epsilon zeta
            eta theta iota kappa lambda mu nu xi omicron
            pi rho

            LEFT2 alpha beta gamma delta epsilon zeta
            eta theta iota kappa lambda mu nu xi omicron
            pi rho

            RIGHT1 alpha beta gamma delta epsilon
            zeta eta theta iota kappa lambda mu nu xi
            omicron pi rho

            RIGHT2 alpha beta gamma delta epsilon
            zeta eta theta iota kappa lambda mu nu xi
            omicron pi rho

            Item Qty Price
            Widget 2 10.00
            Gadget 1 5.50

            Footer x2 and H2O
            """.ReplaceLineEndings("\n"),
            text);
    }

    [Fact]
    public void Layout_mode_keeps_columns_apart_on_each_line()
    {
        var text = Extract(HtmlToPdf.ConvertToBytes(ColumnsHtml), new TextExtractionOptions { Mode = TextExtractionMode.Layout }).Text;
        var lines = text.Split('\n');

        var row = Assert.Single(lines, l => l.StartsWith("LEFT1", StringComparison.Ordinal));
        Assert.Matches(@"^LEFT1 alpha beta gamma[a-z ]*[a-z] {2,}RIGHT1 alpha", row);

        // The second line of each column starts where its first line does.
        var second = lines[Array.IndexOf(lines, row) + 1];
        Assert.Equal(row.IndexOf("RIGHT1", StringComparison.Ordinal), second.IndexOf(second.Split("  ", StringSplitOptions.RemoveEmptyEntries)[1].Trim(), StringComparison.Ordinal));
        Assert.Contains(lines, l => l.TrimStart().StartsWith("Widget", StringComparison.Ordinal) && l.Contains("  2  ", StringComparison.Ordinal));
    }

    [Fact]
    public void Layout_mode_puts_two_spaces_at_a_column_gap_even_after_narrow_glyphs()
    {
        // A is 200 units wide, B 500: the median glyph is B, so ten As take more characters than their width.
        var font = $"<< /Type /Font /Subtype /Type1 /BaseFont /Narrow /FirstChar 65 /LastChar 66 /Widths [200 500] /Encoding /WinAnsiEncoding >>";
        var content = "BT /F1 10 Tf 100 700 Td (BBBBBBBBBBBBBBBBBBBB) Tj 0 -14 Td (AAAAAAAAAA) Tj 40 0 Td (B) Tj ET";
        var lines = Text(content, Font(font), options: new TextExtractionOptions { Mode = TextExtractionMode.Layout }).Split('\n');

        Assert.Equal("AAAAAAAAAA  B", lines[1]);
    }

    [Fact]
    public void Raw_mode_keeps_the_drawing_order()
    {
        var content = "BT /F1 10 Tf 100 600 Td (B) Tj ET BT /F1 10 Tf 100 700 Td (A) Tj 20 0 Td (BA) Tj ET";
        var raw = new TextExtractionOptions { Mode = TextExtractionMode.Raw };

        Assert.Equal("B\nA BA", Text(content, options: raw));
        Assert.Equal("A BA\n\nB", Text(content));
    }

    [Fact]
    public void A_turned_page_is_read_as_displayed()
    {
        // On a page displayed turned by 90 degrees, horizontal user-space text runs down the screen.
        var text = Text("BT /F1 10 Tf 12 TL 100 700 Td (AB) Tj T* (BA) Tj ET", pageEntries: "/MediaBox [0 0 600 800] /Rotate 90");

        Assert.Equal("AB\nBA", text);
    }

    [Fact]
    public void Text_in_other_directions_follows_the_ordinary_text()
    {
        var text = Text("BT /F1 10 Tf 0 1 -1 0 500 100 Tm (BB) Tj ET BT /F1 10 Tf 100 700 Td (AA) Tj ET");

        Assert.Equal("AA\n\nBB", text);
    }

    [Fact]
    public void Vertical_writing_is_read_in_columns_from_right_to_left()
    {
        var font = "<< /Type /Font /Subtype /Type0 /BaseFont /V /Encoding /UniJIS-UCS2-V /DescendantFonts [<< /Type /Font /Subtype /CIDFontType0 /BaseFont /V >>] >>";
        var text = Text("BT /F1 20 Tf 270 700 Td <4E0B4E0A> Tj ET BT /F1 20 Tf 300 700 Td <65E5672C> Tj ET", Font(font));

        Assert.Equal("\u65E5\u672C\n\u4E0B\u4E0A", text);
    }

    [Fact]
    public void Right_to_left_text_is_read_in_logical_order()
    {
        // Hebrew drawn as it is displayed: "123 " then the letters of shalom from left to right (final mem first).
        var toUnicode = TestPdf.Stream("1 begincodespacerange <00> <FF> endcodespacerange 4 beginbfchar <41> <05DD> <42> <05D5> <43> <05DC> <44> <05E9> endbfchar");
        var text = Text("BT /F1 10 Tf 100 700 Td (123 ABCD) Tj ET", Font(HebrewFont), extra: [toUnicode]);

        Assert.Equal("\u05E9\u05DC\u05D5\u05DD 123", text);
    }

    /// <summary>A simple font whose codes are ASCII, except A and B (see <see cref="HebrewToUnicode"/>); every glyph 500 units wide.</summary>
    internal static readonly string HebrewFont =
        $"<< /Type /Font /Subtype /TrueType /BaseFont /Hebrew /FirstChar 32 /LastChar 126 /Widths [{string.Concat(Enumerable.Repeat("500 ", 95))}] /ToUnicode 5 0 R >>";

    /// <summary>The ToUnicode map of <see cref="HebrewFont"/>: A is alef, B is bet.</summary>
    internal static readonly string HebrewToUnicode =
        TestPdf.Stream("1 begincodespacerange <00> <FF> endcodespacerange 2 beginbfchar <41> <05D0> <42> <05D1> endbfchar");

    [Fact]
    public void Right_to_left_table_rows_are_read_cell_by_cell_from_the_right()
    {
        // A Hebrew invoice row drawn as displayed: total, quantity, then the item "\u05D0\u05D1 Web" (shown "Web \u05D1\u05D0").
        var text = Text("BT /F1 10 Tf 100 700 Td (800.00) Tj 100 0 Td (2) Tj 100 0 Td (Web BA) Tj ET", Font(HebrewFont), extra: [HebrewToUnicode]);

        Assert.Equal("\u05D0\u05D1 Web 2 800.00", text);
    }

    [Fact]
    public void A_right_aligned_block_with_as_many_Latin_as_Hebrew_letters_is_right_to_left()
    {
        // Under a full-width line, "Web \u05D0\u05D1" in a right-to-left paragraph shows as "\u05D1\u05D0 Web", flush right.
        var content = "BT /F1 10 Tf 50 700 Td (AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA) Tj ET "
            + "BT /F1 10 Tf 520 600 Td (BA Web) Tj ET";
        var text = Text(content, Font(HebrewFont), extra: [HebrewToUnicode]);

        Assert.EndsWith("\n\nWeb \u05D0\u05D1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Arabic_from_the_HTML_converter_is_read_from_its_actual_text()
    {
        var html = "<p style=\"font-family: 'Noto Sans Arabic'; direction: rtl\">\u0627\u0644\u0633\u0644\u0627\u0645 \u0639\u0644\u064A\u0643\u0645</p>";
        var fonts = BundledFonts.CreateCollection().AddFile(Fixtures.Path("NotoSansArabic-Subset.ttf"));

        Assert.Equal("\u0627\u0644\u0633\u0644\u0627\u0645 \u0639\u0644\u064A\u0643\u0645", Extract(HtmlToPdf.ConvertToBytes(html, new HtmlConverterOptions { Fonts = fonts })).Text);
    }

    [Theory]
    [InlineData(new[] { "\u05DD", "\u05D5", "\u05DC", "\u05E9" }, null, "\u05E9\u05DC\u05D5\u05DD")]
    [InlineData(new[] { "a", " ", "\u05D1", "\u05D0", " ", "c" }, false, "a \u05D0\u05D1 c")]
    [InlineData(new[] { "1", ",", "2", "5", "0", " ", "\u05D1", "\u05D0" }, true, "\u05D0\u05D1 1,250")]
    [InlineData(new[] { "W", "e", "b", " ", "\u05D1", "\u05D0" }, true, "\u05D0\u05D1 Web")]
    [InlineData(new[] { "\u20AA", "5", " ", "\u05D1", "\u05D0" }, true, "\u05D0\u05D1 \u20AA5")]
    [InlineData(new[] { "a", "b", "c" }, true, "abc")]
    [InlineData(new[] { "\u05D1", " ", "1", " ", "\u05D0" }, false, "\u05D0 1 \u05D1")]
    [InlineData(new[] { "W", "e", "b", " ", "1", "2", " ", "\u05D1", "\u05D0" }, true, "\u05D0\u05D1 12 Web")]
    public void Bidi_turns_visual_order_into_logical_order(string[] visual, bool? rightToLeft, string expected) =>
        Assert.Equal(expected, Bidi.ToLogical(visual, rightToLeft));

    [Fact]
    public void Text_drawn_by_RaiCore_reads_back_in_order()
    {
        const string html = """
            <h1>Invoice 2026-001</h1>
            <p>Thank you for your order. Payment is due in <b>30 days</b>.</p>
            <ul><li>First item</li><li>Second item</li></ul>
            """;
        var text = Extract(HtmlToPdf.ConvertToBytes(html)).Text;

        Assert.Equal("Invoice 2026-001\n\nThank you for your order. Payment is due in 30 days.\n\n\u2022 First item\n\u2022 Second item", text);
    }

    [Fact]
    public void Documents_from_other_producers_read_as_written()
    {
        var text = new TextExtractor(PdfReader.Open(Fixtures.Read("Extraction/reportlab-truetype.pdf"))).ExtractText();

        Assert.Equal(
            "Gr\u00FC\u00DFe \u2013 1,50 \u20AC \u201Cquoted\u201D\n\n\u03A9mega \u03B1\u03B2\u03B3\n\nScaled 80 percent\nRaised\n\nTurned 30 degrees",
            text);
    }

    private const string ColumnsHtml = """
        <html><body style="font: 11pt 'Liberation Sans'">
        <h1>Report Heading Across Columns</h1>
        <div style="display: flex; gap: 24pt">
          <div style="flex: 1"><p>LEFT1 alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho</p>
            <p>LEFT2 alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho</p></div>
          <div style="flex: 1"><p>RIGHT1 alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho</p>
            <p>RIGHT2 alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho</p></div>
        </div>
        <table style="width: 100%"><tr><th>Item</th><th>Qty</th><th>Price</th></tr>
          <tr><td>Widget</td><td>2</td><td>10.00</td></tr><tr><td>Gadget</td><td>1</td><td>5.50</td></tr></table>
        <p>Footer x<sup>2</sup> and H<sub>2</sub>O</p>
        </body></html>
        """;
}

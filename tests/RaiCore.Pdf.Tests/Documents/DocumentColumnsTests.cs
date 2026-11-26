using PDFtoImage;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Reading;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>
/// Sections with several text columns in Word, OpenDocument and RTF documents (#312): laid out in columns of equal or
/// their own widths, with a line between them, column breaks, and balanced where a continuous section break ends them.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class DocumentColumnsTests
{
    [Fact]
    public void A_column_break_starts_the_next_column_of_a_docx_section()
    {
        // Two columns 0.5 inch apart: the second starts (content width + gap) / 2 to the right of the first.
        var body = Docx.P("Alpha") + Docx.P("Bravo") + ColumnBreak("Charlie") + Docx.P("Delta") + "<w:sectPr><w:cols w:num=\"2\" w:space=\"1440\"/></w:sectPr>";
        var pdf = DocxToPdf.ConvertToBytes(Docx.Document(body));

        var (a, c) = (Bounds(pdf, "Alpha"), Bounds(pdf, "Charlie"));
        Assert.Equal(SecondColumn(a.X, 72), c.X, 1);
        Assert.Equal(a.Top, c.Top, 1);
        Assert.Equal(a.X, Bounds(pdf, "Bravo").X, 1);
    }

    [Fact]
    public void A_page_break_in_a_section_with_columns_starts_the_next_page()
    {
        var body = Docx.P("Alpha") + "<w:p><w:r><w:br w:type=\"page\"/><w:t>Bravo</w:t></w:r></w:p>" + "<w:sectPr><w:cols w:num=\"2\"/></w:sectPr>";
        var pdf = DocxToPdf.ConvertToBytes(Docx.Document(body));

        var pages = Docx.Pages(pdf);
        Assert.Equal(2, pages.Length);
        Assert.Contains("Bravo", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_column_break_in_a_section_of_one_column_starts_a_page()
    {
        var pdf = DocxToPdf.ConvertToBytes(Docx.Document(Docx.P("Alpha") + ColumnBreak("Bravo")));

        Assert.Equal(2, Docx.Pages(pdf).Length);
    }

    [Fact]
    public void The_last_section_fills_its_columns_one_after_the_other_and_a_continuous_break_balances_them()
    {
        var columns = "<w:cols w:num=\"2\" w:space=\"720\"/>";
        var last = DocxToPdf.ConvertToBytes(Docx.Document(Docx.P("Alpha") + Docx.P("Bravo") + $"<w:sectPr>{columns}</w:sectPr>"));
        Assert.Equal(Bounds(last, "Alpha").X, Bounds(last, "Bravo").X, 1); // Both in the first column.

        var beforePage = DocxToPdf.ConvertToBytes(Docx.Document(
            Docx.P("Alpha") + Docx.P("Bravo", $"<w:sectPr>{columns}</w:sectPr>") + Docx.P("Next") + "<w:sectPr/>"));
        Assert.Equal(Bounds(beforePage, "Alpha").X, Bounds(beforePage, "Bravo").X, 1); // A section on the next page: not balanced.

        var balanced = DocxToPdf.ConvertToBytes(Docx.Document(
            Docx.P("Alpha") + Docx.P("Bravo", $"<w:sectPr>{columns}</w:sectPr>") + Docx.P("After") + "<w:sectPr><w:type w:val=\"continuous\"/></w:sectPr>"));
        var (a, b, after) = (Bounds(balanced, "Alpha"), Bounds(balanced, "Bravo"), Bounds(balanced, "After"));
        Assert.Equal(SecondColumn(a.X, 36), b.X, 1);
        Assert.Equal(a.Top, b.Top, 1);
        Assert.True(after.Top < a.Y, "The next section goes on below the columns.");
        Assert.Equal(a.X, after.X, 1);
    }

    [Fact]
    public void Docx_columns_of_their_own_widths_keep_them_and_a_line_separates_columns()
    {
        // 2 and 4 inches with 0.5 inch between: the second column starts 2.5 inches after the first.
        var body = Docx.P("Alpha") + ColumnBreak("Bravo")
            + "<w:sectPr><w:cols w:num=\"2\" w:equalWidth=\"0\" w:sep=\"1\"><w:col w:w=\"2880\" w:space=\"720\"/><w:col w:w=\"5760\"/></w:cols></w:sectPr>";
        var pdf = DocxToPdf.ConvertToBytes(Docx.Document(body));

        var (a, b) = (Bounds(pdf, "Alpha"), Bounds(pdf, "Bravo"));
        Assert.Equal(a.X + 180, b.X, 1);
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        var lineX = (int)Math.Round(a.X + 144 + 18);
        var row = (int)(bitmap.Height - a.Y - (a.Height / 2));
        Assert.True(Math.Min(bitmap.GetPixel(lineX - 1, row).Red, bitmap.GetPixel(lineX, row).Red) < 128, "A line in the middle of the gap.");
        Assert.Equal(new SKColor(255, 255, 255), bitmap.GetPixel(lineX + 8, row));
    }

    [Fact]
    public void Rtf_sections_have_columns_line_breaks_and_widths()
    {
        var equal = RtfToPdf.ConvertToBytes(Rtf.Document(@"\sectd\cols2\colsx1440 Alpha\par\column Bravo\par"));
        var (a, b) = (Bounds(equal, "Alpha"), Bounds(equal, "Bravo"));
        Assert.Equal(SecondColumn(a.X, 72), b.X, 1);
        Assert.Equal(a.Top, b.Top, 1);

        var own = RtfToPdf.ConvertToBytes(Rtf.Document(@"\sectd\cols2\colno1\colw2880\colsr720\colno2\colw5760 Alpha\par\column Bravo\par"));
        Assert.Equal(Bounds(own, "Alpha").X + 180, Bounds(own, "Bravo").X, 1);
    }

    [Fact]
    public void An_odt_section_with_columns_is_balanced_on_the_page_and_the_text_after_it_goes_on_below()
    {
        var style = "<style:style style:name=\"Sect1\" style:family=\"section\"><style:section-properties>"
            + "<style:columns fo:column-count=\"2\" fo:column-gap=\"0.5in\"/></style:section-properties></style:style>";
        var body = Odt.P("Before") + "<text:section text:name=\"S\" text:style-name=\"Sect1\">" + Odt.P("Alpha") + Odt.P("Bravo") + "</text:section>" + Odt.P("After");
        var pdf = OdtToPdf.ConvertToBytes(Odt.Document(body, style));

        var (before, a, b, after) = (Bounds(pdf, "Before"), Bounds(pdf, "Alpha"), Bounds(pdf, "Bravo"), Bounds(pdf, "After"));
        Assert.Single(Docx.Pages(pdf));
        Assert.Equal(before.X, a.X, 1);
        Assert.Equal(SecondColumn(a.X, 36), b.X, 1);
        Assert.Equal(a.Top, b.Top, 1);
        Assert.True(after.Top < a.Y);
        Assert.Equal(before.X, after.X, 1);
    }

    [Fact]
    public void Odt_columns_share_the_width_by_their_relative_widths_less_their_indents()
    {
        // A quarter and three quarters of the width; the indents (0.25 in each) make a 0.5 in gap.
        var style = "<style:style style:name=\"Sect1\" style:family=\"section\"><style:section-properties><style:columns fo:column-count=\"2\">"
            + "<style:column-sep style:width=\"0.02in\"/>"
            + "<style:column style:rel-width=\"1*\" fo:start-indent=\"0in\" fo:end-indent=\"0.25in\"/>"
            + "<style:column style:rel-width=\"3*\" fo:start-indent=\"0.25in\" fo:end-indent=\"0in\"/></style:columns></style:section-properties></style:style>";
        var body = "<text:section text:name=\"S\" text:style-name=\"Sect1\">" + Odt.P("Alpha") + Odt.P("Bravo") + "</text:section>" + Odt.P("Then");
        var pdf = OdtToPdf.ConvertToBytes(Odt.Document(body, style));

        var a = Bounds(pdf, "Alpha");
        var width = PageWidth(pdf) - (2 * a.X);
        Assert.Equal(a.X + (width / 4) + 18, Bounds(pdf, "Bravo").X, 1);
    }

    [Fact]
    public void An_odt_page_style_with_columns_lays_the_pages_out_in_them()
    {
        var layout = "<style:page-layout style:name=\"pm\"><style:page-layout-properties fo:page-width=\"8.5in\" fo:page-height=\"11in\" fo:margin=\"1in\">"
            + "<style:columns fo:column-count=\"3\" fo:column-gap=\"0.25in\"/></style:page-layout-properties></style:page-layout>";
        var pdf = OdtToPdf.ConvertToBytes(Odt.Document(Odt.P("Alpha") + Odt.P("Bravo"), pageLayouts: layout, masters: "<style:master-page style:name=\"Standard\" style:page-layout-name=\"pm\"/>"));

        // Three columns of (6.5 in - 2 x 0.25 in) / 3 = 2 in, filled one after the other (the last section): the
        // text is in the first one; the column rule is not drawn without a style:column-sep.
        Assert.Equal(72, Bounds(pdf, "Alpha").X, 1);
        Assert.Equal(72, Bounds(pdf, "Bravo").X, 1);
    }

    [Fact]
    public void Odt_sections_in_sections_go_back_to_the_columns_around_them_and_a_section_at_the_end_adds_no_empty_one()
    {
        var styles = "<style:style style:name=\"Two\" style:family=\"section\"><style:section-properties><style:columns fo:column-count=\"2\"/></style:section-properties></style:style>"
            + "<style:style style:name=\"Three\" style:family=\"section\"><style:section-properties><style:columns fo:column-count=\"3\"/></style:section-properties></style:style>";
        var body = Odt.P("One") + "<text:section text:name=\"Outer\" text:style-name=\"Two\">" + Odt.P("Two")
            + "<text:section text:name=\"Inner\" text:style-name=\"Three\">" + Odt.P("Three") + "</text:section>" + Odt.P("Two again") + "</text:section>";
        var document = Odt.Read(Odt.Document(body, styles));

        Assert.Equal([1, 2, 3, 2], document.Sections.Select(s => s.Columns.Count));
        Assert.Equal("Two again", Docx.Text(Docx.Paragraphs(document.Sections[^1].Blocks).Single()));
    }

    [Fact]
    public void Doc_column_properties_set_the_gap_the_line_and_the_widths()
    {
        // sprmSCcolumns 1 (two columns), sprmSDxaColumns 1440, sprmSLBetween 1.
        var sep = Documents.Doc.DocSep.Default;
        Documents.Doc.DocProperties.ApplySection(ref sep, [0x0B, 0x50, 0x01, 0x00, 0x0C, 0x90, 0xA0, 0x05, 0x19, 0x30, 0x01]);
        Assert.Equal(new Documents.Model.WordColumns(2, 72, true), Documents.Doc.DocReader.ColumnsOf(sep));

        // Not evenly spaced (sprmSFEvenlySpaced 0): sprmSDxaColWidth and sprmSDxaColSpacing per column.
        Documents.Doc.DocProperties.ApplySection(ref sep, [0x05, 0x30, 0x00, 0x03, 0xF2, 0x00, 0x40, 0x0B, 0x04, 0xF2, 0x00, 0x20, 0x03, 0x03, 0xF2, 0x01, 0x80, 0x16]);
        var columns = Documents.Doc.DocReader.ColumnsOf(sep);
        Assert.Equal([(144.0, 40.0), (288.0, 0.0)], columns.Widths!); // The last column has no space after it.
    }

    [Fact]
    public void Doc_sections_with_columns_are_laid_out_in_them()
    {
        var pdf = DocToPdf.ConvertToBytes(Fixtures.Read(Path.Combine("Documents", "Doc", "testWORD_various.doc")));

        var (first, second) = (Bounds(pdf, "Row 1 column 1"), Bounds(pdf, "Row 1 column 2"));
        Assert.True(second.X > first.X + 150, $"{second.X} {first.X}");
        Assert.Equal(first.Top, second.Top, 1);
    }

    /// <summary>Where the second of two equal columns starts, from the first column's x and the gap (symmetric margins).</summary>
    private static double SecondColumn(double firstX, double gap) => firstX + ((612 - (2 * firstX) + gap) / 2);

    private static string ColumnBreak(string text) => $"<w:p><w:r><w:br w:type=\"column\"/><w:t>{text}</w:t></w:r></w:p>";

    private static PdfRectangle Bounds(byte[] pdf, string text)
    {
        var reader = PdfReader.Open(pdf);
        var extractor = new TextExtractor(reader);
        for (var page = 0; page < reader.Pages.Count; page++)
        {
            if (extractor.FindOnPage(page, text).FirstOrDefault() is { } match)
            {
                return match.Bounds;
            }
        }

        throw new InvalidOperationException($"'{text}' is not in the PDF.");
    }

    private static double PageWidth(byte[] pdf) => PdfReader.Open(pdf).Pages[0].Width;
}

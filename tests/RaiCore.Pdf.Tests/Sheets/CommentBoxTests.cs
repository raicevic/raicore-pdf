using System.IO.Compression;
using System.Text;
using RaiCore.Pdf.DrawingML;
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Sheets.Drawings;
using RaiCore.Pdf.Sheets.Xls;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>
/// Comments printed as displayed on the sheet: the boxes of the comments shown on it drawn over it, from .xlsx (VML
/// note shapes) and .xls (OfficeArt note shapes) workbooks. The fixtures are Apache POI's SimpleWithComments with their
/// first sheet's page setup changed (make-comment-boxes.py): only the comment on B3 is shown.
/// </summary>
public sealed class CommentBoxTests
{
    private static readonly PdfColor NoteFill = PdfColor.FromRgb(0xFF, 0xFF, 0xE1);

    private static IReadOnlyList<SheetDrawing> Boxes(Sheet sheet) =>
        [.. sheet.Formatting!.Drawings.Where(d => d.Item is ShapeItem { Geometry: PresetGeometry { Name: "rect" } })];

    [Fact]
    public void Xlsx_comments_shown_on_the_sheet_are_drawn_as_their_boxes()
    {
        var box = Assert.Single(Boxes(XlsxConverter.Read(Fixtures.Read("Sheets/Formatting/CommentBoxes.xlsx"))[0]));

        // x:Anchor "2, 15, 1, 7, 4, 15, 5, 13": columns C to E and rows 2 to 6, offsets in pixels.
        Assert.Equal(AnchorKind.TwoCell, box.Kind);
        Assert.Equal((new AnchorEdge(2, 11.25), new AnchorEdge(1, 5.25), new AnchorEdge(4, 11.25), new AnchorEdge(5, 9.75)), (box.Left, box.Top, box.Right, box.Bottom));
        var item = Assert.IsType<ShapeItem>(box.Item);
        Assert.Equal(NoteFill, item.Fill); // "infoBackground [80]".
        Assert.Equal(new ShapeLine(0.75, PdfColor.Black, []).Color, item.Line!.Color);
        Assert.Equal(0.75, item.Line.Width);

        // The comment's rich text: the author in bold, then the text on a line of its own, at its size and in its font.
        var text = item.Text!.Paragraphs;
        Assert.Equal(2, text.Count);
        Assert.Equal(("Yegor Kozlov:", true, 8.0, "Tahoma"), (text[0].Runs[0].Text, text[0].Runs[0].Bold, text[0].Runs[0].Size, text[0].Runs[0].Font));
        Assert.Equal(("third cell", false), (Assert.Single(text[1].Runs).Text, text[1].Runs[0].Bold));
    }

    [Fact]
    public void Xls_comments_shown_on_the_sheet_are_drawn_as_their_boxes()
    {
        var box = Assert.Single(Boxes(XlsConverter.Read(Fixtures.Read("Sheets/Xls/CommentBoxes.xls"))[0]));
        Assert.Equal((2, 1, 4, 5), (box.Left.Index, box.Top.Index, box.Right.Index, box.Bottom.Index));
        var item = Assert.IsType<ShapeItem>(box.Item);
        Assert.Equal(NoteFill, item.Fill);
        Assert.NotNull(item.Line);
        Assert.Equal("Yegor Kozlov:|third cell", string.Join("|", item.Text!.Paragraphs.Select(p => string.Concat(p.Runs.Select(r => r.Text)))));
        Assert.True(item.Text.Paragraphs[0].Runs[0].Bold);
    }

    [Fact]
    public void Comment_boxes_are_printed_only_as_the_page_setup_asks()
    {
        // Workbooks whose page setup does not print comments, or options that print them otherwise: no boxes.
        Assert.Empty(Boxes(XlsxConverter.Read(Fixtures.Read("Sheets/Formatting/SimpleWithComments.xlsx"))[0]));
        Assert.Empty(Boxes(XlsConverter.Read(Fixtures.Read("Sheets/Xls/SimpleWithComments.xls"))[0]));
        foreach (var comments in new[] { SheetComments.AtEnd, SheetComments.Annotations, SheetComments.None })
        {
            var options = new XlsxConverterOptions { Comments = comments };
            Assert.Empty(Boxes(XlsxConverter.Read(Fixtures.Read("Sheets/Formatting/CommentBoxes.xlsx"), options)[0]));
            Assert.Empty(Boxes(XlsConverter.Read(Fixtures.Read("Sheets/Xls/CommentBoxes.xls"), options)[0]));
        }

        var noPageSetup = new XlsxConverterOptions { UsePageSetup = false };
        Assert.Empty(Boxes(XlsxConverter.Read(Fixtures.Read("Sheets/Formatting/CommentBoxes.xlsx"), noPageSetup)[0]));
        Assert.Empty(Boxes(XlsConverter.Read(Fixtures.Read("Sheets/Xls/CommentBoxes.xls"), noPageSetup)[0]));

        // The box is printed: its text is on the page, and the printed range takes in the columns it covers.
        var pdf = XlsxToPdf.ConvertToBytes(Fixtures.Read("Sheets/Formatting/CommentBoxes.xlsx"));
        Assert.Contains("third cell", TextOf(pdf), StringComparison.Ordinal);
        Assert.Equal(5, XlsxConverter.Read(Fixtures.Read("Sheets/Formatting/CommentBoxes.xlsx"))[0].ColumnCount);
    }

    [Fact]
    public void Only_shown_notes_of_a_sheet_printing_them_as_displayed_have_boxes()
    {
        // Shapes of other objects than notes, and a page setup that does not print comments.
        var fixture = Fixtures.Read("Sheets/Formatting/CommentBoxes.xlsx");
        Assert.Empty(Boxes(XlsxConverter.Read(Rewrite(fixture, "xl/drawings/vmlDrawing1.vml", v => v.Replace("ObjectType=\"Note\"", "ObjectType=\"Drop\"", StringComparison.Ordinal)))[0]));
        Assert.Empty(Boxes(XlsxConverter.Read(Rewrite(fixture, "xl/worksheets/sheet1.xml", v => v.Replace("asDisplayed", "none", StringComparison.Ordinal)))[0]));

        // .xls: the SETUP record's fNotes alone prints them as displayed, with fEndNotes at the end.
        static XlsxWorksheet Setup(int options) => SheetFormattingTests.ReadXls(BiffBuilder.SingleSheet(s => s.Record(0x00A1, BiffBuilder.U16(9), BiffBuilder.U16(100),
            BiffBuilder.U16(1), BiffBuilder.U16(1), BiffBuilder.U16(1), BiffBuilder.U16(options), BiffBuilder.U16(600), BiffBuilder.U16(600),
            BiffBuilder.F64(0.5), BiffBuilder.F64(0.5), BiffBuilder.U16(1))));
        Assert.Equal((true, false), (Setup(0x0024).CommentsAsDisplayed, Setup(0x0024).CommentsAtEnd));
        Assert.Equal((false, true), (Setup(0x0224).CommentsAsDisplayed, Setup(0x0224).CommentsAtEnd));
        Assert.Equal((false, false), (Setup(0x0004).CommentsAsDisplayed, Setup(0x0004).CommentsAtEnd));
    }

    [Fact]
    public void Comment_boxes_are_drawn_over_the_other_drawings()
    {
        // The fixture with a shape in a drawing part too: the comment's box comes after it.
        const string Drawing = "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">"
            + "<xdr:twoCellAnchor><xdr:from><xdr:col>2</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>2</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>"
            + "<xdr:to><xdr:col>3</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>4</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>"
            + "<xdr:sp><xdr:nvSpPr><xdr:cNvPr id=\"2\" name=\"Oval\"/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst=\"ellipse\"/></xdr:spPr></xdr:sp><xdr:clientData/></xdr:twoCellAnchor></xdr:wsDr>";
        var xlsx = Rewrite(Fixtures.Read("Sheets/Formatting/CommentBoxes.xlsx"), "xl/worksheets/sheet1.xml",
            v => v.Replace("<legacyDrawing", "<drawing r:id=\"rId9\"/><legacyDrawing", StringComparison.Ordinal));
        xlsx = Rewrite(xlsx, "xl/worksheets/_rels/sheet1.xml.rels", v => v.Replace("</Relationships>",
            "<Relationship Id=\"rId9\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing1.xml\"/></Relationships>", StringComparison.Ordinal));
        xlsx = Rewrite(xlsx, "xl/drawings/drawing1.xml", _ => Drawing);
        var drawings = XlsxConverter.Read(xlsx)[0].Formatting!.Drawings;
        Assert.Equal(["ellipse", "rect"], drawings.Select(d => ((PresetGeometry)((ShapeItem)d.Item).Geometry).Name));
    }

    /// <summary>A package with one part's text changed (or added).</summary>
    private static byte[] Rewrite(byte[] package, string part, Func<string, string> change)
    {
        using var buffer = new MemoryStream();
        buffer.Write(package);
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry(part);
            string text;
            if (entry is null)
            {
                text = string.Empty;
            }
            else
            {
                using (var reader = new StreamReader(entry.Open()))
                {
                    text = reader.ReadToEnd();
                }

                entry.Delete();
            }

            using var stream = zip.CreateEntry(part).Open();
            stream.Write(Encoding.UTF8.GetBytes(change(text)));
        }

        return buffer.ToArray();
    }

    [Theory]
    [InlineData("#ffffe1", 0xFF, 0xFF, 0xE1)]
    [InlineData("#1a2", 0x11, 0xAA, 0x22)]
    [InlineData("#00ff00 [11]", 0x00, 0xFF, 0x00)]
    [InlineData("infoBackground [80]", 0xFF, 0xFF, 0xE1)]
    [InlineData("black", 0, 0, 0)]
    public void Vml_colors_are_read(string value, int r, int g, int b) =>
        Assert.Equal(PdfColor.FromRgb((byte)r, (byte)g, (byte)b), XlsxCommentBoxes.Color(value, new XlsxColors()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("fill lighten(50)")]
    public void Vml_colors_that_are_not_known_are_left_out(string? value) => Assert.Null(XlsxCommentBoxes.Color(value, new XlsxColors()));

    [Theory]
    [InlineData("1pt", 1)]
    [InlineData("2px", 1.5)]
    [InlineData("12700", 1)]
    [InlineData(null, 0.75)]
    [InlineData("-3pt", 0.75)]
    [InlineData("wide", 0.75)]
    public void Vml_stroke_weights_are_read(string? value, double points) => Assert.Equal(points, XlsxCommentBoxes.Weight(value));

    private static string TextOf(byte[] pdf) => new RaiCore.Pdf.Extraction.TextExtractor(PdfReader.Open(pdf)).ExtractText();
}

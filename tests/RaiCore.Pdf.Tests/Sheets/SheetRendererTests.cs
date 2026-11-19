using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>Laying out the sheet model (any source): alignment, column widths, bands.</summary>
public class SheetRendererTests
{
    private static byte[] Render(Sheet sheet, SheetOptions? options = null) =>
        SheetRenderer.Render(sheet, options ?? new SheetOptions(), "test").ToArray();

    private static PdfRectangle Bounds(byte[] pdf, string text, int page = 0) =>
        new TextExtractor(PdfReader.Open(pdf)).FindOnPage(page, text).Single().Bounds;

    [Fact]
    public void Cells_can_be_aligned_explicitly()
    {
        SheetCell[] Row(string text, CellKind kind, CellAlignment alignment) => [new(text, kind, alignment)];
        var sheet = new Sheet(
            [
                Row("A header wider than the cells below", CellKind.Text, CellAlignment.General),
                Row("left", CellKind.Number, CellAlignment.Left),
                Row("center", CellKind.Text, CellAlignment.Center),
                Row("right", CellKind.Text, CellAlignment.Right),
                Row("general", CellKind.Text, CellAlignment.General),
                Row("12", CellKind.Number, CellAlignment.General),
            ],
            headerRows: 1);
        var pdf = Render(sheet);
        var header = Bounds(pdf, "A header wider than the cells below");
        Assert.Equal(header.X, Bounds(pdf, "left").X, 0.5);
        Assert.Equal(header.X, Bounds(pdf, "general").X, 0.5);
        Assert.Equal(header.Right, Bounds(pdf, "right").Right, 0.5);
        Assert.Equal(header.Right, Bounds(pdf, "12").Right, 0.5);
        var center = Bounds(pdf, "center");
        Assert.Equal(header.X + (header.Width / 2), center.X + (center.Width / 2), 0.5);
    }

    [Fact]
    public void The_sheet_pads_short_rows_and_bounds_its_header_rows()
    {
        var sheet = new Sheet([[new("a", CellKind.Text), new("b", CellKind.Text)], [new("c", CellKind.Text)]], headerRows: 5);
        Assert.Equal(2, sheet.ColumnCount);
        Assert.Equal(2, sheet.HeaderRows);
        Assert.Equal(SheetCell.Empty, sheet[1, 1]);
        Assert.Equal(0, new Sheet([], 1).ColumnCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Sheet([], -1));
    }

    private static SheetCell[] Cells(params string[] texts) => [.. texts.Select(t => new SheetCell(t, CellValues.Classify(t, '.')))];

    private static double[] ColumnWidths(Sheet sheet, int table = 0) =>
        [.. ((TableBlock)SheetRenderer.Layout(sheet, new SheetOptions()).Blocks[table]).ColumnWidths.Select(w => w!.Value.Points)];

    [Fact]
    public void A_header_is_right_aligned_over_mostly_numbers_only()
    {
        // One number and one text: no majority, so the header stays at the start.
        var pdf = Render(new Sheet([Cells("H"), Cells("12345"), Cells("text")], headerRows: 1));
        Assert.Equal(Bounds(pdf, "text").X, Bounds(pdf, "H").X, 0.5);
        pdf = Render(new Sheet([Cells("H"), Cells("12345"), Cells("1"), Cells("text")], headerRows: 1));
        Assert.Equal(Bounds(pdf, "12345").Right, Bounds(pdf, "H").Right, 0.5);
    }

    [Fact]
    public void Columns_are_widened_for_words_up_to_about_twenty_characters()
    {
        // A 150-character identifier would take the page from the text beside it: it is broken instead.
        var text = string.Join(' ', Enumerable.Repeat("words of ordinary length", 30));
        var widths = ColumnWidths(new Sheet([Cells("Id", "Text"), Cells(new string('x', 150), text)], headerRows: 1));
        Assert.True(widths[0] < 9 * 14, $"{widths[0]}");
        Assert.True(widths[1] > 300, $"{widths[1]}");
    }

    [Fact]
    public void Numbers_are_never_broken()
    {
        // Four columns of long numbers and four of text do not fit A4 with the numbers whole: they go into bands.
        string[] header = ["N1", "N2", "N3", "N4", "T1", "T2", "T3", "T4"];
        var text = "a few words that wrap in a narrow column of text";
        Sheet sheet = new([Cells(header), Cells("12345678901234567891", "12345678901234567892", "12345678901234567893", "12345678901234567894", text, text, text, text)], headerRows: 1);
        var pdf = Render(sheet);
        for (var i = 1; i <= 4; i++)
        {
            var page = new TextExtractor(PdfReader.Open(pdf)).Find("1234567890123456789" + i).Single();
            Assert.Single(page.Rectangles);
        }

        Assert.True(PdfReader.Open(pdf).Pages.Count > 1);
    }
}

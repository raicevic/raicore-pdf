using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Right-to-left tables and flex containers. Uses a 200 x 200 pt page with 20 pt margins: the content area is
/// x 20-180, so a 40 pt item at the right edge starts at 140.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class RightToLeftLayoutTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));
    private static readonly PdfColor Gray = PdfColor.FromRgb(200, 200, 200);

    [Fact]
    public void A_right_to_left_row_starts_on_the_right()
    {
        var (a, b) = (Rect(40), Rect(40));
        var placements = Layout(new FlexBlock { RightToLeft = true }.Add(a).Add(b));

        Assert.Equal(140, Left(placements, a));
        Assert.Equal(100, Left(placements, b));
    }

    [Fact]
    public void A_right_to_left_reversed_row_starts_on_the_left()
    {
        var (a, b) = (Rect(40), Rect(40));
        var placements = Layout(new FlexBlock { RightToLeft = true, Direction = FlexDirection.RowReverse }.Add(a).Add(b));

        Assert.Equal(20, Left(placements, a));
        Assert.Equal(60, Left(placements, b));
    }

    [Theory]
    [InlineData(JustifyContent.FlexStart, 140, 100)]
    [InlineData(JustifyContent.FlexEnd, 60, 20)]
    [InlineData(JustifyContent.SpaceBetween, 140, 20)]
    public void Justify_content_starts_on_the_right(JustifyContent justify, double first, double second)
    {
        var (a, b) = (Rect(40), Rect(40));
        var placements = Layout(new FlexBlock { RightToLeft = true, JustifyContent = justify }.Add(a).Add(b));

        Assert.Equal(first, Left(placements, a), 6);
        Assert.Equal(second, Left(placements, b), 6);
    }

    [Theory]
    [InlineData(false, FlexDirection.RowReverse)]
    [InlineData(true, FlexDirection.Row)]
    public void The_margin_on_the_main_start_side_comes_first(bool rightToLeft, FlexDirection direction)
    {
        // The row starts on the right, so the right margin is the first item's start margin.
        var a = Rect(40, new Thickness(0, 10, 0, 0));
        var placements = Layout(new FlexBlock { RightToLeft = rightToLeft, Direction = direction }.Add(a).Add(Rect(40)));

        Assert.Equal(130, Left(placements, a));
    }

    [Fact]
    public void An_auto_margin_on_the_start_side_pushes_a_right_to_left_item_left()
    {
        var a = new FlexItem(Rect(40)) { AutoMargins = AutoMargins.Right };
        var placements = Layout(new FlexBlock { RightToLeft = true }.Add(a));

        Assert.Equal(20, Left(placements, a.Content));
    }

    [Theory]
    [InlineData(FlexAlignment.FlexStart, 140)]
    [InlineData(FlexAlignment.FlexEnd, 20)]
    [InlineData(FlexAlignment.Center, 80)]
    public void A_right_to_left_columns_cross_axis_starts_on_the_right(FlexAlignment alignment, double left)
    {
        var a = Rect(40);
        var placements = Layout(new FlexBlock { RightToLeft = true, Direction = FlexDirection.Column, AlignItems = alignment }.Add(a));

        Assert.Equal(left, Left(placements, a));
    }

    [Fact]
    public void Wrapped_right_to_left_columns_are_placed_from_the_right()
    {
        var (a, b) = (Rect(40, height: 100), Rect(40, height: 100));
        var flex = new FlexBlock(new BoxStyle { Height = 150 })
        {
            RightToLeft = true,
            Direction = FlexDirection.Column,
            Wrap = FlexWrap.Wrap,
            AlignItems = FlexAlignment.FlexStart,
            AlignContent = AlignContent.FlexStart,
        };
        var placements = Layout(flex.Add(a).Add(b));

        Assert.Equal(140, Left(placements, a));
        Assert.Equal(100, Left(placements, b));
    }

    [Fact]
    public void A_right_to_left_tables_first_column_is_on_the_right()
    {
        var table = Table(rightToLeft: true, new TableRow(Cell("A"), Cell("Hello world")));
        var layout = TableLayouter.Layout(table, 0, 160);

        var a = layout.Cells.Single(c => c.Grid.Cell.Content.OfType<ParagraphBlock>().Single().Paragraph.Spans[0].Text == "A");
        var hello = layout.Cells.Single(c => c != a);
        Assert.True(a.X > hello.X, $"A at {a.X}, Hello world at {hello.X}");
        Assert.Equal(160, a.X + a.Width, 6);
        Assert.Equal(0, hello.X, 6);
    }

    [Fact]
    public void Column_widths_and_spans_count_from_the_right()
    {
        var table = Table(
            rightToLeft: true,
            new TableRow(new TableCell(new Paragraph(Text).Add("AB")) { ColumnSpan = 2 }, Cell("C")),
            new TableRow(Cell("a"), Cell("b"), Cell("c")));
        table.ColumnWidths.Add(TableWidth.Points(30)); // The first column: the rightmost.
        var layout = TableLayouter.Layout(table, 0, 160);

        Assert.Equal(30, layout.ColumnX[3] - layout.ColumnX[2], 6);
        var spanning = layout.Cells.Single(c => c.Grid.ColumnSpan == 2);
        Assert.Equal((1, 3), (spanning.Grid.Column, spanning.Grid.Column + spanning.Grid.ColumnSpan));
        Assert.Equal(0, layout.Cells.Single(c => c.Grid.Row == 0 && c.Grid.ColumnSpan == 1).Grid.Column);
    }

    [Fact]
    public void A_right_to_left_table_renders_its_first_cell_on_the_right()
    {
        var red = new TableCell(new Paragraph(Text).Add("x")) { Style = new BoxStyle { Background = PdfColor.FromRgb(255, 0, 0) } };
        var table = Table(rightToLeft: true, new TableRow(red, Cell("y")));
        var flow = new FlowDocument(Small).Add(new TableBlock(new BoxStyle { Width = 160 }) { RightToLeft = true }.Add(table.Rows[0]));

        using var bitmap = Conversion.ToImage(flow.Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(170, 25));
        Assert.NotEqual(new SKColor(255, 0, 0), bitmap.GetPixel(30, 25));
    }

    private static ContainerBlock Rect(double width, Thickness margin = default, double height = 10) =>
        new(new BoxStyle { Width = width, Margin = margin, Background = Gray }, new SpacerBlock(height));

    private static TableCell Cell(string text) => new(new Paragraph(Text).Add(text));

    private static TableBlock Table(bool rightToLeft, params TableRow[] rows)
    {
        var table = new TableBlock { RightToLeft = rightToLeft };
        foreach (var row in rows)
        {
            table.Add(row);
        }

        return table;
    }

    private static IReadOnlyList<Placement> Layout(FlexBlock flex) => new FlowDocument(Small).Add(flex).Paginate().Placements;

    private static double Left(IReadOnlyList<Placement> placements, Block block) => placements.Single(p => p.Block == block).Left;
}

using System.Text;
using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Border styles: the drawing operators per style, collapsed border conflict resolution, and rendered pixels. Render
/// tests use a 200 x 200 pt page with 20 pt margins at 72 dpi, so a pixel is a point and a box placed first starts at
/// (20, 20) from the top left.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class BorderStyleTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));
    private static readonly PdfColor Gray = PdfColor.FromRgb(128, 128, 128);

    // ---- API ----

    [Fact]
    public void Sides_are_solid_unless_given_a_style()
    {
        Assert.Equal(BorderStyle.Solid, new BorderSide(1, PdfColor.Black).Style);
        Assert.Equal(BorderStyle.Solid, Borders.All(1, PdfColor.Black).Left.Style);
        Assert.Equal(BorderStyle.Dotted, Borders.All(1, PdfColor.Black, BorderStyle.Dotted).Bottom.Style);
        Assert.NotEqual(new BorderSide(1, PdfColor.Black), new BorderSide(1, PdfColor.Black, BorderStyle.Dashed));
    }

    // ---- operators ----

    [Fact]
    public void Same_colored_solid_sides_are_filled_as_one_path()
    {
        var ops = Ops(canvas => BorderPainter.PaintBox(canvas, Borders.All(2, PdfColor.Black), 0, 0, 100, 50));

        Assert.Equal(1, ops.Count(o => o == "f"));
        Assert.Equal(4, ops.Count(o => o == "h")); // Four trapezoids.
        Assert.DoesNotContain(ops, o => o.EndsWith(" d", StringComparison.Ordinal));
        Assert.Contains("0 50 m", ops); // Top: outer corners, then the inner ones on the diagonals.
        Assert.Contains("98 48 l", ops);
        Assert.Contains("2 48 l", ops);
    }

    [Fact]
    public void Dashed_sides_are_clipped_strokes_with_whole_dashes_at_the_corners()
    {
        var ops = Ops(canvas => BorderPainter.PaintBox(canvas, Borders.All(2, PdfColor.Black, BorderStyle.Dashed), 0, 0, 100, 50));

        // Dashes three times the width; the gap fits whole dashes in: 9 dashes of 6 along 100, 5 along 50.
        Assert.Equal(2, ops.Count(o => o == "[6 5.75] 0 d"));
        Assert.Equal(2, ops.Count(o => o == "[6 5] 0 d"));
        Assert.Equal(4, ops.Count(o => o == "W n"));
        Assert.Equal(4, ops.Count(o => o == "S"));
        Assert.DoesNotContain(ops, o => o.EndsWith(" J", StringComparison.Ordinal)); // Butt caps.
        Assert.Contains("0 49 m", ops); // The top's center line, from corner to corner.
        Assert.Contains("100 49 l", ops);
    }

    [Fact]
    public void Dotted_sides_are_round_zero_length_dashes_from_corner_center_to_corner_center()
    {
        var ops = Ops(canvas => BorderPainter.PaintBox(canvas, Borders.All(4, PdfColor.Black, BorderStyle.Dotted), 0, 0, 100, 50));

        // Top: dot centers from 2 to 98, 12 spaces of 8 (a dot apart); the path starts half a space early.
        Assert.Equal(4, ops.Count(o => o == "1 J"));
        Assert.Contains("[0 8] 4 d", ops);
        Assert.Contains("-2 48 m", ops);
        Assert.Contains("102 48 l", ops);
    }

    [Fact]
    public void Thin_dotted_sides_have_square_dots()
    {
        var stroke = BorderPainter.Dashes(Edge(new BorderSide(1, PdfColor.Black, BorderStyle.Dotted), 99))!.Value;

        Assert.Equal(LineCap.Butt, stroke.Cap);
        Assert.Equal([1.0, 1.0], stroke.Pattern); // 50 dots a dot apart, starting and ending on a dot.
    }

    [Fact]
    public void Short_dashed_sides_get_shorter_dashes_or_draw_solid()
    {
        Assert.Equal([7.0, 7.0], BorderPainter.Dashes(Edge(new BorderSide(4, PdfColor.Black, BorderStyle.Dashed), 21))!.Value.Pattern);
        Assert.Null(BorderPainter.Dashes(Edge(new BorderSide(4, PdfColor.Black, BorderStyle.Dashed), 10)));
    }

    [Theory]
    [InlineData(100, 6, 5.75)]  // 9 dashes; 8 would leave gaps of 7.43.
    [InlineData(50, 6, 5)]      // 5 dashes.
    [InlineData(19, 6, 7)]      // 2 dashes.
    public void The_gap_fits_whole_dashes_closest_to_the_dash_length(double length, double dash, double gap) =>
        Assert.Equal(gap, BorderPainter.Gap(length, dash)!.Value, 6);

    [Theory]
    [InlineData(2.25, 0.75)]  // 3 px: 1 + 1 + 1
    [InlineData(3, 0.75)]     // 4 px: 1 + 2 + 1
    [InlineData(3.75, 1.5)]   // 5 px: 2 + 1 + 2
    [InlineData(6, 2.25)]     // 8 px: 3 + 2 + 3
    public void Double_lines_are_a_third_of_the_width_in_whole_pixels(double width, double line) =>
        Assert.Equal(line, BorderPainter.DoubleLine(width)!.Value, 6);

    [Fact]
    public void Double_borders_below_three_pixels_draw_solid()
    {
        Assert.Null(BorderPainter.DoubleLine(1.5));
        var ops = Ops(canvas => BorderPainter.PaintBox(canvas, Borders.All(1.5, PdfColor.Black, BorderStyle.Double), 0, 0, 100, 50));
        Assert.Equal(4, ops.Count(o => o == "h"));
    }

    [Fact]
    public void Double_borders_are_two_bands_per_side()
    {
        var ops = Ops(canvas => BorderPainter.PaintBox(canvas, Borders.All(6, PdfColor.Black, BorderStyle.Double), 0, 0, 100, 50));

        Assert.Equal(1, ops.Count(o => o == "f"));
        Assert.Equal(8, ops.Count(o => o == "h"));
        Assert.Contains("97.75 47.75 l", ops); // The top's outer line ends 2.25 down, on the diagonal.
        Assert.Contains("96.25 46.25 l", ops); // Its inner line starts 3.75 down.
    }

    [Fact]
    public void Inset_darkens_the_top_and_left_and_outset_the_bottom_and_right()
    {
        var dark = BorderPainter.Shade(Gray, BorderStyle.Inset, BorderPainter.Side.Top, outer: true);

        Assert.Equal(Gray.Red - (1.0 / 3), dark.Red, 6); // A third of full brightness off.
        Assert.Equal(dark, BorderPainter.Shade(Gray, BorderStyle.Inset, BorderPainter.Side.Left, outer: true));
        Assert.Equal(Gray, BorderPainter.Shade(Gray, BorderStyle.Inset, BorderPainter.Side.Bottom, outer: true));
        Assert.Equal(Gray, BorderPainter.Shade(Gray, BorderStyle.Inset, BorderPainter.Side.Right, outer: true));
        Assert.Equal(Gray, BorderPainter.Shade(Gray, BorderStyle.Outset, BorderPainter.Side.Top, outer: true));
        Assert.Equal(dark, BorderPainter.Shade(Gray, BorderStyle.Outset, BorderPainter.Side.Bottom, outer: true));
    }

    [Fact]
    public void Groove_is_inset_outside_and_outset_inside_and_ridge_the_reverse()
    {
        var dark = BorderPainter.Shade(Gray, BorderStyle.Inset, BorderPainter.Side.Top, outer: true);

        Assert.Equal(dark, BorderPainter.Shade(Gray, BorderStyle.Groove, BorderPainter.Side.Top, outer: true));
        Assert.Equal(Gray, BorderPainter.Shade(Gray, BorderStyle.Groove, BorderPainter.Side.Top, outer: false));
        Assert.Equal(Gray, BorderPainter.Shade(Gray, BorderStyle.Ridge, BorderPainter.Side.Top, outer: true));
        Assert.Equal(dark, BorderPainter.Shade(Gray, BorderStyle.Ridge, BorderPainter.Side.Top, outer: false));
    }

    [Fact]
    public void Black_3d_borders_lighten_the_lit_sides_instead()
    {
        Assert.Equal(PdfColor.Black, BorderPainter.Shade(PdfColor.Black, BorderStyle.Inset, BorderPainter.Side.Top, outer: true));
        Assert.Equal(1.0 / 3, BorderPainter.Shade(PdfColor.Black, BorderStyle.Inset, BorderPainter.Side.Bottom, outer: true).Red, 6);
    }

    // ---- collapsed borders ----

    [Theory]
    [InlineData(BorderStyle.Double, BorderStyle.Solid)]
    [InlineData(BorderStyle.Solid, BorderStyle.Dashed)]
    [InlineData(BorderStyle.Dashed, BorderStyle.Dotted)]
    [InlineData(BorderStyle.Dotted, BorderStyle.Ridge)]
    [InlineData(BorderStyle.Ridge, BorderStyle.Outset)]
    [InlineData(BorderStyle.Outset, BorderStyle.Groove)]
    [InlineData(BorderStyle.Groove, BorderStyle.Inset)]
    public void Equally_wide_collapsed_borders_resolve_by_style(BorderStyle winner, BorderStyle loser)
    {
        var red = PdfColor.FromRgb(255, 0, 0);
        Assert.Equal(winner, TableLayouter.Resolve([new BorderSide(2, PdfColor.Black, loser), new BorderSide(2, red, winner)]).Style);
        Assert.Equal(winner, TableLayouter.Resolve([new BorderSide(2, red, winner), new BorderSide(2, PdfColor.Black, loser)]).Style);
    }

    [Fact]
    public void Collapsed_borders_resolve_hidden_then_width_then_style_then_order()
    {
        var red = PdfColor.FromRgb(255, 0, 0);
        Assert.Equal(default, TableLayouter.Resolve([new BorderSide(5, red, BorderStyle.Double), new BorderSide(0, red, BorderStyle.Hidden)]));
        Assert.Equal(new GridLine(3, red, BorderStyle.Inset), TableLayouter.Resolve([new BorderSide(2, PdfColor.Black, BorderStyle.Double), new BorderSide(3, red, BorderStyle.Inset)]));
        Assert.Equal(new GridLine(2, red, BorderStyle.Dashed), TableLayouter.Resolve([new BorderSide(2, red, BorderStyle.Dashed), new BorderSide(2, PdfColor.Black, BorderStyle.Dashed)]));
        Assert.Equal(new GridLine(1, red), TableLayouter.Resolve([new BorderSide(0, PdfColor.Black, BorderStyle.Double), new BorderSide(1, red)]));
    }

    [Fact]
    public void A_hidden_cell_border_suppresses_the_table_border()
    {
        var hidden = new BoxStyle { Border = new Borders(BorderSide.None, new BorderSide(0, PdfColor.Black, BorderStyle.Hidden), BorderSide.None, BorderSide.None) };
        var table = new TableBlock(new BoxStyle { Border = Borders.All(2, PdfColor.Black) }) { BorderCollapse = BorderCollapse.Collapse }
            .Add(new TableRow(new TableCell(Para("a")), new TableCell(Para("b")) { Style = hidden }));
        var layout = TableLayouter.Layout(table, 0, 160);

        Assert.Equal(2, layout.Vertical![0, 0].Width);
        Assert.Equal(0, layout.Vertical[0, 2].Width);
        Assert.Equal(2, layout.Horizontal![0, 1].Width);
    }

    // ---- rendering ----

    [Fact]
    public void Dashed_borders_have_gaps_along_each_side_and_dashes_at_the_corners()
    {
        using var page = Render(Box(BorderStyle.Dashed, 4));

        var top = Row(page, 22, 20, 180);
        Assert.True(Dark(top[1]) && Dark(top[^2]), "Dashes at both corners.");
        Assert.InRange(top.Count(Dark), 50, 110);
        Assert.True(Runs(top) >= 11, $"{Runs(top)} runs");
        Assert.True(Runs(Column(page, 22, 20, 120)) >= 5);
    }

    [Fact]
    public void Dotted_borders_have_round_dots_a_dot_apart()
    {
        using var page = Render(Box(BorderStyle.Dotted, 4));

        var top = Row(page, 22, 20, 180);
        Assert.True(Runs(top) >= 30, $"{Runs(top)} runs"); // About 20 dots and the gaps between.
        Assert.True(Dark(page.GetPixel(22, 22)), "A dot centered in the corner.");

        using var large = Render(Box(BorderStyle.Dotted, 10));
        Assert.True(Dark(large.GetPixel(25, 25)) && Dark(large.GetPixel(21, 25)) && Dark(large.GetPixel(25, 21)));
        Assert.False(Dark(large.GetPixel(20, 20)), "Round: the corner's outer pixel stays clear.");
    }

    [Fact]
    public void Double_borders_have_a_gap_in_the_middle()
    {
        // 9 pt = 12 px: lines of 4 px (3 pt) with 3 pt between: 20-23, gap 23-26, 26-29.
        using var page = Render(Box(BorderStyle.Double, 9));

        Assert.Equal([true, false, true], new[] { 21, 24, 27 }.Select(y => Dark(page.GetPixel(100, y))));
        Assert.Equal([true, false, true], new[] { 21, 24, 27 }.Select(x => Dark(page.GetPixel(x, 70))));
    }

    [Fact]
    public void Inset_and_outset_shade_the_top_and_bottom_differently()
    {
        using var inset = Render(Box(BorderStyle.Inset, 6, Gray));
        using var outset = Render(Box(BorderStyle.Outset, 6, Gray));

        Assert.True(inset.GetPixel(100, 22).Red < inset.GetPixel(100, 117).Red, "Inset: the top is darker than the bottom.");
        Assert.True(inset.GetPixel(22, 70).Red < inset.GetPixel(177, 70).Red, "Inset: the left is darker than the right.");
        Assert.True(outset.GetPixel(100, 22).Red > outset.GetPixel(100, 117).Red, "Outset: the bottom is darker.");
    }

    [Fact]
    public void Groove_borders_are_darker_outside_on_the_top()
    {
        using var page = Render(Box(BorderStyle.Groove, 8, Gray));

        Assert.True(page.GetPixel(100, 21).Red < page.GetPixel(100, 26).Red);
        Assert.True(page.GetPixel(100, 113).Red < page.GetPixel(100, 118).Red); // The bottom the other way round.
    }

    [Fact]
    public void Collapsed_table_borders_are_drawn_in_their_style()
    {
        using var dashed = Render(Grid(BorderStyle.Dashed));
        using var solid = Render(Grid(BorderStyle.Solid));

        // The line between the rows: the darkest pixel row below the top line, right of the text in the first column.
        var y = Enumerable.Range(28, 30).MaxBy(y => Row(solid, y, 40, 100).Count(Dark));
        Assert.Equal(1, Runs(Row(solid, y, 40, 100)));
        Assert.True(Dark(solid.GetPixel(40, y)));
        Assert.True(Runs(Row(dashed, y, 40, 100)) >= 4, $"{Runs(Row(dashed, y, 40, 100))} runs at {y}");
        Assert.True(Runs(Row(dashed, y, 40, 100)) > Runs(Row(solid, y, 40, 100)));

        static TableBlock Grid(BorderStyle style)
        {
            var cell = new BoxStyle { Border = Borders.All(3, PdfColor.Black, style) };
            return new TableBlock(new BoxStyle { Width = 160 }) { BorderCollapse = BorderCollapse.Collapse }
                .Add(new TableRow(new TableCell(Para("a")) { Style = cell }, new TableCell(Para("b")) { Style = cell }))
                .Add(new TableRow(new TableCell(Para("c")) { Style = cell }, new TableCell(Para("d")) { Style = cell }));
        }
    }

    [Fact]
    public void Split_rows_draw_collapsed_lines_in_their_style()
    {
        // As in TableSplitTests: a 20-line cell with collapsed 2 pt lines; the row ends 50 pt down the second page.
        var cell = new BoxStyle { Border = Borders.All(2, PdfColor.Black, BorderStyle.Dashed), Padding = new Thickness(0, 0, 0, 10) };
        var table = new TableBlock(new BoxStyle { Width = 160 }) { BorderCollapse = BorderCollapse.Collapse }
            .Add(new TableRow(new TableCell(new Paragraph(Text).Add(string.Join('\n', Enumerable.Range(0, 20).Select(i => $"line {i}")))) { Style = cell }));
        var pdf = new FlowDocument(Small).Add(table).Render().ToArray();
        using var second = Conversion.ToImage(pdf, page: 1, options: new RenderOptions(Dpi: 72));

        Assert.True(Runs(Column(second, 20, 20, 68)) >= 5, "The left line is dashed on the continuation.");
        Assert.True(Runs(Row(second, 70, 30, 170)) >= 9, "The bottom line is dashed.");
    }

    [Fact]
    public void A_dashed_box_split_across_pages_keeps_its_style_on_each_fragment()
    {
        var box = new ContainerBlock(new BoxStyle { Border = Borders.All(4, PdfColor.Black, BorderStyle.Dashed) },
            new ParagraphBlock(new Paragraph(Text).Add(string.Join('\n', Enumerable.Range(0, 25).Select(i => $"line {i}")))));
        var pdf = new FlowDocument(Small).Add(box).Render().ToArray();
        using var second = Conversion.ToImage(pdf, page: 1, options: new RenderOptions(Dpi: 72));

        Assert.True(Runs(Column(second, 22, 20, 80)) >= 3, "The left side is dashed on the continuation.");
        Assert.DoesNotContain(true, Row(second, 22, 100, 170).Select(Dark)); // No top border where the box continues.
    }

    // ---- helpers ----

    private static List<string> Ops(Action<PdfCanvas> draw)
    {
        var canvas = new PdfDocument().AddPage(PdfPageSize.A4).Canvas;
        draw(canvas);
        return [.. Encoding.Latin1.GetString(canvas.ToBytes()).Split('\n', StringSplitOptions.RemoveEmptyEntries)];
    }

    private static BorderPainter.Edge Edge(BorderSide side, double length) =>
        new(BorderPainter.Side.Top, side, 0, 0, 1, 0, length, 0, 0, true, true);

    private static Paragraph Para(string text) => new Paragraph(Text).Add(text);

    /// <summary>A 160 x 100 pt box with the border.</summary>
    private static ContainerBlock Box(BorderStyle style, double width, PdfColor? color = null) =>
        new(new BoxStyle { Border = Borders.All(width, color ?? PdfColor.Black, style), Width = 160, Height = 100 });

    private static SKBitmap Render(Block block) =>
        Conversion.ToImage(new FlowDocument(Small).Add(block).Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));

    private static List<SKColor> Row(SKBitmap page, int y, int x0, int x1) => [.. Enumerable.Range(x0, x1 - x0).Select(x => page.GetPixel(x, y))];

    private static List<SKColor> Column(SKBitmap page, int x, int y0, int y1) => [.. Enumerable.Range(y0, y1 - y0).Select(y => page.GetPixel(x, y))];

    private static bool Dark(SKColor color) => color.Red + color.Green + color.Blue < 3 * 128;

    /// <summary>The number of runs of dark and light pixels.</summary>
    private static int Runs(List<SKColor> pixels) => 1 + pixels.Zip(pixels.Skip(1)).Count(p => Dark(p.First) != Dark(p.Second));
}

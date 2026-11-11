using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Relative, absolute and fixed positioning and z-index. Uses a 200 x 200 pt page with 20 pt margins: the page area
/// is x 20-180, y 20-180. Boxes have backgrounds so their placements are recorded.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class PositioningLayoutTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));
    private static readonly PdfColor Gray = PdfColor.FromRgb(200, 200, 200);
    private static readonly PdfColor Red = PdfColor.FromRgb(255, 0, 0);
    private static readonly PdfColor Blue = PdfColor.FromRgb(0, 0, 255);

    [Fact]
    public void An_absolute_block_without_a_positioned_ancestor_is_placed_in_the_page_area()
    {
        var box = Absolute(new BoxInsets(Top: Inset.Points(30), Left: Inset.Points(10)), 50, 20);
        var after = Rect(10);
        var result = Paginate(box, after);

        Assert.Equal((30.0, 50.0, 50.0), (Box(result, box).Left, Box(result, box).Top, Box(result, box).Width));
        Assert.Equal(20, Box(result, after).Top, 3); // It takes no space in the flow.
    }

    [Theory]
    [InlineData(null, 10.0, null, 20.0, 130.0, 40.0)] // right and top.
    [InlineData(10.0, null, 20.0, null, 30.0, 140.0)] // left and bottom.
    public void Insets_place_a_block_from_each_edge(double? left, double? right, double? bottom, double? top, double x, double y)
    {
        var insets = new BoxInsets(
            top is { } t ? Inset.Points(t) : null, right is { } r ? Inset.Points(r) : null,
            bottom is { } b ? Inset.Points(b) : null, left is { } l ? Inset.Points(l) : null);
        var box = Absolute(insets, 40, 20);
        var placed = Box(Paginate(box), box);

        Assert.Equal((x, y), (placed.Left, placed.Top));
    }

    [Fact]
    public void Percent_insets_are_of_the_containing_block()
    {
        var box = Absolute(new BoxInsets(Top: Inset.Percent(50), Left: Inset.Percent(25)), 10, 10);
        var placed = Box(Paginate(box), box);

        Assert.Equal((60.0, 100.0), (placed.Left, placed.Top));
    }

    [Fact]
    public void Left_and_right_insets_give_the_width_and_top_and_bottom_the_height()
    {
        var box = new ContainerBlock(new BoxStyle
        {
            Position = Positioning.Absolute,
            Insets = new BoxInsets(Inset.Points(10), Inset.Points(20), Inset.Points(30), Inset.Points(40)),
            Background = Gray,
        });
        var placed = Box(Paginate(box), box);

        Assert.Equal((60.0, 30.0, 100.0, 120.0), (placed.Left, placed.Top, placed.Width, placed.Bottom - placed.Top));
    }

    [Fact]
    public void Without_a_width_or_both_horizontal_insets_an_absolute_block_shrinks_to_fit()
    {
        var box = new ContainerBlock(
            new BoxStyle { Position = Positioning.Absolute, Insets = new BoxInsets(Right: Inset.Points(0)), Background = Gray },
            new ParagraphBlock(new Paragraph(Text).Add("Hello")));
        var placed = Box(Paginate(box), box);

        Assert.Equal(Sans.MeasureText("Hello", 8), placed.Width, 3);
        Assert.Equal(180 - placed.Width, placed.Left, 3);
    }

    [Fact]
    public void Auto_insets_keep_the_static_position()
    {
        var box = Absolute(default, 40, 20, new Thickness(5, 0, 0, 3));
        var result = Paginate(Rect(30), box);

        Assert.Equal((23.0, 55.0), (Box(result, box).Left, Box(result, box).Top));
    }

    [Fact]
    public void The_containing_block_is_the_padding_box_of_the_nearest_positioned_ancestor()
    {
        var box = Absolute(new BoxInsets(Bottom: Inset.Points(0), Right: Inset.Points(0)), 20, 10);
        var relative = new ContainerBlock(
            new BoxStyle { Position = Positioning.Relative, Border = Borders.All(2, PdfColor.Black), Margin = new Thickness(10, 0, 0, 0) },
            new ContainerBlock(new BoxStyle { Background = Gray }, new SpacerBlock(50)), box);
        var placed = Box(Paginate(Rect(10), relative), box);

        // The ancestor's border box is x 20-180, y 40-94; its padding box x 22-178, y 42-92.
        Assert.Equal((158.0, 82.0), (placed.Left, placed.Top));
    }

    [Fact]
    public void A_relative_block_is_drawn_moved_and_keeps_its_place_in_the_flow()
    {
        var moved = new ContainerBlock(new BoxStyle { Position = Positioning.Relative, Insets = new BoxInsets(Top: Inset.Points(10), Left: Inset.Points(30)), Background = Red }, new SpacerBlock(20));
        var after = new ContainerBlock(new BoxStyle { Background = Blue }, new SpacerBlock(20));
        var flow = new FlowDocument(Small).Add(moved).Add(after);

        Assert.Equal(40, Box(flow.Paginate(), after).Top, 3);
        using var bitmap = Render(flow);
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(55, 35)); // Moved 30 right and 10 down: x 50-210, y 30-50.
        Assert.NotEqual(new SKColor(255, 0, 0), bitmap.GetPixel(25, 25));
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(100, 45)); // Over the next block, which it now covers.
    }

    [Fact]
    public void A_relative_block_moves_its_absolute_children_with_it()
    {
        var box = Absolute(new BoxInsets(Top: Inset.Points(0), Left: Inset.Points(0)), 10, 10);
        var relative = new ContainerBlock(new BoxStyle { Position = Positioning.Relative, Insets = new BoxInsets(Left: Inset.Points(15), Top: Inset.Points(5)) }, Rect(20), box);
        var placed = Box(Paginate(relative), box);

        Assert.Equal((35.0, 25.0), (placed.Left, placed.Top));
    }

    [Fact]
    public void A_higher_z_index_is_painted_over_a_lower_one_and_a_negative_one_under_the_flow()
    {
        var high = Absolute(new BoxInsets(Top: Inset.Points(0), Left: Inset.Points(0)), 60, 60, color: Red, z: 2);
        var low = Absolute(new BoxInsets(Top: Inset.Points(20), Left: Inset.Points(20)), 60, 60, color: Blue, z: 1);
        var under = Absolute(new BoxInsets(Top: Inset.Points(70), Left: Inset.Points(0)), 60, 40, color: Red, z: -1);
        var flow = new FlowDocument(Small).Add(high).Add(low).Add(under).Add(Rect(100, Blue));

        using var bitmap = Render(flow);
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(70, 70)); // Where both are: the higher z-index (written first).
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(90, 90));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(30, 110)); // The flow's block covers the negative z-index.
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(30, 125)); // Below the flow's block it shows.
    }

    [Theory]
    [InlineData(null, 30.0, null, null, -30.0, 0.0)] // right moves left.
    [InlineData(null, null, 10.0, null, 0.0, -10.0)] // bottom moves up.
    [InlineData(5.0, 30.0, 10.0, 20.0, 5.0, 20.0)] // left wins over right, top over bottom.
    public void Relative_offsets_move_the_block(double? left, double? right, double? bottom, double? top, double dx, double dy)
    {
        var insets = new BoxInsets(
            top is { } t ? Inset.Points(t) : null, right is { } r ? Inset.Points(r) : null,
            bottom is { } b ? Inset.Points(b) : null, left is { } l ? Inset.Points(l) : null);
        var box = Absolute(new BoxInsets(Top: Inset.Points(0), Left: Inset.Points(0)), 10, 10);
        var relative = new ContainerBlock(new BoxStyle { Position = Positioning.Relative, Insets = insets }, Rect(20), box);
        var placed = Box(Paginate(Rect(40), relative), box);

        Assert.Equal((20 + dx, 60 + dy), (placed.Left, placed.Top));
    }

    [Fact]
    public void Relative_offsets_add_up_when_nested()
    {
        var inner = new ContainerBlock(new BoxStyle { Position = Positioning.Relative, Insets = new BoxInsets(Left: Inset.Points(20), Top: Inset.Points(10)), Background = Red }, new SpacerBlock(10));
        var outer = new ContainerBlock(new BoxStyle { Position = Positioning.Relative, Insets = new BoxInsets(Left: Inset.Points(30), Top: Inset.Points(20)), Width = 40 }, inner);

        using var bitmap = Render(new FlowDocument(Small).Add(outer));
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(72, 52)); // x 70-110, y 50-60.
        Assert.NotEqual(new SKColor(255, 0, 0), bitmap.GetPixel(52, 42));
    }

    [Fact]
    public void A_relative_percent_offset_is_of_the_containing_width()
    {
        var box = Absolute(new BoxInsets(Top: Inset.Points(0), Left: Inset.Points(0)), 10, 10);
        var relative = new ContainerBlock(new BoxStyle { Position = Positioning.Relative, Insets = new BoxInsets(Left: Inset.Percent(25)) }, Rect(20), box);
        var placed = Box(Paginate(relative), box);

        Assert.Equal(60, placed.Left, 3); // 25% of 160.
    }

    [Fact]
    public void A_top_inset_is_from_the_containing_blocks_padding_edge()
    {
        var box = Absolute(new BoxInsets(Top: Inset.Points(0), Left: Inset.Points(0)), 10, 10);
        var relative = new ContainerBlock(new BoxStyle { Position = Positioning.Relative, Border = Borders.All(4, PdfColor.Black) }, Rect(30), box);
        var placed = Box(Paginate(relative), box);

        Assert.Equal((24.0, 24.0), (placed.Left, placed.Top));
    }

    [Fact]
    public void The_static_position_is_below_the_previous_blocks_margin()
    {
        var before = new ContainerBlock(new BoxStyle { Margin = new Thickness(0, 0, 15, 0), Background = Gray }, new SpacerBlock(10));
        var box = Absolute(default, 10, 10);
        var placed = Box(Paginate(before, box), box);

        Assert.Equal(45, placed.Top, 3);
    }

    [Fact]
    public void A_z_index_auto_child_paints_in_its_ancestors_layer()
    {
        // The child (z-index auto) inside the z-index 2 box goes over the z-index 1 box that follows in the document.
        var child = Absolute(new BoxInsets(Top: Inset.Points(0), Left: Inset.Points(0)), 40, 40, color: Red);
        var parent = new ContainerBlock(new BoxStyle { Position = Positioning.Relative, ZIndex = 2, Width = 10 }, child);
        var other = Absolute(new BoxInsets(Top: Inset.Points(10), Left: Inset.Points(10)), 40, 40, color: Blue, z: 1);

        using var bitmap = Render(new FlowDocument(Small).Add(parent).Add(other));
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(45, 45));
    }

    [Fact]
    public void A_float_in_a_positioned_block_paints_in_its_layer()
    {
        var red = new ContainerBlock(new BoxStyle { Float = FloatSide.Left, Width = 40, Background = Red }, new SpacerBlock(40));
        var parent = new ContainerBlock(new BoxStyle { Position = Positioning.Relative, ZIndex = 1 }, red);
        var other = Absolute(new BoxInsets(Top: Inset.Points(10), Left: Inset.Points(10)), 40, 40, color: Blue, z: 0);

        using var bitmap = Render(new FlowDocument(Small).Add(other).Add(parent));
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(45, 45));
    }

    [Fact]
    public void A_fixed_block_is_drawn_on_every_page()
    {
        var box = new ContainerBlock(new BoxStyle { Position = Positioning.Fixed, Insets = new BoxInsets(Bottom: Inset.Points(0), Left: Inset.Points(0)), Width = 30, Background = Gray }, new SpacerBlock(10));
        var result = Paginate(box, Lines(40)); // Three pages.

        Assert.Equal([0, 1, 2], result.Placements.Where(p => p.Block == box).Select(p => p.Page));
        Assert.All(result.Placements.Where(p => p.Block == box), p => Assert.Equal((20.0, 170.0), (p.Left, p.Top)));
    }

    [Fact]
    public void An_absolute_block_on_a_later_page_is_placed_there()
    {
        var box = Absolute(new BoxInsets(Top: Inset.Points(5), Right: Inset.Points(5)), 20, 10);
        var relative = new ContainerBlock(new BoxStyle { Position = Positioning.Relative, BreakInside = PageBreakInside.Avoid, Background = Gray }, new SpacerBlock(30), box);
        var placed = Box(Paginate(Rect(150), relative), box);

        Assert.Equal((1, 25.0, 155.0), (placed.Page, placed.Top, placed.Left));
    }

    [Fact]
    public void A_bottom_inset_places_the_block_on_the_page_its_containing_block_ends_on()
    {
        var box = Absolute(new BoxInsets(Bottom: Inset.Points(0), Left: Inset.Points(0)), 20, 10);
        var relative = new ContainerBlock(new BoxStyle { Position = Positioning.Relative }, new ContainerBlock(new BoxStyle { Background = Gray }, Lines(20)), box);
        var placed = Box(Paginate(relative), box);

        Assert.Equal((1, 50.0), (placed.Page, placed.Top)); // The 20 lines end at 60 on page 2.
    }

    [Fact]
    public void Absolute_children_do_not_count_in_a_containers_intrinsic_width()
    {
        var container = new ContainerBlock(null, new ParagraphBlock(new Paragraph(Text).Add("ab")), Absolute(default, 150, 10));

        Assert.Equal(Sans.MeasureText("ab", 8), Paginator.MeasureIntrinsic(container).Max, 3);
    }

    private static ContainerBlock Absolute(BoxInsets insets, double width, double height, Thickness margin = default, PdfColor? color = null, int? z = null) =>
        new(new BoxStyle { Position = Positioning.Absolute, Insets = insets, Width = width, Margin = margin, Background = color ?? Gray, ZIndex = z }, new SpacerBlock(height));

    private static ContainerBlock Rect(double height, PdfColor? color = null) => new(new BoxStyle { Background = color ?? Gray }, new SpacerBlock(height));

    private static ParagraphBlock Lines(int count) =>
        new(new Paragraph(Text).Add(string.Join("\n", Enumerable.Range(1, count).Select(i => $"L{i}"))));

    private static PaginationResult Paginate(params Block[] blocks)
    {
        var flow = new FlowDocument(Small);
        foreach (var block in blocks)
        {
            flow.Add(block);
        }

        return flow.Paginate();
    }

    private static Placement Box(PaginationResult result, Block block) => result.Placements.First(p => p.Block == block);

    private static SKBitmap Render(FlowDocument flow) => Conversion.ToImage(flow.Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));
}

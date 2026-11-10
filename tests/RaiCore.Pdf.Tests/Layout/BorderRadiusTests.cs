using System.Text;
using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Rounded corners (CSS <c>border-radius</c>): resolving radii, scaling overlapping ones, slicing, and rendered pixels.
/// Render tests use a 200 x 200 pt page with 20 pt margins at 72 dpi, so a pixel is a point and a box placed first
/// starts at (20, 20) from the top left.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class BorderRadiusTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));
    private static readonly PdfColor Red = PdfColor.FromRgb(255, 0, 0);
    private static readonly PdfColor Blue = PdfColor.FromRgb(0, 0, 255);
    private static readonly PdfImage Quadrants = PdfImage.Load(Fixtures.Path(Path.Combine("Images", "rgb.jpg")));

    // ---- resolving ----

    [Fact]
    public void Percentages_are_of_the_width_horizontally_and_of_the_height_vertically()
    {
        var radii = new CornerRadii(
            CornerRadius.Percentage(10), CornerRadius.Elliptical(30, 5), CornerRadius.Circular(8),
            new CornerRadius(new RadiusLength(4, 10), RadiusLength.FromPercent(20)));

        var corners = radii.Resolve(200, 100);

        Assert.Equal((20, 10), corners[0]);
        Assert.Equal((30, 5), corners[1]);
        Assert.Equal((8, 8), corners[2]);
        Assert.Equal((24, 20), corners[3]); // 4 pt plus 10% of the width; 20% of the height.
    }

    [Fact]
    public void A_corner_with_either_radius_zero_or_negative_is_square()
    {
        var corners = new CornerRadii(CornerRadius.Elliptical(10, 0), CornerRadius.Elliptical(-5, 10), CornerRadius.Circular(5), CornerRadius.Square)
            .Resolve(100, 100);

        Assert.Equal((0, 0), corners[0]);
        Assert.Equal((0, 0), corners[1]);
        Assert.True(corners.Rounded(2));
        Assert.False(corners.Rounded(3));
        Assert.True(new CornerRadii(CornerRadius.Elliptical(10, 0), CornerRadius.Square, CornerRadius.Square, CornerRadius.Square).IsSquare);
        Assert.False(CornerRadii.All(CornerRadius.Percentage(1)).IsSquare);
        Assert.True(CornerRadii.None.IsSquare);
    }

    [Fact]
    public void Overlapping_radii_are_all_scaled_by_the_smallest_side_ratio()
    {
        // 60 + 60 on a 100 x 50 box: the sides allow 100/120 and 50/120; the smaller factor applies to every radius.
        var corners = CornerRadii.All(60).Resolve(100, 50);
        Assert.Equal(25, corners[0].X, 9);
        Assert.Equal(25, corners[2].Y, 9);

        // Only the left side overflows (40 + 80 > 100): f = 100/120, the right corners shrink too.
        var mixed = new CornerRadii(CornerRadius.Elliptical(10, 40), CornerRadius.Circular(10), CornerRadius.Circular(10), CornerRadius.Elliptical(10, 80))
            .Resolve(200, 100);
        Assert.Equal(40 * 100.0 / 120, mixed[0].Y, 9);
        Assert.Equal(80 * 100.0 / 120, mixed[3].Y, 9);
        Assert.Equal(10 * 100.0 / 120, mixed[1].X, 9);

        // Radii that fit are left alone, even when they add up to exactly the side.
        Assert.Equal((50, 50), CornerRadii.All(50).Resolve(100, 100)[1]);
    }

    [Fact]
    public void Fragments_keep_only_their_corners_and_fit_them_again()
    {
        var whole = CornerRadii.All(30).Resolve(100, 400);

        var first = whole.Fragment(top: true, bottom: false, 100, 100);
        Assert.Equal((30, 30), first[0]);
        Assert.Equal((30, 30), first[1]);
        Assert.False(first.Rounded(2) || first.Rounded(3));

        var last = whole.Fragment(top: false, bottom: true, 100, 40);
        Assert.False(last.Rounded(0) || last.Rounded(1));
        Assert.Equal(30, last[2].X, 9);

        var tiny = whole.Fragment(top: true, bottom: false, 100, 20); // A 20 pt fragment cannot hold 30 pt curves.
        Assert.Equal(20, tiny[0].Y, 9);

        var middle = whole.Fragment(top: false, bottom: false, 100, 100);
        Assert.True(middle.IsSquare);

        var line = whole.Fragment(true, true, 100, 100, left: false);
        Assert.False(line.Rounded(0) || line.Rounded(3));
        Assert.True(line.Rounded(1) && line.Rounded(2));
    }

    [Fact]
    public void Inner_radii_are_the_outer_ones_minus_the_insets_at_least_zero()
    {
        var corners = new CornerRadii(CornerRadius.Elliptical(20, 10), CornerRadius.Circular(5), CornerRadius.Circular(20), CornerRadius.Circular(20))
            .Resolve(200, 200)
            .Inset(top: 4, right: 8, bottom: 2, left: 6);

        Assert.Equal((14, 6), corners[0]);
        Assert.Equal((0, 0), corners[1]); // 5 - 8 is negative: square, in both directions.
        Assert.Equal((12, 18), corners[2]);
        Assert.Equal((14, 18), corners[3]);
    }

    // ---- operators ----

    [Fact]
    public void Square_boxes_keep_their_rectangles_and_trapezoids()
    {
        var ops = Ops(new BoxStyle { Background = Red, Border = Borders.All(2, PdfColor.Black), Width = 100, Height = 50 });

        Assert.Contains(ops, o => o.EndsWith(" re", StringComparison.Ordinal));
        Assert.DoesNotContain(ops, o => o.EndsWith(" c", StringComparison.Ordinal));
    }

    [Fact]
    public void Rounded_backgrounds_and_borders_are_curved_paths()
    {
        var ops = Ops(new BoxStyle { Background = Red, Border = Borders.All(2, PdfColor.Black), BorderRadius = CornerRadii.All(10), Width = 100, Height = 50 });

        Assert.DoesNotContain(ops, o => o.EndsWith(" re", StringComparison.Ordinal));
        Assert.Equal(4 + 8, ops.Count(o => o.EndsWith(" c", StringComparison.Ordinal))); // The background, and the border's two edges.
        Assert.Equal(2, ops.Count(o => o == "f")); // The background, and the border ring in one fill.
        Assert.DoesNotContain("W n", ops); // All four sides alike: no need to split the ring.
    }

    [Fact]
    public void Sides_of_different_colors_are_filled_separately_split_on_the_diagonals()
    {
        var borders = new Borders(new BorderSide(2, Red), new BorderSide(2, PdfColor.Black), new BorderSide(2, Red), new BorderSide(2, PdfColor.Black));
        var ops = Ops(new BoxStyle { Border = borders, BorderRadius = CornerRadii.All(10), Width = 100, Height = 50 });

        Assert.Equal(2, ops.Count(o => o == "W n"));
        Assert.Equal(2, ops.Count(o => o == "f"));
    }

    [Theory]
    [InlineData(BorderStyle.Dashed)]
    [InlineData(BorderStyle.Dotted)]
    [InlineData(BorderStyle.Double)]
    [InlineData(BorderStyle.Groove)]
    [InlineData(BorderStyle.Ridge)]
    [InlineData(BorderStyle.Inset)]
    [InlineData(BorderStyle.Outset)]
    public void Every_style_draws_along_the_curves(BorderStyle style)
    {
        var ops = Ops(new BoxStyle { Border = Borders.All(6, PdfColor.Black, style), BorderRadius = CornerRadii.All(20), Width = 100, Height = 50 });

        Assert.Contains(ops, o => o.EndsWith(" c", StringComparison.Ordinal));
        Assert.Equal(ops.Count(o => o == "q"), ops.Count(o => o == "Q"));
    }

    [Fact]
    public void Dashed_rounded_sides_get_whole_dashes_with_the_corner_dashes_centered_on_the_diagonals()
    {
        // 2 pt dashes, so dashes of 6: the visible length of each side runs from diagonal to diagonal around the curves,
        // plus half a dash at each end, so that the dashes at the corners are centered on the diagonals.
        var ops = Ops(new BoxStyle { Border = Borders.All(2, PdfColor.Black, BorderStyle.Dashed), BorderRadius = CornerRadii.All(10), Width = 100, Height = 50 });

        var dashes = ops.Where(o => o.EndsWith(" d", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, dashes.Count);
        Assert.All(dashes, d => Assert.StartsWith("[6 ", d, StringComparison.Ordinal));
        Assert.Equal(4, ops.Count(o => o == "S"));

        // The top's center line: from the left side's tangent point around the top-left curve, to the top-right one.
        Assert.Contains("21 170 m", ops);
        Assert.Contains("110 179 l", ops);

        // Each side's share of the ring reaches a width past the diagonals into the identical neighbors, so that the
        // halves of the corner dashes overlap instead of meeting in a seam.
        Assert.Contains("18 180 m", ops);
    }

    [Fact]
    public void Corner_dashes_and_dots_are_centered_on_the_diagonal()
    {
        // A 30 pt radius around (50, 50) with 4 pt dashes (12 long): the corner dash spans 6 pt each way along the
        // center line (radius 28), so 9 pt from the diagonal toward the top is a gap.
        using var dashed = Render(new ContainerBlock(new BoxStyle { Border = Borders.All(4, PdfColor.Black, BorderStyle.Dashed), BorderRadius = CornerRadii.All(30), Width = 160, Height = 100 }));
        Assert.True(Dark(dashed.GetPixel(30, 30)), "The corner dash on the diagonal.");
        Assert.False(Dark(dashed.GetPixel(37, 25)), "A gap 9 pt along the curve toward the top.");
        Assert.False(Dark(dashed.GetPixel(25, 37)), "And toward the left.");

        // 6 pt dots: one centered on the diagonal (center line radius 27), a gap 4.5 pt along the curve either way.
        using var dotted = Render(new ContainerBlock(new BoxStyle { Border = Borders.All(6, PdfColor.Black, BorderStyle.Dotted), BorderRadius = CornerRadii.All(30), Width = 160, Height = 100 }));
        Assert.True(Dark(dotted.GetPixel(30, 30)), "The corner dot on the diagonal.");
        Assert.False(Dark(dotted.GetPixel(34, 28)));
        Assert.False(Dark(dotted.GetPixel(28, 34)));
    }

    [Fact]
    public void Dashes_start_on_the_diagonal_toward_a_wider_side()
    {
        // A 2 pt dashed top next to a 12 pt solid left: the diagonal from (20, 20) through (32, 22) is shallow, and the
        // top's first dash starts where it crosses the curve (about (41, 23)), not where a 45 degree line would.
        var borders = new Borders(new BorderSide(2, PdfColor.Black, BorderStyle.Dashed), BorderSide.None, BorderSide.None, new BorderSide(12, Red));
        using var page = Render(new ContainerBlock(new BoxStyle { Border = borders, BorderRadius = CornerRadii.All(30), Width = 160, Height = 100 }));

        Assert.True(Dark(page.GetPixel(40, 22)) && !IsRed(page.GetPixel(40, 22)), "The first dash, right at the diagonal.");
        Assert.True(IsRed(page.GetPixel(36, 24)), "The left side below the diagonal.");
    }

    // ---- rendered ----

    [Fact]
    public void Rounded_background_is_clear_outside_the_curve()
    {
        // A 20 pt radius: the corner's center is at (40, 40).
        using var page = Render(new ContainerBlock(new BoxStyle { Background = Red, BorderRadius = CornerRadii.All(20), Width = 160, Height = 100 }));

        Assert.True(IsWhite(page.GetPixel(21, 21)), "Outside the curve.");
        Assert.True(IsWhite(page.GetPixel(24, 24)));
        Assert.True(IsRed(page.GetPixel(30, 30)), "Inside the curve (14 pt from its center).");
        Assert.True(IsRed(page.GetPixel(40, 21)), "Where the curve meets the top.");
        Assert.True(IsWhite(page.GetPixel(178, 118)), "The bottom-right corner too.");
        Assert.True(IsRed(page.GetPixel(100, 70)));
    }

    [Fact]
    public void Elliptical_corners_follow_both_radii()
    {
        // 60 pt across and 20 pt down: the top-left ellipse's center is at (80, 40).
        using var page = Render(new ContainerBlock(new BoxStyle { Background = Red, BorderRadius = CornerRadii.All(CornerRadius.Elliptical(60, 20)), Width = 160, Height = 100 }));

        Assert.True(IsWhite(page.GetPixel(30, 24)), "Outside the ellipse (inside a 20 pt circle).");
        Assert.True(IsRed(page.GetPixel(60, 36)));
        Assert.True(IsRed(page.GetPixel(21, 60)), "Below the curve, the side is straight.");
        Assert.True(IsWhite(page.GetPixel(45, 21)));
    }

    [Fact]
    public void Percentages_render_as_an_ellipse()
    {
        using var page = Render(new ContainerBlock(new BoxStyle { Background = Red, BorderRadius = CornerRadii.All(CornerRadius.Percentage(50)), Width = 160, Height = 100 }));

        Assert.True(IsRed(page.GetPixel(100, 70)));
        Assert.True(IsRed(page.GetPixel(22, 70)), "The ellipse touches the left side in the middle.");
        Assert.True(IsWhite(page.GetPixel(30, 30)));
        Assert.True(IsWhite(page.GetPixel(30, 110)));
    }

    [Fact]
    public void Overlapping_radii_make_a_pill()
    {
        // 200 pt radii on a 160 x 100 box scale down to 50 pt: half circles at the ends, straight top and bottom.
        using var page = Render(new ContainerBlock(new BoxStyle { Background = Red, BorderRadius = CornerRadii.All(200), Width = 160, Height = 100 }));

        Assert.True(IsRed(page.GetPixel(23, 70)));
        Assert.True(IsWhite(page.GetPixel(25, 30)));
        Assert.True(IsRed(page.GetPixel(100, 21)));
        Assert.True(IsRed(page.GetPixel(100, 118)));
        Assert.True(IsWhite(page.GetPixel(176, 30)));
    }

    [Fact]
    public void Solid_borders_follow_the_curve()
    {
        // A 6 pt border with a 20 pt radius: along the diagonal, the ring is between 20 and 14 pt from (40, 40).
        using var page = Render(new ContainerBlock(new BoxStyle
        {
            Background = Red, Border = Borders.All(6, PdfColor.Black), BorderRadius = CornerRadii.All(20), Width = 160, Height = 100,
        }));

        Assert.True(IsWhite(page.GetPixel(23, 23)));
        var ring = 40 - (17 / Math.Sqrt(2)); // 17 pt from the center, on the diagonal.
        Assert.True(Dark(page.GetPixel((int)ring, (int)ring)), "The border on the curve.");
        Assert.True(IsRed(page.GetPixel(32, 32)), "The background inside the inner curve (11 pt from the center).");
        Assert.True(Dark(page.GetPixel(100, 22)) && Dark(page.GetPixel(22, 70)));
    }

    [Fact]
    public void Sides_of_different_colors_meet_on_the_corner_diagonal()
    {
        var borders = new Borders(new BorderSide(10, Red), new BorderSide(10, Red), new BorderSide(10, Blue), new BorderSide(10, Blue));
        using var page = Render(new ContainerBlock(new BoxStyle { Border = borders, BorderRadius = CornerRadii.All(30), Width = 160, Height = 100 }));

        // At the top-left corner (center (50, 50)), the ring just above the diagonal is the top's, just below the left's.
        Assert.True(IsRed(page.GetPixel(34, 28)), "Top side of the diagonal.");
        Assert.True(IsBlue(page.GetPixel(28, 34)), "Left side of the diagonal.");

        // Deeper than the width, where the curve's ring still crosses the diagonal (21 pt from the center).
        Assert.True(IsRed(page.GetPixel(36, 33)), "Top side, 13 pt deep.");
        Assert.True(IsBlue(page.GetPixel(33, 36)), "Left side, 13 pt in.");
        Assert.True(IsRed(page.GetPixel(100, 25)) && IsBlue(page.GetPixel(25, 70)));
    }

    [Fact]
    public void A_side_without_width_lets_its_neighbors_taper_around_the_corner()
    {
        var borders = new Borders(new BorderSide(8, Red), BorderSide.None, BorderSide.None, BorderSide.None);
        using var page = Render(new ContainerBlock(new BoxStyle { Border = borders, BorderRadius = CornerRadii.All(30), Width = 160, Height = 100 }));

        Assert.True(IsRed(page.GetPixel(100, 23)));
        Assert.True(IsRed(page.GetPixel(29, 32)), "The top border continues down the curve.");
        Assert.True(IsWhite(page.GetPixel(21, 60)), "No left border.");
        Assert.True(IsWhite(page.GetPixel(100, 60)));
    }

    [Fact]
    public void Double_rounded_borders_have_two_lines_along_the_sides()
    {
        using var page = Render(new ContainerBlock(new BoxStyle { Border = Borders.All(9, PdfColor.Black, BorderStyle.Double), BorderRadius = CornerRadii.All(20), Width = 160, Height = 100 }));

        // 3 pt lines with a 3 pt gap, as without radii, also on the curve (on the diagonal from the center (40, 40)).
        Assert.Equal([true, false, true], new[] { 21, 24, 27 }.Select(y => Dark(page.GetPixel(100, y))));
        double Diagonal(double distance) => 40 - (distance / Math.Sqrt(2));
        Assert.Equal([true, false, true], new[] { 18.5, 15.5, 12.5 }.Select(d => Dark(page.GetPixel((int)Diagonal(d), (int)Diagonal(d)))));
        Assert.True(IsWhite(page.GetPixel(22, 22)));
    }

    [Fact]
    public void Dashed_and_dotted_rounded_borders_put_a_dash_or_dot_on_each_corner()
    {
        double Diagonal(double distance) => 40 - (distance / Math.Sqrt(2));
        foreach (var style in new[] { BorderStyle.Dashed, BorderStyle.Dotted })
        {
            using var page = Render(new ContainerBlock(new BoxStyle { Border = Borders.All(6, PdfColor.Black, style), BorderRadius = CornerRadii.All(20), Width = 160, Height = 100 }));

            var corner = (int)Diagonal(17);
            Assert.True(Dark(page.GetPixel(corner, corner)), $"{style}: on the top-left corner's diagonal.");
            Assert.True(Dark(page.GetPixel(200 - corner - 1, 140 - corner - 1)), $"{style}: on the bottom-right corner's.");
            Assert.True(Runs(Row(page, 23, 45, 155)) >= 5, $"{style}: broken along the top.");
            Assert.True(IsWhite(page.GetPixel(22, 22)), $"{style}: nothing outside the curve.");
        }
    }

    [Fact]
    public void A_box_split_across_pages_is_rounded_only_at_its_own_top_and_bottom()
    {
        // Three 70 pt children: two on the first page, where the box continues to the content bottom (180), one on the second.
        var box = new ContainerBlock(
            new BoxStyle { Background = Red, BorderRadius = CornerRadii.All(20) },
            Enumerable.Range(0, 3).Select(_ => new ContainerBlock(new BoxStyle { Height = 70 })));
        var pdf = new FlowDocument(Small).Add(box).Render().ToArray();
        using var first = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        using var second = Conversion.ToImage(pdf, page: 1, options: new RenderOptions(Dpi: 72));

        Assert.True(IsWhite(first.GetPixel(21, 21)), "Rounded at the top of the first fragment.");
        Assert.True(IsRed(first.GetPixel(21, 178)), "Square where it continues.");
        Assert.True(IsRed(first.GetPixel(178, 178)));
        Assert.True(IsRed(second.GetPixel(21, 21)), "Square where it continues from.");
        Assert.True(IsWhite(second.GetPixel(21, 88)), "Rounded at the bottom of the last fragment.");
        Assert.True(IsWhite(second.GetPixel(178, 88)));
        Assert.True(IsRed(second.GetPixel(100, 88)));
    }

    [Fact]
    public void Image_blocks_are_clipped_to_their_rounded_content_box()
    {
        // A 100 x 80 image in a box with a 4 pt border and 4 pt padding: the content box's radius is 24 - 8 = 16.
        var style = new BoxStyle { Border = Borders.All(4, PdfColor.Black), Padding = Thickness.All(4), BorderRadius = CornerRadii.All(24) };
        using var page = Render(new ImageBlock(Quadrants, 100, 80, style));

        Assert.True(IsWhite(page.GetPixel(31, 31)), "In the padding, outside the content box's curve (center (44, 44), radius 16).");
        Assert.True(page.GetPixel(40, 40).Red > 200 && page.GetPixel(40, 40).Green < 80, "The red quadrant inside it.");
        Assert.True(page.GetPixel(29, 60).Red > 200, "The straight left side of the image.");
    }

    [Fact]
    public void Inline_images_are_clipped_to_their_corners()
    {
        var paragraph = new Paragraph(Text).AddImage(Quadrants, 80, 80, borderRadius: CornerRadii.All(CornerRadius.Percentage(50)));
        using var page = Render(new ParagraphBlock(paragraph));

        // A circle: its top is where its middle column starts, at the top of the line; clear in the corner, red inside.
        var top = Enumerable.Range(20, 40).First(y => !IsWhite(page.GetPixel(59, y)));
        Assert.True(IsWhite(page.GetPixel(23, top + 3)), "The image's corner is clipped away.");
        Assert.True(page.GetPixel(40, top + 20).Red > 200, "Inside the circle.");
    }

    [Fact]
    public void Tables_with_collapsed_borders_ignore_the_radius()
    {
        var table = new TableBlock(new BoxStyle { Background = Red, BorderRadius = CornerRadii.All(30), Width = 160 }) { BorderCollapse = BorderCollapse.Collapse }
            .Add(new TableRow(new TableCell(new Paragraph(Text).Add(string.Join('\n', Enumerable.Range(0, 6).Select(i => $"{i}"))))));
        using var page = Render(table);

        Assert.True(IsRed(page.GetPixel(21, 21)));

        var separate = new TableBlock(new BoxStyle { Background = Red, BorderRadius = CornerRadii.All(30), Width = 160 })
            .Add(new TableRow(new TableCell(new Paragraph(Text).Add(string.Join('\n', Enumerable.Range(0, 6).Select(i => $"{i}"))))));
        using var rounded = Render(separate);
        Assert.True(IsWhite(rounded.GetPixel(21, 21)));
    }

    [Fact]
    public void Table_cells_and_flex_items_are_rounded_too()
    {
        var cell = new BoxStyle { Background = Red, BorderRadius = CornerRadii.All(15) };
        var table = new TableBlock(new BoxStyle { Width = 160 }) { HorizontalSpacing = 0, VerticalSpacing = 0 }
            .Add(new TableRow(new TableCell(new Paragraph(Text).Add("a\nb\nc\nd")) { Style = cell }));
        using var page = Render(table);
        Assert.True(IsWhite(page.GetPixel(21, 21)));
        Assert.True(IsRed(page.GetPixel(100, 30)));

        var flex = new FlexBlock(new BoxStyle { Width = 160 });
        flex.Add(new FlexItem(new ContainerBlock(new BoxStyle { Background = Red, BorderRadius = CornerRadii.All(15), Height = 60 })) { Grow = 1 });
        using var row = Render(flex);
        Assert.True(IsWhite(row.GetPixel(21, 21)));
        Assert.True(IsRed(row.GetPixel(100, 50)));
    }

    [Fact]
    public void Background_clip_insets_the_background_and_its_corners()
    {
        // Padding 10 and a 30 pt radius: content-box clipping leaves the padding clear and rounds the inner corner by 20.
        var style = new BoxStyle { Background = Red, Padding = Thickness.All(10), BorderRadius = CornerRadii.All(30), Width = 160, Height = 100 };
        using var border = Render(new ContainerBlock(style));
        using var content = Render(new ContainerBlock(style with { BackgroundClip = BackgroundClip.ContentBox }));

        Assert.True(IsRed(border.GetPixel(100, 25)));
        Assert.True(IsWhite(content.GetPixel(100, 25)), "The padding is clear.");
        Assert.True(IsRed(content.GetPixel(100, 35)));
        Assert.True(IsWhite(content.GetPixel(33, 33)), "Outside the inner curve (center (50, 50), radius 20).");
        Assert.True(IsRed(content.GetPixel(40, 40)));
    }

    [Fact]
    public void Rounded_inline_elements_are_rounded_on_their_first_and_last_lines()
    {
        var box = new BoxStyle { Background = Red, BorderRadius = CornerRadii.All(4) };
        var paragraph = new Paragraph(Text).Add("a ").BeginInline(box, Text).Add("rounded").EndInline().Add(" b");
        var ops = Uncompressed(new ParagraphBlock(paragraph));

        Assert.Contains(" c\n", ops, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BorderStyle.Dashed)]
    [InlineData(BorderStyle.Dotted)]
    public void Translucent_dashes_and_dots_are_painted_once_where_identical_sides_meet(BorderStyle style)
    {
        // Half-transparent blue over a red background: one layer is (128, 0, 128); two would be (64, 0, 191). The
        // corner's dash or dot is centered on the diagonal (the ring's middle is 17 pt from the center (40, 40)), where
        // both sides draw it.
        var blue = new PdfColor(0, 0, 1, 0.5);
        using var page = Render(new ContainerBlock(new BoxStyle
        {
            Background = Red, Border = Borders.All(6, blue, style), BorderRadius = CornerRadii.All(20), Width = 160, Height = 100,
        }));

        var corner = (int)(40 - (17 / Math.Sqrt(2)));
        var pixel = page.GetPixel(corner, corner);
        Assert.InRange(pixel.Red, 110, 145);
        Assert.InRange(pixel.Blue, 110, 145);
        var side = page.GetPixel(22, 70); // Somewhere along the left side, for comparison: a dash or a dot, or the background.
        Assert.True(side.Red > 110, "Never darker than one layer.");
    }

    [Fact]
    public void An_opacity_group_keeps_the_rounded_corners()
    {
        // opacity: 0.5 on a rounded red box: one transparency group, so the corners stay clear and the inside is pink.
        using var page = Render(new ContainerBlock(new BoxStyle
        {
            Background = Red, Border = Borders.All(4, Red), BorderRadius = CornerRadii.All(20), Width = 160, Height = 100, Opacity = 0.5,
        }));

        Assert.True(IsWhite(page.GetPixel(22, 22)));
        var inside = page.GetPixel(100, 70);
        Assert.InRange(inside.Green, 115, 140);
        var border = page.GetPixel(100, 22); // The border over the background, flattened in the group first: still one layer.
        Assert.InRange(border.Green, 115, 140);
    }

    // ---- helpers ----

    /// <summary>The page content of a single box, as operators.</summary>
    private static List<string> Ops(BoxStyle style)
    {
        var text = Uncompressed(new ContainerBlock(style));
        var start = text.IndexOf("stream\n", StringComparison.Ordinal) + 7;
        var end = text.IndexOf("endstream", start, StringComparison.Ordinal);
        return [.. text[start..end].Split('\n', StringSplitOptions.RemoveEmptyEntries)];
    }

    private static string Uncompressed(Block block)
    {
        var document = new FlowDocument(Small).Add(block).Render();
        document.CompressContent = false;
        return Encoding.Latin1.GetString(document.ToArray());
    }

    private static SKBitmap Render(Block block) =>
        Conversion.ToImage(new FlowDocument(Small).Add(block).Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));

    private static List<SKColor> Row(SKBitmap page, int y, int x0, int x1) => [.. Enumerable.Range(x0, x1 - x0).Select(x => page.GetPixel(x, y))];

    private static bool Dark(SKColor color) => color.Red + color.Green + color.Blue < 3 * 128;

    private static bool IsWhite(SKColor color) => color.Red > 240 && color.Green > 240 && color.Blue > 240;

    private static bool IsRed(SKColor color) => color.Red > 200 && color.Green < 60 && color.Blue < 60;

    private static bool IsBlue(SKColor color) => color.Blue > 200 && color.Red < 60 && color.Green < 60;

    /// <summary>The number of runs of dark and light pixels.</summary>
    private static int Runs(List<SKColor> pixels) => 1 + pixels.Zip(pixels.Skip(1)).Count(p => Dark(p.First) != Dark(p.Second));
}

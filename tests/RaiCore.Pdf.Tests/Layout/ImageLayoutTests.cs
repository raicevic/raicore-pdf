using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>Uses a 200 x 200 pt page with 20 pt margins: the content area is 160 x 160 pt starting at (20, 20).</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class ImageLayoutTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));

    // 64 x 48 px with red / green / blue / yellow quadrants.
    private static readonly PdfImage Quadrants = PdfImage.Load(Fixtures.Path(Path.Combine("Images", "rgb.jpg")));

    [Fact]
    public void Natural_size_is_96_pixels_per_inch()
    {
        var placement = Single(new ImageBlock(Quadrants));

        Assert.Equal(48 * 0.75, placement.Bottom - placement.Top, 6); // 48 px = 36 pt
    }

    [Theory]
    [InlineData(100.0, null, 75.0)] // Width only: height follows the 4:3 aspect ratio.
    [InlineData(null, 30.0, 30.0)]
    [InlineData(100.0, 20.0, 20.0)] // Both: stretched.
    [InlineData(400.0, null, 120.0)] // Wider than the 160 pt content: scaled down to 160 x 120.
    public void Requested_sizes_keep_the_aspect_ratio_and_fit_the_width(double? width, double? height, double expectedHeight)
    {
        var placement = Single(new ImageBlock(Quadrants, width, height));

        Assert.Equal(expectedHeight, placement.Bottom - placement.Top, 6);
    }

    [Fact]
    public void Images_taller_than_the_page_are_scaled_to_fit()
    {
        // 50 x 1000 pt fits the width but not the 160 pt page height: both sides scale by 0.16.
        var placement = Single(new ImageBlock(Quadrants, width: 50, height: 1000));

        Assert.Equal(160, placement.Bottom - placement.Top, 6);
    }

    [Fact]
    public void Images_move_to_the_next_page_whole()
    {
        var image = new ImageBlock(Quadrants, width: 80); // 60 pt tall
        var flow = new FlowDocument(Small).Add(Lines(12)).Add(image); // 120 pt used, 40 left
        var placement = flow.Paginate().Placements.Single(p => p.Block == image);

        Assert.Equal((1, 20.0), (placement.Page, placement.Top));
    }

    [Theory]
    [InlineData(TextAlignment.Left, 20)]
    [InlineData(TextAlignment.Center, 80)]
    [InlineData(TextAlignment.Right, 140)]
    public void Block_images_can_be_aligned(TextAlignment alignment, int expectedLeft)
    {
        var flow = new FlowDocument(Small).Add(new ImageBlock(Quadrants, width: 40) { Alignment = alignment });
        using var bitmap = Render(flow);

        // The red quadrant starts at the image's left edge, at the top of the content area.
        AssertRed(bitmap.GetPixel(expectedLeft + 3, 23), "inside");
        Assert.False(IsRed(bitmap.GetPixel(expectedLeft - 3, 23)), "left of the image");
    }

    [Fact]
    public void Inline_images_sit_on_the_baseline_and_make_the_line_taller()
    {
        var paragraph = new Paragraph(Text).Add("Logo ").AddImage(Quadrants, 32, 24).Add(" text");
        var line = paragraph.Layout(160).Lines.Single();

        var image = Assert.Single(line.Images);
        Assert.Equal(Sans.MeasureText("Logo ", 8), image.X, 3);
        Assert.Equal(24, line.Baseline, 6); // The image is the tallest thing above the baseline.
        Assert.True(line.Height > 24); // Plus the text's descent below the baseline.
        Assert.Equal(Sans.MeasureText("Logo ", 8) + 32 + Sans.MeasureText(" text", 8), line.Width, 3);
    }

    [Fact]
    public void Lines_break_before_an_inline_image_that_does_not_fit()
    {
        var paragraph = new Paragraph(Text).Add("Some words ").AddImage(Quadrants, 150, 20);
        var lines = paragraph.Layout(160).Lines;

        Assert.Equal(2, lines.Count);
        Assert.Empty(lines[0].Images);
        Assert.Single(lines[1].Images);
    }

    [Fact]
    public void Inline_images_are_drawn_where_laid_out()
    {
        var flow = new FlowDocument(Small).Add(new ParagraphBlock(new Paragraph(Text).Add("Logo ").AddImage(Quadrants, 32, 24)));
        var line = new Paragraph(Text).Add("Logo ").AddImage(Quadrants, 32, 24).Layout(160).Lines[0];
        using var bitmap = Render(flow);

        var left = 20 + (int)Math.Round(line.Images[0].X);
        AssertRed(bitmap.GetPixel(left + 4, 20 + 3), "top-left quadrant of the inline image");
        Assert.True(bitmap.GetPixel(left + 28, 20 + 21).Blue < 100 && bitmap.GetPixel(left + 28, 20 + 21).Red > 200, "bottom-right quadrant is yellow");
    }

    [Fact]
    public void Images_in_boxes_get_borders_and_padding()
    {
        var image = new ImageBlock(Quadrants, width: 64, style: new BoxStyle { Padding = Thickness.All(4), Border = Borders.All(2, PdfColor.Black) });
        var placements = new FlowDocument(Small).Add(image).Paginate().Placements.Where(p => p.Block == image).ToList();

        var box = placements.Single(p => p.FirstLine == -1 && p.Top == 20);
        var content = placements.Single(p => p.Top == 26);
        Assert.Equal(48, content.Bottom - content.Top, 6);
        Assert.Equal(48 + 12, placements.Max(p => p.Bottom) - box.Top, 6);
    }

    [Fact]
    public void A_bordered_image_box_shrinks_to_the_image()
    {
        // 40 pt image + 4 pt padding + 2 pt border on each side: the box spans x = 20..72.
        var image = new ImageBlock(Quadrants, width: 40, style: new BoxStyle { Padding = Thickness.All(4), Border = Borders.All(2, PdfColor.Black) });
        using var bitmap = Render(new FlowDocument(Small).Add(image));

        Assert.True(bitmap.GetPixel(71, 40).Red < 60, "right border right after the image and padding");
        Assert.Equal(SKColors.White, bitmap.GetPixel(90, 40)); // Nothing beyond it.
    }

    private static Placement Single(ImageBlock block) =>
        new FlowDocument(Small).Add(block).Paginate().Placements.Single(p => p.Block == block);

    private static ParagraphBlock Lines(int count) =>
        new(new Paragraph(Text).Add(string.Join("\n", Enumerable.Range(1, count).Select(i => $"Line {i}"))));

    private static SKBitmap Render(FlowDocument flow) =>
        Conversion.ToImage(flow.Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));

    private static bool IsRed(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;

    private static void AssertRed(SKColor c, string where) => Assert.True(IsRed(c), $"{where}: {c}");
}

using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Clipping overflow and fixed heights across pages. Uses a 200 x 200 pt page with 20 pt margins, rendered at 72 dpi
/// (1 pt per pixel); blocks start at (20, 20) and are 160 pt wide.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class OverflowLayoutTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));
    private static readonly SKColor Red = new(255, 0, 0);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Content_below_a_clipping_box_is_hidden(bool clip)
    {
        var box = new ContainerBlock(new BoxStyle { Height = 30, ClipOverflow = clip }, RedRect(60));
        using var bitmap = Render(new FlowDocument(Small).Add(box));

        Assert.Equal(Red, bitmap.GetPixel(100, 40));
        Assert.Equal(clip ? new SKColor(255, 255, 255) : Red, bitmap.GetPixel(100, 60));
    }

    [Fact]
    public void Content_is_clipped_to_the_padding_box()
    {
        // A child pulled 16 pt left reaches through the padding (10 pt) into the border (5 pt) and beyond.
        var child = new ContainerBlock(new BoxStyle { Margin = new Thickness(0, 0, 0, -16), Background = PdfColor.FromRgb(255, 0, 0) }, new SpacerBlock(20));
        var box = new ContainerBlock(new BoxStyle { ClipOverflow = true, Padding = Thickness.All(10), Border = Borders.All(5, PdfColor.FromRgba(0, 0, 0, 0)) }, child);
        using var bitmap = Render(new FlowDocument(Small).Add(box));

        Assert.Equal(Red, bitmap.GetPixel(30, 40)); // In the padding.
        Assert.NotEqual(Red, bitmap.GetPixel(22, 40)); // In the border.
    }

    [Fact]
    public void Rounded_corners_clip_the_content()
    {
        var box = new ContainerBlock(new BoxStyle { Height = 60, ClipOverflow = true, BorderRadius = CornerRadii.All(30) }, RedRect(60));
        using var bitmap = Render(new FlowDocument(Small).Add(box));

        Assert.Equal(Red, bitmap.GetPixel(100, 50));
        Assert.NotEqual(Red, bitmap.GetPixel(22, 22));
        Assert.NotEqual(Red, bitmap.GetPixel(178, 78));
    }

    [Fact]
    public void Hidden_content_of_a_fixed_height_box_makes_no_pages()
    {
        var box = new ContainerBlock(new BoxStyle { Height = 50, ClipOverflow = true }, Lines(60));
        var after = RedRect(10);
        var flow = new FlowDocument(Small).Add(box).Add(after);

        Assert.Equal(1, flow.CountPages());
        Assert.Equal(70, flow.Paginate().Placements.Single(p => p.Block == after).Top, 3);
    }

    [Fact]
    public void A_fixed_height_taller_than_the_page_continues_on_the_next()
    {
        var box = new ContainerBlock(new BoxStyle { Height = 300, Background = PdfColor.FromRgb(200, 200, 200) }, new SpacerBlock(1));
        var after = RedRect(10);
        var result = new FlowDocument(Small).Add(box).Add(after).Paginate();

        Assert.Equal([0, 1], result.Placements.Where(p => p.Block == box).Select(p => p.Page));
        var placed = result.Placements.Single(p => p.Block == after);
        Assert.Equal((1, 160.0), (placed.Page, placed.Top)); // 160 on page 1, 140 on page 2 from y 20.
    }

    [Fact]
    public void A_clipping_box_split_across_pages_is_clipped_on_each_and_ends_at_its_height()
    {
        var content = new ContainerBlock(new BoxStyle { Background = PdfColor.FromRgb(255, 0, 0) }, Lines(40)); // 400 pt.
        var box = new ContainerBlock(new BoxStyle { Height = 220, ClipOverflow = true }, content);
        var after = new ContainerBlock(new BoxStyle { Background = PdfColor.FromRgb(0, 0, 255) }, new SpacerBlock(10));
        var flow = new FlowDocument(Small).Add(box).Add(after);

        var placed = flow.Paginate().Placements.Single(p => p.Block == after);
        Assert.Equal((1, 80.0), (placed.Page, placed.Top)); // The box is 160 on page 1 and 60 on page 2.
        using var second = Conversion.ToImage(flow.Render().ToArray(), page: 1, options: new RenderOptions(Dpi: 72));
        Assert.Equal(Red, second.GetPixel(100, 50));
        Assert.Equal(new SKColor(0, 0, 255), second.GetPixel(100, 85)); // Below the box: what follows, not the hidden lines.
        Assert.Equal(new SKColor(255, 255, 255), second.GetPixel(100, 120));

        // The hidden lines reach a third page, where the box has no part: nothing of them shows.
        using var third = Conversion.ToImage(flow.Render().ToArray(), page: 2, options: new RenderOptions(Dpi: 72));
        Assert.Equal(new SKColor(255, 255, 255), third.GetPixel(100, 40));
    }

    [Fact]
    public void A_float_inside_a_clipping_box_is_clipped()
    {
        var red = new ContainerBlock(new BoxStyle { Float = FloatSide.Left, Width = 40, Background = PdfColor.FromRgb(255, 0, 0) }, new SpacerBlock(80));
        var box = new ContainerBlock(new BoxStyle { Height = 30, ClipOverflow = true, ContainsFloats = true }, red);
        using var bitmap = Render(new FlowDocument(Small).Add(box));

        Assert.Equal(Red, bitmap.GetPixel(30, 40));
        Assert.NotEqual(Red, bitmap.GetPixel(30, 70));
    }

    [Fact]
    public void A_relative_clipping_box_clips_where_it_is_drawn()
    {
        var box = new ContainerBlock(
            new BoxStyle { Position = Positioning.Relative, Insets = new BoxInsets(Top: Inset.Points(20)), Height = 30, ClipOverflow = true }, RedRect(60));
        using var bitmap = Render(new FlowDocument(Small).Add(box));

        Assert.Equal(Red, bitmap.GetPixel(100, 65)); // Moved down 20: the box is y 40-70.
        Assert.NotEqual(Red, bitmap.GetPixel(100, 75));
    }

    [Fact]
    public void An_ellipsis_ends_a_line_that_is_too_wide()
    {
        // Non-breaking spaces, as white-space: nowrap gives: one line that cannot break.
        var paragraph = new Paragraph(Text) { Ellipsis = true }.Add("A long line of text that does not fit in its box".Replace(' ', '\u00A0'));
        var line = Assert.Single(paragraph.Layout(100).Lines);

        var text = string.Concat(line.Fragments.SelectMany(f => f.Glyphs).Select(g => g.Text));
        Assert.EndsWith("\u2026", text, StringComparison.Ordinal);
        Assert.True(line.Width <= 100 + 1e-6, $"{line.Width}");
        Assert.True(line.Width > 90, $"{line.Width}"); // As much text as fits.
    }

    [Fact]
    public void A_box_narrower_than_the_ellipsis_keeps_one_character()
    {
        var paragraph = new Paragraph(Text) { Ellipsis = true }.Add("Wide\u00A0text");
        var line = paragraph.Layout(2).Lines[0];

        Assert.StartsWith("W", string.Concat(line.Fragments.SelectMany(f => f.Glyphs).Select(g => g.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_that_fits_gets_no_ellipsis()
    {
        var paragraph = new Paragraph(Text) { Ellipsis = true }.Add("Short");
        var line = Assert.Single(paragraph.Layout(100).Lines);

        Assert.Equal("Short", string.Concat(line.Fragments.SelectMany(f => f.Glyphs).Select(g => g.Text)));
    }

    private static ContainerBlock RedRect(double height) =>
        new(new BoxStyle { Background = PdfColor.FromRgb(255, 0, 0) }, new SpacerBlock(height));

    private static ParagraphBlock Lines(int count) =>
        new(new Paragraph(Text).Add(string.Join("\n", Enumerable.Range(1, count).Select(i => $"L{i}"))));

    private static SKBitmap Render(FlowDocument flow) => Conversion.ToImage(flow.Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));
}

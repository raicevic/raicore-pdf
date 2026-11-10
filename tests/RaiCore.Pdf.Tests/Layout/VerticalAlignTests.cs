using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>CSS vertical-align for inline text, images and boxes. Text is 10 pt with 12 pt lines.</summary>
public class VerticalAlignTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 10) { LineHeight = LineHeight.Points(12) };
    private static readonly PdfImage Pixel = PdfImage.Load(Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));

    // The strut: ascent + half-leading above the baseline, descent + half-leading below.
    private static readonly double Ascent = Sans.Ascent * 10;
    private static readonly double Descent = -Sans.Descent * 10;
    private static readonly double HalfLeading = (12 - Ascent - Descent) / 2;

    [Fact]
    public void Raised_text_is_drawn_higher_and_makes_the_line_taller()
    {
        var line = new Paragraph(Text).Add("x").Add("2", Text with { VerticalAlign = InlineAlignment.Raised(4) }).Layout(300).Lines[0];

        Assert.Equal(4, line.Fragments[1].BaselineShift);
        Assert.Equal(0, line.Fragments[0].BaselineShift);
        Assert.Equal(Ascent + HalfLeading + 4, line.Baseline, 6);
        Assert.Equal(12 + 4, line.Height, 6);
    }

    [Fact]
    public void Shifted_text_is_drawn_on_its_shifted_baseline()
    {
        var layout = new Paragraph(Text).Add("x").Add("2", Text with { VerticalAlign = InlineAlignment.Raised(4) }).Layout(300);
        var document = new PdfDocument { CompressContent = false };
        layout.Draw(document.AddPage(PdfPageSize.A4).Canvas, 0, 500);

        var content = System.Text.Encoding.Latin1.GetString(document.ToArray());
        var baselines = System.Text.RegularExpressions.Regex.Matches(content, @"1 0 0 1 \S+ (\S+) Tm")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).Distinct().Order().ToList();
        Assert.Equal(2, baselines.Count);
        Assert.Equal(4, baselines[1] - baselines[0], 6);
    }

    [Fact]
    public void Lowered_text_makes_the_line_deeper()
    {
        var line = new Paragraph(Text).Add("H").Add("2", Text with { VerticalAlign = InlineAlignment.Raised(-3) }).Layout(300).Lines[0];

        Assert.Equal(Ascent + HalfLeading, line.Baseline, 6);
        Assert.Equal(12 + 3, line.Height, 6);
    }

    [Fact]
    public void Middle_centers_an_image_on_half_the_x_height()
    {
        var line = new Paragraph(Text).Add("x").AddImage(Pixel, 10, 20, new InlineAlignment(InlineAlignmentKind.Middle)).Layout(300).Lines[0];

        var shift = line.Images[0].BaselineShift;
        Assert.Equal((Sans.XHeight * 10 / 2) - 10, shift, 6); // The image's middle (10 above its bottom) at x-height / 2.
    }

    [Theory]
    [InlineData(InlineAlignmentKind.TextTop)]
    [InlineData(InlineAlignmentKind.TextBottom)]
    public void Text_top_and_bottom_align_with_the_paragraph_fonts_edges(InlineAlignmentKind kind)
    {
        var line = new Paragraph(Text).Add("x").AddImage(Pixel, 5, 5, new InlineAlignment(kind)).Layout(300).Lines[0];

        var image = line.Images[0];
        var (top, bottom) = (image.BaselineShift + 5, image.BaselineShift);
        if (kind == InlineAlignmentKind.TextTop)
        {
            Assert.Equal(Ascent, top, 6);
        }
        else
        {
            Assert.Equal(-Descent, bottom, 6);
        }
    }

    [Fact]
    public void Top_and_bottom_align_with_the_line_box_after_the_rest()
    {
        var tall = new ContainerBlock(new BoxStyle { Width = 10 }, new SpacerBlock(40));
        var line = new Paragraph(Text)
            .Add("a")
            .AddBox(tall)
            .AddImage(Pixel, 5, 5, new InlineAlignment(InlineAlignmentKind.Top))
            .AddImage(Pixel, 5, 5, new InlineAlignment(InlineAlignmentKind.Bottom))
            .Layout(300).Lines[0];

        // The 40 pt box on the baseline sets the line: 40 above, the strut's descent below.
        Assert.Equal(40, line.Baseline, 6);
        Assert.Equal(40, line.Images[0].BaselineShift + 5, 6);                    // Top edge at the line top.
        Assert.Equal(-(line.Height - line.Baseline), line.Images[1].BaselineShift, 6); // Bottom edge at the line bottom.
    }

    [Fact]
    public void A_top_aligned_item_taller_than_the_line_extends_it_downwards()
    {
        var tall = new ContainerBlock(new BoxStyle { Width = 10 }, new SpacerBlock(50));
        var line = new Paragraph(Text).Add("a").AddBox(tall, new InlineAlignment(InlineAlignmentKind.Top)).Layout(300).Lines[0];

        Assert.Equal(Ascent + HalfLeading, line.Baseline, 6); // The baseline stays where the text puts it.
        Assert.Equal(50, line.Height, 6);
        Assert.Equal(line.Baseline - 50, line.Boxes[0].BaselineShift, 6);
    }

    [Fact]
    public void Inline_boxes_can_be_raised()
    {
        var box = new ContainerBlock(new BoxStyle { Width = 10 }, new SpacerBlock(8));
        var line = new Paragraph(Text).Add("a").AddBox(box, InlineAlignment.Raised(5)).Layout(300).Lines[0];

        Assert.Equal(5, line.Boxes[0].BaselineShift);
        Assert.Equal(Math.Max(Ascent + HalfLeading, 13), line.Baseline, 6);
    }
}

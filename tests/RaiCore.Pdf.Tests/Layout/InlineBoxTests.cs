using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Layout.Text;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>Inline boxes (CSS inline-block, inline-flex, inline-table) in paragraphs. Text is 8 pt with 10 pt lines.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class InlineBoxTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PdfColor Gray = PdfColor.FromRgb(200, 200, 200);

    [Fact]
    public void A_fixed_width_box_takes_its_width_on_the_line()
    {
        var box = Rect(40, 20);
        var layout = new Paragraph(Text).Add("ab ").AddBox(box).Add(" cd").Layout(300);

        var line = Assert.Single(layout.Lines);
        var fragment = Assert.Single(line.Boxes);
        Assert.Equal((40.0, 20.0), (fragment.Width, fragment.Height));
        Assert.Equal(Width("ab "), fragment.X, 6);
        Assert.Equal(Width("ab ") + 40 + Width(" cd"), line.Width, 6);
    }

    [Fact]
    public void A_box_without_text_sits_on_the_baseline_with_its_bottom_margin_edge()
    {
        var box = Rect(40, 20, new Thickness(3, 0, 5, 0));
        var line = new Paragraph(Text).Add("x").AddBox(box).Layout(300).Lines[0];

        var fragment = line.Boxes[0];
        Assert.Equal(25, fragment.Baseline); // Border box 20 + bottom margin 5.
        Assert.Equal(28, line.Baseline);     // Top margin 3 above it.
    }

    [Fact]
    public void A_box_with_text_sits_on_the_baseline_of_its_last_line()
    {
        var inner = new Paragraph(Text).Add("one\ntwo");
        var box = new ParagraphBlock(inner, new BoxStyle { Padding = Thickness.All(4), Background = Gray });
        var line = new Paragraph(Text).Add("x ").AddBox(box).Layout(300).Lines[0];

        var innerLines = inner.Layout(1000).Lines;
        var fragment = line.Boxes[0];
        Assert.Equal(4 + innerLines[1].Top + innerLines[1].Baseline, fragment.Baseline, 6);
        Assert.Equal(fragment.Baseline, line.Baseline, 6); // The box is taller than the line's own text.
        Assert.Equal(fragment.Height - fragment.Baseline, line.Height - line.Baseline, 6);
    }

    [Fact]
    public void A_box_without_a_width_shrinks_to_fit_its_content()
    {
        var box = new ParagraphBlock(new Paragraph(Text).Add("Hello"), new BoxStyle { Padding = new Thickness(0, 6, 0, 6) });
        var fragment = new Paragraph(Text).AddBox(box).Layout(300).Lines[0].Boxes[0];

        Assert.Equal(Width("Hello") + 12, fragment.Width, 6);
    }

    [Fact]
    public void A_box_wider_than_the_paragraph_wraps_its_content_at_the_paragraph_width()
    {
        var box = new ParagraphBlock(new Paragraph(Text).Add("several words that do not fit"), new BoxStyle { Margin = new Thickness(0, 5, 0, 5) });
        var fragment = new Paragraph(Text).AddBox(box).Layout(60).Lines[0].Boxes[0];

        Assert.Equal(50, fragment.Width, 6);      // 60 minus the margins.
        Assert.True(fragment.Height > 10, $"Expected several lines, got a {fragment.Height} pt box of width {fragment.Width}.");
        Assert.Equal(60, fragment.OuterWidth, 6);
    }

    [Fact]
    public void Lines_break_around_a_box_but_never_inside_it()
    {
        var box = Rect(60, 10);
        var layout = new Paragraph(Text).Add("word word ").AddBox(box).Add(" end").Layout(Width("word word ") + 30);

        Assert.Empty(layout.Lines[0].Boxes);
        Assert.Equal(0, Assert.Single(layout.Lines[1].Boxes).X);
    }

    [Fact]
    public void Intrinsic_widths_include_boxes()
    {
        var paragraph = new Paragraph(Text).Add("a ").AddBox(Rect(70, 10)).Add(" b");
        var (min, max) = paragraph.MeasureIntrinsicWidths();

        Assert.Equal(70, min, 6);
        Assert.Equal(Width("a ") + 70 + Width(" b"), max, 6);
    }

    [Fact]
    public void The_margins_of_a_boxs_children_stay_inside_it()
    {
        var child = new ParagraphBlock(new Paragraph(Text).Add("x"), new BoxStyle { Margin = new Thickness(6, 0, 6, 0) });
        var box = new ContainerBlock(new BoxStyle { Background = Gray }, child);
        var fragment = new Paragraph(Text).AddBox(box).Layout(300).Lines[0].Boxes[0];

        Assert.Equal(22, fragment.Height, 6); // 6 + one 10 pt line + 6.
    }

    [Fact]
    public void Flex_containers_align_on_their_first_baseline_and_tables_on_their_first_row()
    {
        var flex = new FlexBlock().Add(new ParagraphBlock(new Paragraph(Text).Add("a\nb"), new BoxStyle { Padding = new Thickness(5, 0, 0, 0) }));
        var table = new TableBlock().Add(new TableRow(new TableCell(new Paragraph(Text).Add("c")))).Add(new TableRow(new TableCell(new Paragraph(Text).Add("d"))));
        var line = new Paragraph(Text).AddBox(flex).Add(" ").AddBox(table).Layout(300).Lines[0];

        var firstLine = new Paragraph(Text).Add("a").Layout(100).Lines[0];
        Assert.Equal(5 + firstLine.Baseline, line.Boxes[0].Baseline, 6);
        Assert.Equal(firstLine.Baseline, line.Boxes[1].Baseline, 6);
        Assert.Equal(20, line.Boxes[1].Height, 6);
    }

    [Fact]
    public void Boxes_are_drawn_where_they_are_placed()
    {
        // A 200 x 100 page with 20 pt margins; a red 40 x 20 box after "ab ".
        var red = new ContainerBlock(new BoxStyle { Width = 40, Background = PdfColor.FromRgb(255, 0, 0) }, new SpacerBlock(20));
        var flow = new FlowDocument(new PageSetup(new PdfPageSize(200, 100), Thickness.All(20)))
            .Add(new ParagraphBlock(new Paragraph(Text).Add("ab ").AddBox(red).Add(" cd")));

        using var bitmap = Conversion.ToImage(flow.Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));
        var left = 20 + Width("ab ");
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel((int)(left + 20), 30));   // Inside the box (y 20-40).
        Assert.NotEqual(new SKColor(255, 0, 0), bitmap.GetPixel((int)(left + 20), 45));
        Assert.NotEqual(new SKColor(255, 0, 0), bitmap.GetPixel((int)(left - 3), 30));
    }

    private static ContainerBlock Rect(double width, double height, Thickness margin = default) =>
        new(new BoxStyle { Width = width, Margin = margin, Background = Gray }, new SpacerBlock(height));

    private static double Width(string text) => TextShaper.Shape(text, Text.Fonts).GetWidth(Text.FontSize);
}

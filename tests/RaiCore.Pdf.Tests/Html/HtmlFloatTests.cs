using PDFtoImage;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>CSS <c>float</c> and <c>clear</c>, and what starts a new block formatting context.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlFloatTests
{
    private const string Pixel = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='10' height='10'%3E%3Crect width='10' height='10'/%3E%3C/svg%3E";

    [Theory]
    [InlineData("left", FloatSide.Left)]
    [InlineData("right", FloatSide.Right)]
    [InlineData("inline-start", FloatSide.Left)]
    [InlineData("inline-end", FloatSide.Right)]
    [InlineData("none", FloatSide.None)]
    public void Float_is_read(string value, FloatSide expected) =>
        Assert.Equal(expected, Blocks($"<div id=\"f\" style=\"float: {value}\">x</div>").Single(b => b.Id == "f").Style.Float);

    [Theory]
    [InlineData("float: inline-start", FloatSide.Right, ClearSide.None)]
    [InlineData("float: inline-end", FloatSide.Left, ClearSide.None)]
    [InlineData("clear: inline-start", FloatSide.None, ClearSide.Right)]
    [InlineData("clear: inline-end", FloatSide.None, ClearSide.Left)]
    public void Logical_sides_follow_a_right_to_left_direction(string css, FloatSide floating, ClearSide clear)
    {
        var style = Blocks($"<div dir=\"rtl\"><div id=\"f\" style=\"{css}\">x</div></div>").Single(b => b.Id == "f").Style;

        Assert.Equal((floating, clear), (style.Float, style.Clear));
    }

    [Theory]
    [InlineData("left", ClearSide.Left)]
    [InlineData("right", ClearSide.Right)]
    [InlineData("both", ClearSide.Both)]
    [InlineData("none", ClearSide.None)]
    public void Clear_is_read(string value, ClearSide expected) =>
        Assert.Equal(expected, Blocks($"<div id=\"c\" style=\"clear: {value}\">x</div>").Single(b => b.Id == "c").Style.Clear);

    [Theory]
    [InlineData("display: flow-root", true)]
    [InlineData("overflow: hidden", true)]
    [InlineData("overflow: auto", true)]
    [InlineData("overflow-y: scroll", true)]
    [InlineData("overflow: clip", false)]
    [InlineData("overflow: visible", false)]
    [InlineData("", false)]
    public void New_formatting_contexts_contain_floats(string css, bool expected) =>
        Assert.Equal(expected, Blocks($"<div id=\"r\" style=\"{css}\">x</div>").Single(b => b.Id == "r").Style.ContainsFloats);

    [Fact]
    public void A_float_in_text_goes_before_its_paragraph_without_splitting_it()
    {
        var blocks = Blocks("<p>Before <span id=\"f\" style=\"float: right\">floated</span> after</p>");
        var box = blocks.Single(b => b.Id == "f");
        var paragraphs = blocks.OfType<ParagraphBlock>().Where(p => Text(p).Contains("Before", StringComparison.Ordinal)).ToList();

        var paragraph = Assert.Single(paragraphs);
        Assert.Equal(FloatSide.Right, box.Style.Float);
        Assert.Equal("Before after", Text(paragraph));
        Assert.True(blocks.IndexOf(box) < blocks.IndexOf(paragraph));
    }

    [Fact]
    public void A_floated_image_is_a_floating_image_block()
    {
        var image = Blocks($"<p><img src=\"{Pixel}\" style=\"float: left; width: 40px\"> text</p>").OfType<ImageBlock>().Single();

        Assert.Equal(FloatSide.Left, image.Style.Float);
    }

    [Fact]
    public void A_floated_inline_table_is_a_floating_table()
    {
        var table = Blocks("<p><span style=\"display: inline-table; float: right\"><span style=\"display: table-cell\">x</span></span> y</p>").OfType<TableBlock>().Single();

        Assert.Equal(FloatSide.Right, table.Style.Float);
    }

    [Fact]
    public void Auto_margins_of_a_float_are_zero()
    {
        var box = Blocks("<body style=\"margin: 0\"><div id=\"f\" style=\"float: left; width: 100px; margin: 0 auto\">x</div></body>").Single(b => b.Id == "f");

        Assert.Equal((0, 0), (box.Style.Margin.Left, box.Style.Margin.Right));
    }

    [Fact]
    public void Flex_items_do_not_float()
    {
        var flex = Blocks("<div style=\"display: flex\"><div style=\"float: left; clear: both\">a</div><img src=\"" + Pixel + "\" style=\"float: right\"></div>").OfType<FlexBlock>().Single();

        Assert.All(flex.Items, item => Assert.Equal((FloatSide.None, ClearSide.None), (item.Content.Style.Float, item.Content.Style.Clear)));
    }

    [Fact]
    public void A_clearfix_makes_the_container_hold_its_floats()
    {
        const string Css = ".clearfix::after { content: \"\"; display: table; clear: both }";
        var withFix = HtmlToPdf.CreateFlowDocument(
            $"<style>{Css}</style><body style=\"margin: 0\"><div id=\"c\" class=\"clearfix\" style=\"background: #eee\">"
            + "<div style=\"float: left; width: 50px; height: 100px; background: red\"></div>x</div></body>");
        var without = HtmlToPdf.CreateFlowDocument(
            "<body style=\"margin: 0\"><div id=\"c\" style=\"background: #eee\">"
            + "<div style=\"float: left; width: 50px; height: 100px; background: red\"></div>x</div></body>");

        double Height(FlowDocument flow) => flow.Paginate().Placements.Where(p => p.Block.Id == "c").Sum(p => p.Bottom - p.Top);
        Assert.Equal(75, Height(withFix), 1); // 100px.
        Assert.True(Height(without) < 30, $"{Height(without)}");
    }

    [Fact]
    public void An_invoice_totals_box_floats_right_beside_the_text()
    {
        var pdf = HtmlToPdf.Convert(
            "<body style=\"margin: 0; font-size: 20px\">"
            + "<div style=\"float: right; width: 150px; height: 100px; background: blue\"></div>"
            + "<p style=\"margin: 0\">Thank you for your business. Payment within thirty days of the invoice date, please.</p>"
            + "</body>").ToArray();
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));

        // The page is A4 with 2 cm margins: the float's right edge at 595 - 56.7, its top at 56.7.
        var blue = bitmap.GetPixel(bitmap.Width - 80, 80);
        Assert.True(blue.Blue > 200 && blue.Red < 50, $"{blue}");
        var textBesideFloat = Enumerable.Range(57, 70).Any(x => Enumerable.Range(60, 40).Any(y => bitmap.GetPixel(x, y).Red < 100 && bitmap.GetPixel(x, y).Blue < 100));
        Assert.True(textBesideFloat);
        // The float covers x 425.7-538.3 and y 56.7-131.7; text must stay out of it.
        var textUnderFloat = Enumerable.Range(430, 105).Any(x => Enumerable.Range(60, 68).Any(y => bitmap.GetPixel(x, y) is var c && c.Red < 100 && c.Blue < 100));
        Assert.False(textUnderFloat);
    }

    private static List<Block> Blocks(string html) => [.. Flatten(HtmlToPdf.CreateFlowDocument(html).Blocks)];

    private static string Text(ParagraphBlock paragraph) =>
        string.Join(" ", string.Concat(paragraph.Paragraph.Spans.Select(s => s.Text)).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b switch
        {
            ContainerBlock c => Flatten(c.Children).Prepend(b),
            FlexBlock f => Flatten(f.Items.Select(i => i.Content)).Prepend(b),
            _ => [b],
        });
}

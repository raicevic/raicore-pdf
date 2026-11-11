using PDFtoImage;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>CSS <c>position</c>, <c>top</c>/<c>right</c>/<c>bottom</c>/<c>left</c> and <c>z-index</c>.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlPositioningTests
{
    private const double Px = 0.75;

    [Theory]
    [InlineData("relative", Positioning.Relative)]
    [InlineData("absolute", Positioning.Absolute)]
    [InlineData("fixed", Positioning.Fixed)]
    [InlineData("sticky", Positioning.Static)]
    [InlineData("static", Positioning.Static)]
    public void Position_is_read(string value, Positioning expected) =>
        Assert.Equal(expected, Box($"<div id=\"b\" style=\"position: {value}\">x</div>").Style.Position);

    [Fact]
    public void Insets_keep_auto_and_an_absolute_blocks_percentages()
    {
        var insets = Box("<div id=\"b\" style=\"position: absolute; top: 10px; left: 25%\">x</div>").Style.Insets;

        Assert.Equal(new BoxInsets(Inset.Points(10 * Px), null, null, Inset.Percent(25)), insets);
    }

    [Fact]
    public void A_relative_blocks_percentages_resolve_against_its_containing_block()
    {
        var insets = Box("<body style=\"margin: 0; width: 400px\"><div id=\"b\" style=\"position: relative; left: 10%; top: 50%\">x</div></body>").Style.Insets;

        Assert.Equal(new BoxInsets(Top: null, Left: Inset.Points(40 * Px)), insets); // top: 50% of an auto height is auto.
    }

    [Theory]
    [InlineData("z-index: 3", 3)]
    [InlineData("z-index: -2", -2)]
    [InlineData("z-index: auto", null)]
    [InlineData("", null)]
    public void Z_index_is_read(string css, int? expected) =>
        Assert.Equal(expected, Box($"<div id=\"b\" style=\"position: relative; {css}\">x</div>").Style.ZIndex);

    [Fact]
    public void An_absolute_element_does_not_float()
    {
        var style = Box("<div id=\"b\" style=\"position: absolute; float: left\">x</div>").Style;

        Assert.Equal((Positioning.Absolute, FloatSide.None), (style.Position, style.Float));
    }

    [Fact]
    public void An_absolute_element_in_text_leaves_the_paragraph_whole()
    {
        var blocks = Blocks("<p>Before <span id=\"b\" style=\"position: absolute; top: 0\">badge</span> after</p>");
        var paragraph = blocks.OfType<ParagraphBlock>().Single(p => string.Concat(p.Paragraph.Spans.Select(s => s.Text)).Contains("Before", StringComparison.Ordinal));

        Assert.Equal(Positioning.Absolute, blocks.Single(b => b.Id == "b").Style.Position);
        var text = string.Concat(paragraph.Paragraph.Spans.Select(s => s.Text));
        Assert.DoesNotContain("badge", text, StringComparison.Ordinal);
        Assert.Contains("after", text, StringComparison.Ordinal); // Not split.
        Assert.Single(blocks.OfType<ParagraphBlock>(), p => string.Concat(p.Paragraph.Spans.Select(s => s.Text)).Contains("after", StringComparison.Ordinal));
    }

    [Fact]
    public void An_address_for_an_envelope_window_is_placed_in_the_page_area()
    {
        // A4 with 2 cm margins: the page area starts at 56.7 pt; the box at top 100px, left 200px is at (206.7, 131.7).
        var pdf = HtmlToPdf.Convert(
            "<body style=\"margin: 0\"><p>Letter text.</p>"
            + "<div style=\"position: absolute; top: 100px; left: 200px; width: 100px; height: 50px; background: blue\"></div></body>").ToArray();
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));

        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(210, 135));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(280, 165));
        Assert.Equal(new SKColor(255, 255, 255), bitmap.GetPixel(200, 135));
    }

    [Fact]
    public void A_fixed_element_is_drawn_on_every_page()
    {
        var pdf = HtmlToPdf.Convert(
            "<body style=\"margin: 0\"><div style=\"position: fixed; top: 0; left: 0; width: 50px; height: 20px; background: red\"></div>"
            + "<p>One</p><p style=\"break-before: page\">Two</p></body>").ToArray();

        for (var page = 0; page < 2; page++)
        {
            using var bitmap = Conversion.ToImage(pdf, page: page, options: new RenderOptions(Dpi: 72));
            Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(70, 65));
        }
    }

    [Fact]
    public void A_badge_is_placed_in_the_corner_of_its_relative_card()
    {
        var card = HtmlToPdf.CreateFlowDocument(
            "<body style=\"margin: 0\"><div id=\"card\" style=\"position: relative; width: 200px; height: 100px; background: #eee\">"
            + "<div id=\"badge\" style=\"position: absolute; top: -10px; right: -10px; width: 20px; height: 20px; background: red\"></div></div></body>");
        var placements = card.Paginate().Placements;
        var cardBox = placements.First(p => p.Block.Id == "card");
        var badge = placements.First(p => p.Block.Id == "badge");

        Assert.Equal(cardBox.Left + (190 * Px), badge.Left, 3);
        Assert.Equal(cardBox.Top - (10 * Px), badge.Top, 3);
    }

    private static Block Box(string html) => Blocks(html).Single(b => b.Id == "b");

    private static List<Block> Blocks(string html) => [.. Flatten(HtmlToPdf.CreateFlowDocument(html).Blocks)];

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b switch
        {
            ContainerBlock c => Flatten(c.Children).Prepend(b),
            FlexBlock f => Flatten(f.Items.Select(i => i.Content)).Prepend(b),
            _ => [b],
        });
}

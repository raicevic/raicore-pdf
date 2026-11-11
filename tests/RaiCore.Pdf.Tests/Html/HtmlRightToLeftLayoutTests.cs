using PDFtoImage;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>
/// Right-to-left boxes: tables and flex rows take the direction, narrow boxes go to the start side of their containing
/// block, and the logical margins and padding (also the default list indent) follow the direction.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlRightToLeftLayoutTests
{
    private const double Px = 0.75;

    [Theory]
    [InlineData("<div dir=\"rtl\"><table><tr><td>x</td></tr></table></div>", true)]
    [InlineData("<table style=\"direction: rtl\"><tr><td>x</td></tr></table>", true)]
    [InlineData("<table><tr><td>x</td></tr></table>", false)]
    public void Tables_take_the_direction(string html, bool expected) =>
        Assert.Equal(expected, Blocks(html).OfType<TableBlock>().Single().RightToLeft);

    [Theory]
    [InlineData("<div dir=\"rtl\"><div style=\"display: flex\"><div>x</div></div></div>", true)]
    [InlineData("<div style=\"display: flex\"><div>x</div></div>", false)]
    public void Flex_containers_take_the_direction(string html, bool expected) =>
        Assert.Equal(expected, Blocks(html).OfType<FlexBlock>().Single().RightToLeft);

    [Theory]
    [InlineData("<div dir=\"rtl\"><table><tr><td>x</td></tr></table></div>", TextAlignment.Right)]
    [InlineData("<div dir=\"rtl\"><table style=\"margin-right: auto\"><tr><td>x</td></tr></table></div>", TextAlignment.Left)]
    [InlineData("<div dir=\"rtl\"><table style=\"margin: 0 auto\"><tr><td>x</td></tr></table></div>", TextAlignment.Center)]
    [InlineData("<table dir=\"rtl\"><tr><td>x</td></tr></table>", TextAlignment.Left)] // The containing block is left to right.
    [InlineData("<div dir=\"rtl\"><table dir=\"ltr\"><tr><td>x</td></tr></table></div>", TextAlignment.Right)]
    public void A_shrink_to_fit_table_goes_to_the_start_of_its_containing_block(string html, TextAlignment expected) =>
        Assert.Equal(expected, Blocks(html).OfType<TableBlock>().Single().Alignment);

    [Theory]
    [InlineData("<div dir=\"rtl\"><div id=\"b\" style=\"width: 100px\">x</div></div>", 300)]
    [InlineData("<div dir=\"rtl\"><div id=\"b\" style=\"width: 100px; margin-left: 20px; margin-right: 10px\">x</div></div>", 290)]
    [InlineData("<div dir=\"rtl\"><div id=\"b\" style=\"width: 100px; margin-right: auto\">x</div></div>", 0)]
    [InlineData("<div dir=\"rtl\"><div id=\"b\" dir=\"ltr\" style=\"width: 100px\">x</div></div>", 300)]
    [InlineData("<div><div id=\"b\" dir=\"rtl\" style=\"width: 100px\">x</div></div>", 0)]
    public void An_over_constrained_box_gives_up_its_end_margin(string html, double marginLeftPx)
    {
        // A 400px wide body: the end margin, the left one in a right-to-left containing block, takes what is left.
        var box = Blocks($"<body style=\"margin: 0; width: 400px\">{html}</body>").Single(b => b.Id == "b");

        Assert.Equal(marginLeftPx * Px, box.Style.Margin.Left, 3);
    }

    [Fact]
    public void A_right_to_left_list_is_indented_on_the_right()
    {
        var list = Blocks("<ul dir=\"rtl\" id=\"l\"><li>x</li></ul>").Single(b => b.Id == "l");

        Assert.Equal((0, 40 * Px), (list.Style.Padding.Left, list.Style.Padding.Right));
    }

    [Fact]
    public void A_list_inherits_the_direction_for_its_indent()
    {
        var list = Blocks("<div dir=\"rtl\"><ol id=\"l\"><li>x</li></ol></div>").Single(b => b.Id == "l");

        Assert.Equal((0, 40 * Px), (list.Style.Padding.Left, list.Style.Padding.Right));
    }

    [Theory]
    [InlineData("ul { padding: 0 }", 0, 0)]
    [InlineData("ul { padding-right: 10px }", 0, 10)]
    [InlineData("ul { padding-left: 10px }", 10, 40)] // The default indent is on the other side.
    [InlineData("ul { padding-inline-start: 0 }", 0, 0)]
    [InlineData("ul { padding-inline-end: 5px }", 5, 40)]
    [InlineData("ul { padding-inline: 1px 2px }", 2, 1)]
    [InlineData("#l { padding-right: 7px } ul { padding-inline-start: 9px }", 0, 7)] // Specificity decides.
    [InlineData("ul { padding-inline-start: 9px } ul { padding-right: 7px }", 0, 7)] // Then order.
    [InlineData("ul { padding-right: 7px !important } ul { padding-inline-start: 9px }", 0, 7)]
    [InlineData("ul { padding: 1px 2px 3px 4px }", 4, 2)]
    [InlineData("ul { padding: 1px 2px }", 2, 2)]
    [InlineData("ul { padding: /* x */ 1px calc(2px + 3px) }", 5, 5)]
    [InlineData("ul { padding: calc(1px + 1px) 3px }", 3, 3)]
    public void The_cascade_decides_a_right_to_left_lists_padding(string css, double leftPx, double rightPx)
    {
        var list = Blocks($"<style>{css}</style><ul dir=\"rtl\" id=\"l\"><li>x</li></ul>").Single(b => b.Id == "l");

        Assert.Equal(leftPx * Px, list.Style.Padding.Left, 3);
        Assert.Equal(rightPx * Px, list.Style.Padding.Right, 3);
    }

    [Theory]
    [InlineData("<div dir=\"rtl\" id=\"b\" style=\"margin-inline-start: 10px; margin-inline-end: 4px\">x</div>", 4, 10)]
    [InlineData("<div dir=\"ltr\" id=\"b\" style=\"margin-inline-start: 10px; margin-inline-end: 4px\">x</div>", 10, 4)]
    [InlineData("<div dir=\"rtl\" id=\"b\" style=\"margin: 0 0 0 6px\">x</div>", 6, 0)]
    [InlineData("<div dir=\"rtl\" style=\"margin-inline-start: 10px\"><div id=\"b\">x</div></div>", 0, 0)] // Not inherited.
    [InlineData("<dl dir=\"rtl\"><dd id=\"b\">x</dd></dl>", 0, 40)]
    public void Logical_margins_follow_the_direction(string html, double leftPx, double rightPx)
    {
        var box = Blocks($"<body style=\"margin: 0\">{html}</body>").Single(b => b.Id == "b");

        Assert.Equal(leftPx * Px, box.Style.Margin.Left, 3);
        Assert.Equal(rightPx * Px, box.Style.Margin.Right, 3);
    }

    [Theory]
    [InlineData("p { margin: 0 }", "p { margin: 0; --raicore-rtl-margin-right: 0; --raicore-rtl-margin-left: 0 }")]
    [InlineData("p{padding-left:1px}", "p{padding-left:1px; --raicore-rtl-padding-left: 1px}")]
    [InlineData("p { margin-inline-start: 2em !important; color: red }", "p { margin-inline-start: 2em !important; --raicore-rtl-margin-right: 2em !important; color: red }")]
    [InlineData(".margin-top { margin-top: 1px }", ".margin-top { margin-top: 1px }")] // Vertical sides and selectors stay.
    [InlineData("p { --my-margin: 1px; scroll-margin: 2px }", "p { --my-margin: 1px; scroll-margin: 2px }")]
    public void The_rewrite_adds_the_right_to_left_sides(string css, string expected) =>
        Assert.Equal(expected, LogicalSides.Rewrite(css));

    [Fact]
    public void A_right_to_left_flex_row_is_drawn_from_the_right()
    {
        var pdf = HtmlToPdf.Convert(
            "<body style=\"margin: 0\"><div dir=\"rtl\" style=\"display: flex\">"
            + "<div style=\"width: 50px; height: 50px; background: red\"></div><div style=\"width: 50px; height: 50px; background: blue\"></div>"
            + "</div></body>").ToArray();
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));

        var row = Enumerable.Range(0, bitmap.Width).Select(x => bitmap.GetPixel(x, 80)).ToList(); // Below the 2 cm page margin.
        var red = row.FindIndex(c => c.Red > 200 && c.Blue < 50);
        var blue = row.FindIndex(c => c.Blue > 200 && c.Red < 50);
        Assert.True(red > bitmap.Width / 2 && blue > bitmap.Width / 2 && red > blue, $"red at {red}, blue at {blue} of {bitmap.Width}");
    }

    private static List<Block> Blocks(string html) => [.. Flatten(HtmlToPdf.CreateFlowDocument(html).Blocks)];

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b switch
        {
            ContainerBlock c => Flatten(c.Children).Prepend(b),
            FlexBlock f => Flatten(f.Items.Select(i => i.Content)).Prepend(b),
            _ => [b],
        });
}

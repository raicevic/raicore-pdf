using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlBorderStyleTests
{
    private const double Px = 0.75;

    [Theory]
    [InlineData("dashed", "Dashed Dashed Dashed Dashed")]
    [InlineData("dotted double", "Dotted Double Dotted Double")]
    [InlineData("solid dashed dotted", "Solid Dashed Dotted Dashed")]
    [InlineData("inset outset groove ridge", "Inset Outset Groove Ridge")]
    public void Border_style_takes_one_to_four_values(string value, string expected)
    {
        var border = Border($"border-width: 3px; border-style: {value}");

        Assert.Equal(expected, string.Join(' ', Styles(border)));
        Assert.All(Widths(border), w => Assert.Equal(3 * Px, w, 3));
    }

    [Fact]
    public void Each_side_has_its_own_style()
    {
        var border = Border("border: 2px solid red; border-left-style: dashed; border-bottom: 4px double blue; border-top-style: dotted");

        Assert.Equal([BorderStyle.Dotted, BorderStyle.Solid, BorderStyle.Double, BorderStyle.Dashed], Styles(border));
        Assert.Equal(4 * Px, border.Bottom.Width, 3);
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), border.Bottom.Color);
    }

    [Fact]
    public void None_and_hidden_have_no_width()
    {
        var border = Border("border: 2px solid; border-top-style: none; border-bottom-style: hidden");

        Assert.Equal(0, border.Top.Width);
        Assert.Equal((0.0, BorderStyle.Hidden), (border.Bottom.Width, border.Bottom.Style));
        Assert.Equal(2 * Px, border.Left.Width, 3);
    }

    [Fact]
    public void Collapsed_cell_borders_resolve_by_style()
    {
        var table = Flatten(Blocks("""
            <table style="border-collapse: collapse">
              <tr><td style="border: 2px dotted">a</td><td style="border: 2px double">b</td><td style="border-left: hidden">c</td></tr>
            </table>
            """)).OfType<TableBlock>().Single();
        var layout = TableLayouter.Layout(table, 0, 400);

        Assert.Equal(BorderStyle.Dotted, layout.Vertical![0, 0].Style);
        Assert.Equal(BorderStyle.Double, layout.Vertical[0, 1].Style); // Double beats dotted.
        Assert.Equal(0, layout.Vertical[0, 2].Width); // Hidden beats double.
    }

    [Fact]
    public void The_legacy_border_attribute_stays_solid()
    {
        var table = Flatten(Blocks("<table border='1'><tr><td>a</td></tr></table>")).OfType<TableBlock>().Single();

        Assert.Equal(BorderStyle.Solid, table.Style.Border.Top.Style);
        Assert.Equal(BorderStyle.Solid, table.Rows[0].Cells[0].Style.Border.Left.Style);
        Assert.Equal(1 * Px, table.Rows[0].Cells[0].Style.Border.Left.Width, 3);
    }

    // ---- helpers ----

    private static Borders Border(string css) =>
        Flatten(Blocks($"<div style='{css}'>x</div>")).Single(b => b.Style.Border != Borders.None).Style.Border;

    private static BorderStyle[] Styles(Borders border) => [border.Top.Style, border.Right.Style, border.Bottom.Style, border.Left.Style];

    private static double[] Widths(Borders border) => [border.Top.Width, border.Right.Width, border.Bottom.Width, border.Left.Width];

    private static IReadOnlyList<Block> Blocks(string html) =>
        HtmlToPdf.CreateFlowDocument("<body style='margin: 0'>" + html, new HtmlConverterOptions
        {
            Page = new PageSetup(new PdfPageSize(500, 800), Thickness.All(50)),
        }).Blocks;

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);
}

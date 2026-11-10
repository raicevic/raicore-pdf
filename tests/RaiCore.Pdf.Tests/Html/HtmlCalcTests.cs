using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>calc(), min(), max() and clamp() in every property that takes a length.</summary>
public class HtmlCalcTests
{
    private const double Px = 0.75; // Points per CSS pixel.

    // A 500 x 800 pt page with 50 pt margins: content x 50-450, y 50-750 (400 x 700 pt). Body margins are removed.
    private static readonly PageSetup Page = new(new PdfPageSize(500, 800), Thickness.All(50));

    private const string NoBodyMargin = "<style>body { margin: 0 }</style>";

    [Fact]
    public void Widths_resolve_percentages_against_each_containing_block()
    {
        var blocks = Blocks(NoBodyMargin + """
            <div id="a" style="width: calc(100% - 20px)">a</div>
            <div style="width: 200px"><div id="b" style="width: calc(100% - 20px)">b</div></div>
            <div style="width: 200px; padding: 0 10px"><div id="c" style="width: calc((100% - 2em) / 2); font-size: 10px">c</div></div>
            <div style="width: 200px"><div id="d" style="width: calc(100% - 20px); padding: 5px; border: 1px solid">d</div></div>
            <div style="width: 200px"><div id="e" style="box-sizing: border-box; width: calc(100% - 20px); padding: 5px">e</div></div>
            """);

        Assert.Equal(400 - (20 * Px), blocks["a"].Style.Width!.Value, 6);
        Assert.Equal((200 - 20) * Px, blocks["b"].Style.Width!.Value, 6);
        Assert.Equal((200 - 20) / 2 * Px, blocks["c"].Style.Width!.Value, 6);
        Assert.Equal((200 - 20 + 12) * Px, blocks["d"].Style.Width!.Value, 6); // Content box plus padding and border.
        Assert.Equal((200 - 20) * Px, blocks["e"].Style.Width!.Value, 6);
    }

    [Fact]
    public void Negative_sizes_are_clamped_at_zero()
    {
        var blocks = Blocks(NoBodyMargin + """<div id="a" style="width: calc(50% - 1000px)">a</div>""");

        Assert.Equal(0, blocks["a"].Style.Width);
    }

    [Fact]
    public void Max_width_with_min_and_max()
    {
        var blocks = Blocks(NoBodyMargin + """
            <div id="a" style="max-width: min(500px, 90%)">a</div>
            <div style="width: 800px"><div id="b" style="max-width: min(500px, 90%)">b</div></div>
            <div id="c" style="width: max(100px, 50%); margin: 0 auto">c</div>
            """);

        Assert.Equal(400 * 0.9, blocks["a"].Style.Width!.Value, 6);
        Assert.Equal(500 * Px, blocks["b"].Style.Width!.Value, 6);
        Assert.Equal(200, blocks["c"].Style.Width!.Value, 6);
        Assert.Equal(100, blocks["c"].Style.Margin.Left, 6); // Auto margins share what calc() leaves.
    }

    [Fact]
    public void Heights_resolve_against_a_definite_containing_height()
    {
        var blocks = Blocks("""
            <div style="height: 200px"><div id="a" style="height: calc(50% + 10px)">a</div></div>
            <div><div id="b" style="height: calc(50% + 10px)">b</div></div>
            <div style="height: 200px"><div id="c" style="min-height: calc(25% - 5px); max-height: clamp(10px, 80%, 100px)">c</div></div>
            <div><div id="d" style="min-height: calc(25% - 5px); max-height: calc(80% + 1px)">d</div></div>
            <div id="e" style="height: calc(2em + 10px); font-size: 10px">e</div>
            """);

        Assert.Equal((100 + 10) * Px, blocks["a"].Style.Height!.Value, 6);
        Assert.Null(blocks["b"].Style.Height); // The parent's height is auto, so the percentage (and the calc) is too.
        Assert.Equal((50 - 5) * Px, blocks["c"].Style.MinHeight!.Value, 6);
        Assert.Equal(100 * Px, blocks["c"].Style.MaxHeight!.Value, 6);
        Assert.Null(blocks["d"].Style.MinHeight);
        Assert.Null(blocks["d"].Style.MaxHeight);
        Assert.Equal(30 * Px, blocks["e"].Style.Height!.Value, 6);
    }

    [Fact]
    public void Margins_and_padding()
    {
        var blocks = Blocks(NoBodyMargin + """
            <div id="a" style="margin: calc(10px + 1em) calc(10% - 5px); font-size: 16px">a</div>
            <div id="b" style="padding: calc(1em / 2) clamp(1em, 5%, 3em); font-size: 16px">b</div>
            <div id="c" style="margin-left: max(5%, 1em); margin-right: calc(-1 * 10px)">c</div>
            <div style="width: 100px"><div id="d" style="padding-left: clamp(1em, 5%, 3em); margin-left: calc(50% - 10px); font-size: 16px">d</div></div>
            """);

        Assert.Equal(26 * Px, blocks["a"].Style.Margin.Top, 6);
        Assert.Equal(40 - (5 * Px), blocks["a"].Style.Margin.Left, 6);
        Assert.Equal(40 - (5 * Px), blocks["a"].Style.Margin.Right, 6);
        Assert.Equal(8 * Px, blocks["b"].Style.Padding.Top, 6);
        Assert.Equal(20, blocks["b"].Style.Padding.Left, 6); // 5% of 400 is between 1em (12) and 3em (36).
        Assert.Equal(20, blocks["c"].Style.Margin.Left, 6);
        Assert.Equal(-10 * Px, blocks["c"].Style.Margin.Right, 6);
        Assert.Equal(16 * Px, blocks["d"].Style.Padding.Left, 6); // 5% of 75 is below 1em.
        Assert.Equal((50 - 10) * Px, blocks["d"].Style.Margin.Left, 6);
    }

    [Fact]
    public void Text_indent()
    {
        var paragraph = Paragraphs(NoBodyMargin + "<p style='text-indent: calc(2em + 10%); font-size: 10px'>x</p>").Single();

        Assert.Equal((20 * Px) + 40, paragraph.Paragraph.FirstLineIndent, 6);
    }

    [Fact]
    public void Flex_basis_in_a_row()
    {
        var flex = Flex(NoBodyMargin + """
            <div style="display: flex; width: 400px">
              <div style="flex: 0 0 calc(50% - 10px)">a</div>
              <div style="flex-basis: min(30%, 100px)">b</div>
              <div style="flex-basis: max(30%, 100px); padding: 0 5px">c</div>
            </div>
            """);

        Assert.Equal(FlexBasis.Points((200 - 10) * Px), flex.Items[0].Basis);
        Assert.Equal(FlexBasis.Points(100 * Px), flex.Items[1].Basis);
        Assert.Equal(FlexBasis.Points((120 + 10) * Px), flex.Items[2].Basis);
    }

    [Fact]
    public void Flex_basis_in_a_column_needs_a_definite_height()
    {
        var definite = Flex("<div style='display: flex; flex-direction: column; height: 200px'><div style='flex-basis: calc(50% - 10px)'>a</div></div>");
        var auto = Flex("<div style='display: flex; flex-direction: column'><div style='flex-basis: calc(50% - 10px)'>a</div></div>");

        Assert.Equal(FlexBasis.Points((100 - 10) * Px), definite.Items[0].Basis);
        Assert.Equal(FlexBasis.Content, auto.Items[0].Basis); // As a plain percentage of an auto height.
    }

    [Fact]
    public void Flex_items_lay_out_with_calc_bases()
    {
        var flow = Flow(NoBodyMargin + """
            <div style="display: flex">
              <div style="flex: 0 0 200px; background: #eee">side</div>
              <div style="flex: 0 0 calc(100% - 200px); background: #ddd">main</div>
            </div>
            """);
        var placements = flow.Paginate().Placements.Where(p => p.FirstLine == -1 && p.Block is ParagraphBlock).ToList();
        var main = placements.Single(p => Text(p.Block) == "main");

        Assert.Equal(50 + (200 * Px), main.Left, 6);
        Assert.Equal(450, main.Left + main.Width, 6);
    }

    [Fact]
    public void Gaps()
    {
        var flex = Flex(NoBodyMargin + "<div style='display: flex; width: 400px; font-size: 16px; gap: calc(1em + 2px) calc(5% - 1px)'></div>");
        var definite = Flex("<div style='display: flex; height: 200px; row-gap: calc(10% + 2px); column-gap: max(10px, 50%)'></div>");
        var auto = Flex("<div style='display: flex; row-gap: calc(10% + 2px)'></div>");

        Assert.Equal(18 * Px, flex.RowGap, 6);
        Assert.Equal((20 - 1) * Px, flex.ColumnGap, 6);
        Assert.Equal((20 + 2) * Px, definite.RowGap, 6);
        Assert.Equal((400 - (16 * Px)) / 2, definite.ColumnGap, 6); // Half the body's content width.
        Assert.Equal(2 * Px, auto.RowGap, 6); // The percentage of an auto height is 0.
    }

    [Theory]
    [InlineData("font-size: calc(1em + 2px)", 18)]
    [InlineData("font-size: calc(50% + 2px)", 10)]
    [InlineData("font-size: min(2em, 20px)", 20)]
    [InlineData("font-size: max(2em, 20px)", 32)]
    [InlineData("font-size: clamp(12px, 1vw, 20px)", 12)] // 1vw is about 5px.
    [InlineData("font-size: clamp(12px, 3vw, 20px)", 15.99)] // 3vw of 533px.
    [InlineData("font-size: clamp(12px, 5vw, 20px)", 20)]
    public void Font_sizes_resolve_against_the_parent_font_size(string css, double pixels)
    {
        var paragraph = Paragraphs($"<div style='font-size: 16px'><p style='{css}'>x</p></div>").Single();

        Assert.Equal(pixels * Px, paragraph.Paragraph.Style.FontSize, 2);
    }

    [Theory]
    [InlineData("line-height: calc(1em + 4px)", 14)]
    [InlineData("line-height: calc(150% + 2px)", 17)]
    [InlineData("line-height: max(1em, 18px)", 18)]
    public void Line_heights_resolve_against_the_font_size(string css, double pixels)
    {
        var paragraph = Paragraphs($"<p style='font-size: 10px; {css}'>x</p>").Single();

        Assert.Equal(LineHeight.Points(pixels * Px), paragraph.Paragraph.Style.LineHeight);
    }

    [Fact]
    public void Vertical_align_percentages_refer_to_the_line_height()
    {
        var paragraph = Paragraphs("<p>a<span style='line-height: 20px; vertical-align: calc(10% + 2px)'>b</span></p>").Single();
        var span = paragraph.Paragraph.Spans.Single(s => s.Text == "b");

        Assert.Equal((2 + 2) * Px, span.Style.VerticalAlign.Shift, 6);
    }

    [Theory]
    [InlineData("letter-spacing: calc(0.1em + 1px); word-spacing: max(2px, 0.25em)", 3, 5)]
    [InlineData("letter-spacing: calc(10% + 1px); word-spacing: calc(-1 * 0.25em)", 3, -5)] // Percentages of the font size.
    [InlineData("letter-spacing: calc(1px + 3); word-spacing: calc(2px + 5)", 0, 0)] // Invalid: normal.
    public void Letter_and_word_spacing(string css, double letter, double word)
    {
        var span = Paragraphs($"<p style='font-size: 20px; {css}'>x</p>").Single().Paragraph.Spans[0];

        Assert.Equal(letter * Px, span.Style.LetterSpacing, 6);
        Assert.Equal(word * Px, span.Style.WordSpacing, 6);
    }

    [Theory]
    [InlineData("text-decoration-thickness: calc(10% + 1px); text-underline-offset: calc(0.1em - 1px)", 3, 1)] // Percentages of 1em.
    [InlineData("text-decoration-thickness: max(1px, 5%); text-underline-offset: min(-2px, 10%)", 1, -2)]
    [InlineData("text-decoration-thickness: calc(1px - 1em); text-underline-offset: calc(1px + 3)", 0, double.NaN)] // Clamped; invalid is auto.
    public void Text_decoration_thickness_and_underline_offset(string css, double thickness, double offset)
    {
        var span = Paragraphs($"<p style='font-size: 20px; text-decoration: underline; {css}'>x</p>").Single().Paragraph.Spans[0];
        var decoration = Assert.Single(span.Style.Decorations);

        Assert.Equal(thickness * Px, decoration.Thickness!.Value, 6);
        if (double.IsNaN(offset))
        {
            Assert.Null(decoration.UnderlineOffset);
        }
        else
        {
            Assert.Equal(offset * Px, decoration.UnderlineOffset!.Value, 6);
        }
    }

    [Fact]
    public void Viewport_units_are_the_page_content_area()
    {
        // The viewport is a whole number of CSS pixels (533 x 933), so values can be up to a pixel short.
        var blocks = Blocks(NoBodyMargin + """
            <div id="a" style="width: calc(50vw - 10px)">a</div>
            <div style="height: 200px"><div id="b" style="height: calc(10vh + 10%)">b</div></div>
            <div id="c" style="width: min(50vw, 50%)">c</div>
            """);

        Assert.Equal(200 - (10 * Px), blocks["a"].Style.Width!.Value, Px);
        Assert.Equal(70 + 15, blocks["b"].Style.Height!.Value, Px);
        Assert.Equal(200, blocks["c"].Style.Width!.Value, Px);
    }

    [Fact]
    public void Viewport_units_follow_the_page_rule()
    {
        // @page makes the content area 300 x 600 pt (400 x 800 px).
        var blocks = Blocks("""
            <style>@page { margin: 100pt } body { margin: 0 }</style>
            <div id="a" style="width: calc(50vw + 1px); height: 50vh">a</div>
            """);

        Assert.Equal(150 + Px, blocks["a"].Style.Width!.Value, 6);
        Assert.Equal(300, blocks["a"].Style.Height!.Value, 6);
    }

    [Fact]
    public void Custom_properties_inside_expressions()
    {
        var blocks = Blocks(NoBodyMargin + """
            <style>:root { --gap: 12px; --cols: 3; --side: calc(20% + 5px) }</style>
            <div style="width: 400px">
              <div id="a" style="width: calc((100% - var(--gap) * (var(--cols) - 1)) / var(--cols))">a</div>
              <div id="b" style="width: calc(var(--gap) * 2)">b</div>
              <div id="c" style="width: calc(var(--side) - 10px); margin-left: var(--side)">c</div>
              <div id="d" style="width: calc(100% - var(--missing, 20px))">d</div>
            </div>
            """);

        Assert.Equal((400 - 24) / 3.0 * Px, blocks["a"].Style.Width!.Value, 6);
        Assert.Equal(24 * Px, blocks["b"].Style.Width!.Value, 6);
        Assert.Equal((80 + 5 - 10) * Px, blocks["c"].Style.Width!.Value, 6);
        Assert.Equal((80 + 5) * Px, blocks["c"].Style.Margin.Left, 6);
        Assert.Equal((400 - 20) * Px, blocks["d"].Style.Width!.Value, 6);
    }

    [Fact]
    public void Invalid_expressions_fall_back_to_the_initial_value()
    {
        // A number cannot be added to a length; AngleSharp keeps such values, so they are rejected here.
        var blocks = Blocks(NoBodyMargin + """
            <style>:root { --n: 3 }</style>
            <div id="a" style="width: calc(50% + var(--n)); margin-left: calc(10% + var(--n)); padding-left: calc(5% * 3px)">a</div>
            <div style="height: 100px"><div id="c" style="height: calc(50% + var(--n)); max-height: calc(1% / 2px)">c</div></div>
            """);
        var flex = Flex("<div style='display: flex; gap: calc(10% + var(--n))'><div style='flex-basis: calc(10% + var(--n))'>x</div></div>");

        Assert.Null(blocks["a"].Style.Width); // auto
        Assert.Equal(0, blocks["a"].Style.Margin.Left);
        Assert.Equal(0, blocks["a"].Style.Padding.Left);
        Assert.Null(blocks["c"].Style.Height);
        Assert.Null(blocks["c"].Style.MaxHeight);
        Assert.Equal(FlexBasis.Auto, flex.Items[0].Basis);
        Assert.Equal(0, flex.ColumnGap);
    }

    [Fact]
    public void Table_cell_and_column_widths_resolve_against_the_table()
    {
        var table = Flatten(Flow(NoBodyMargin + """
            <table style="width: 400px; table-layout: fixed; border-spacing: 0">
              <col style="width: calc(25% + 10px)">
              <tr><td>a</td><td style="width: calc(50% - 10px)">b</td><td>c</td></tr>
            </table>
            """).Blocks).OfType<TableBlock>().Single();

        Assert.Equal(TableWidth.Points((100 + 10) * Px), table.ColumnWidths[0]);
        Assert.Equal(TableWidth.Points((200 - 10) * Px), table.Rows[0].Cells[1].Width);
    }

    [Fact]
    public void Image_widths()
    {
        const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var image = Flatten(Flow(NoBodyMargin + $"<img style='display: block; width: calc(50% - 10px)' src='{Pixel}'>").Blocks).OfType<ImageBlock>().Single();

        Assert.Equal(200 - (10 * Px), image.Width!.Value, 6);
    }

    [Fact]
    public void Page_size_and_margins()
    {
        var flow = HtmlToPdf.CreateFlowDocument("<style>@page { size: calc(100mm + 5mm) 200mm; margin: calc(1cm + 5mm) min(2cm, 30mm) }</style><p>x</p>");

        Assert.Equal(105 * 72 / 25.4, flow.Setup.Size.Width, 6);
        Assert.Equal(15 * 72 / 25.4, flow.Setup.Margins.Top, 6);
        Assert.Equal(20 * 72 / 25.4, flow.Setup.Margins.Left, 6);
    }

    private static FlowDocument Flow(string html) => HtmlToPdf.CreateFlowDocument(html, new HtmlConverterOptions { Page = Page });

    private static Dictionary<string, Block> Blocks(string html)
    {
        // Each test element's text is its id, so blocks can be found by their text.
        var ids = System.Text.RegularExpressions.Regex.Matches(html, "id=\"([a-z]+)\"").Select(m => m.Groups[1].Value);
        var blocks = Flatten(Flow(html).Blocks).ToList();
        return ids.Distinct().ToDictionary(id => id, id => blocks.FirstOrDefault(b => b is ParagraphBlock && Text(b) == id) ?? blocks.First(b => Text(b) == id));
    }

    private static List<ParagraphBlock> Paragraphs(string html) => [.. Flatten(Flow(html).Blocks).OfType<ParagraphBlock>()];

    private static FlexBlock Flex(string html) => Flatten(Flow(html).Blocks).OfType<FlexBlock>().First();

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b switch
        {
            ContainerBlock c => Flatten(c.Children).Prepend(b),
            FlexBlock f => Flatten(f.Items.Select(i => i.Content)).Prepend(b),
            _ => [b],
        });

    private static string Text(Block block) =>
        string.Concat(Flatten([block]).OfType<ParagraphBlock>().Select(p => string.Concat(p.Paragraph.Spans.Select(s => s.Text)))).Trim();
}

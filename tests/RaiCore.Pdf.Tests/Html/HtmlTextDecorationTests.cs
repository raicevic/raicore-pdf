using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlTextDecorationTests
{
    private const double Px = 0.75;

    [Theory]
    [InlineData("<a href='x'>t</a>", TextDecorationLines.Underline)]
    [InlineData("<u>t</u>", TextDecorationLines.Underline)]
    [InlineData("<ins>t</ins>", TextDecorationLines.Underline)]
    [InlineData("<del>t</del>", TextDecorationLines.LineThrough)]
    [InlineData("<s>t</s>", TextDecorationLines.LineThrough)]
    [InlineData("<strike>t</strike>", TextDecorationLines.LineThrough)]
    [InlineData("<span style='text-decoration: overline underline'>t</span>", TextDecorationLines.Overline | TextDecorationLines.Underline)]
    public void Elements_and_css_give_decorations(string inner, TextDecorationLines lines)
    {
        Assert.Equal(lines, Assert.Single(Span(Spans($"<p>{inner}</p>"), "t").Style.Decorations).Lines);
    }

    [Fact]
    public void Style_color_thickness_and_offset_are_mapped()
    {
        var decoration = Span(Spans("""
            <p style="font-size: 20px; color: blue"><span style="text-decoration: underline wavy red; text-decoration-thickness: 10%; text-underline-offset: 3px">t</span>
               <span style="text-decoration: line-through">c</span></p>
            """), "t").Style.Decorations[0];
        var current = Span(Spans("<p style='color: blue'><span style='text-decoration: line-through'>c</span></p>"), "c").Style.Decorations[0];

        Assert.Equal((TextDecorationStyle.Wavy, PdfColor.FromRgb(255, 0, 0)), (decoration.Style, decoration.Color));
        Assert.Equal(20 * Px / 10, decoration.Thickness!.Value, 6); // 10% of 1em.
        Assert.Equal(3 * Px, decoration.UnderlineOffset!.Value, 6);
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), current.Color);     // currentColor.
    }

    [Theory]
    [InlineData("underline wavy red 2px", TextDecorationLines.Underline, TextDecorationStyle.Wavy, "red", 2 * Px)]
    [InlineData("2px dotted overline blue", TextDecorationLines.Overline, TextDecorationStyle.Dotted, "blue", 2 * Px)]
    [InlineData("line-through 10%", TextDecorationLines.LineThrough, TextDecorationStyle.Solid, "black", 20 * Px / 10)]
    [InlineData("underline overline calc(1px + 1px) double", TextDecorationLines.Underline | TextDecorationLines.Overline, TextDecorationStyle.Double, "black", 2 * Px)]
    public void A_shorthand_with_a_thickness_sets_every_longhand(string value, TextDecorationLines lines, TextDecorationStyle style, string color, double thickness)
    {
        // #137: AngleSharp drops the shorthand with a thickness; it is split into longhands first.
        var decoration = Assert.Single(Span(Spans($"<p style='font-size: 20px'><span style='text-decoration: {value}'>t</span></p>"), "t").Style.Decorations);
        var colors = new Dictionary<string, PdfColor> { ["red"] = PdfColor.FromRgb(255, 0, 0), ["blue"] = PdfColor.FromRgb(0, 0, 255), ["black"] = PdfColor.FromRgb(0, 0, 0) };

        Assert.Equal((lines, style, colors[color]), (decoration.Lines, decoration.Style, decoration.Color));
        Assert.Equal(thickness, decoration.Thickness!.Value, 6);
    }

    [Fact]
    public void Shorthands_with_a_thickness_work_in_style_sheets_with_important()
    {
        var spans = Spans("""
            <style>
              .a { text-decoration: underline 4px !important }
              .a { text-decoration: overline }
              .b { text-decoration: none 2px }
            </style>
            <p><span class="a">a</span> <span class="b">b</span></p>
            """);

        var a = Assert.Single(Span(spans, "a").Style.Decorations);
        Assert.Equal((TextDecorationLines.Underline, 4 * Px), (a.Lines, a.Thickness!.Value));
        Assert.Empty(Span(spans, "b").Style.Decorations);
    }

    [Fact]
    public void The_thickness_from_the_shorthand_is_drawn()
    {
        var document = HtmlToPdf.Convert("<p><span style='text-decoration: underline 4px'>Thick</span></p>");
        document.CompressContent = false;
        var content = System.Text.Encoding.Latin1.GetString(document.ToArray());

        Assert.Matches(@"[\d.]+ [\d.]+ [\d.]+ 3 re\nf", content); // A solid underline is a rectangle 4px = 3pt tall.
    }

    [Fact]
    public void Diagnostics_no_longer_report_a_shorthand_with_a_thickness()
    {
        var found = new List<HtmlDiagnostic>();
        HtmlToPdf.Convert("<p style='text-decoration: underline wavy red 2px'>t</p>", new HtmlConverterOptions { Diagnostics = found.Add });

        Assert.Empty(found);
    }

    [Fact]
    public void Decorations_propagate_to_descendants_and_cannot_be_removed()
    {
        var spans = Spans("""
            <div style="text-decoration: underline"><p>para <b>bold</b> <span style="text-decoration: none">still</span></p></div>
            """);

        Assert.All(["para", "bold", "still"], text => Assert.Single(Span(spans, text).Style.Decorations));
    }

    [Fact]
    public void Nested_decorations_add_up_and_keep_the_declaring_elements_font()
    {
        var spans = Spans("<p style='font-size: 12px'><u>a <span style='font-size: 30px'>big</span> <del>gone</del></u></p>");

        var underline = Span(spans, "a").Style.Decorations[0];
        Assert.Same(underline, Span(spans, "big").Style.Decorations[0]); // One decoration: one continuous line.
        Assert.Equal(12 * Px, underline.FontSize, 6);
        Assert.Equal(2, Span(spans, "gone").Style.Decorations.Count);
    }

    [Fact]
    public void Decorations_stay_on_the_declaring_baseline_and_skip_atomic_inline_boxes()
    {
        var paragraph = Paragraphs("<p><u>H<sub>2</sub>O <span style='display: inline-block'>box</span></u></p>")[0];
        var sub = paragraph.Spans.Single(s => s.Text == "2");
        var box = (ParagraphBlock)paragraph.Spans.Single(s => s.Box is not null).Box!.Content;

        Assert.Equal(0, sub.Style.Decorations[0].BaselineShift);
        Assert.True(sub.Style.VerticalAlign.Shift < 0);
        Assert.Empty(box.Paragraph.Spans[0].Style.Decorations);
    }

    [Fact]
    public void A_raised_element_draws_its_decoration_on_its_own_baseline()
    {
        var spans = Spans("<p>x<sup style='text-decoration: underline'>2</sup></p>");

        Assert.True(Span(spans, "2").Style.Decorations[0].BaselineShift > 0);
    }

    [Fact]
    public void Running_elements_do_not_inherit_decorations()
    {
        var flow = HtmlToPdf.CreateFlowDocument("""
            <style>@page { @top-center { content: element(head); } } .head { position: running(head); }</style>
            <div style="text-decoration: underline"><div class="head">header</div><p>body</p></div>
            """);
        var running = Flatten(flow.Blocks).Select(b => b.Marker).OfType<RaiCore.Pdf.Html.Paged.RunningMarker>().Single();
        var header = Flatten(running.Blocks).OfType<ParagraphBlock>().Single();

        Assert.Empty(header.Paragraph.Spans[0].Style.Decorations);
    }

    private static List<Paragraph> Paragraphs(string html) =>
        [.. Flatten(HtmlToPdf.CreateFlowDocument(html).Blocks).OfType<ParagraphBlock>().Select(p => p.Paragraph)];

    private static List<ParagraphSpan> Spans(string html) => [.. Paragraphs(html).SelectMany(p => p.Spans)];

    private static ParagraphSpan Span(List<ParagraphSpan> spans, string text) => spans.First(s => s.Text.Trim() == text);

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);
}

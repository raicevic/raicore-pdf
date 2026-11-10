using PDFtoImage;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>CSS <c>direction</c> and <c>unicode-bidi</c>, and the HTML <c>dir</c> attribute, <c>bdi</c> and <c>bdo</c>.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlBidiTests
{
    private const string Hebrew = "\u05E9\u05DC\u05D5\u05DD"; // "shalom"

    [Theory]
    [InlineData("<p dir=\"rtl\">x</p>", ParagraphDirection.RightToLeft)]
    [InlineData("<div dir=\"rtl\"><p>x</p></div>", ParagraphDirection.RightToLeft)] // Inherited.
    [InlineData("<p style=\"direction: rtl\">x</p>", ParagraphDirection.RightToLeft)]
    [InlineData("<p dir=\"rtl\"><span dir=\"ltr\">x</span></p>", ParagraphDirection.RightToLeft)] // The block's direction.
    [InlineData("<p dir=\"auto\">x</p>", ParagraphDirection.Auto)]
    [InlineData("<p style=\"unicode-bidi: plaintext\">x</p>", ParagraphDirection.Auto)]
    [InlineData("<p>x</p>", ParagraphDirection.LeftToRight)]
    public void Paragraphs_take_the_blocks_direction(string html, ParagraphDirection expected) =>
        Assert.Equal(expected, Paragraph(html).Direction);

    [Theory]
    [InlineData("<p dir=\"rtl\">x</p>", TextAlignment.Start)]
    [InlineData("<p style=\"text-align: end\">x</p>", TextAlignment.End)]
    [InlineData("<p style=\"text-align: left\" dir=\"rtl\">x</p>", TextAlignment.Left)]
    [InlineData("<p style=\"text-align: right\">x</p>", TextAlignment.Right)]
    public void Text_align_keeps_start_and_end(string html, TextAlignment expected) =>
        Assert.Equal(expected, Paragraph(html).Alignment);

    [Theory]
    [InlineData("<span dir=\"rtl\">a</span>", "\u2067a\u2069")]
    [InlineData("<span dir=\"ltr\">a</span>", "\u2066a\u2069")]
    [InlineData("<span dir=\"auto\">a</span>", "\u2068a\u2069")]
    [InlineData("<bdi>a</bdi>", "\u2068a\u2069")]
    [InlineData("<bdo dir=\"rtl\">a</bdo>", "\u2067\u202Ea\u202C\u2069")]
    [InlineData("<span style=\"unicode-bidi: embed; direction: rtl\">a</span>", "\u202Ba\u202C")]
    [InlineData("<span style=\"unicode-bidi: bidi-override; direction: ltr\">a</span>", "\u202Da\u202C")]
    [InlineData("<span style=\"direction: rtl\">a</span>", "a")] // direction alone does nothing inline.
    public void Inline_unicode_bidi_becomes_controls(string inline, string expected) =>
        Assert.Equal($"x {expected} y", Text(Paragraph($"<p>x {inline} y</p>")));

    [Fact]
    public void Bdo_reverses_its_text()
    {
        var line = Assert.Single(Paragraph("<p><bdo dir=\"rtl\">abc</bdo> d</p>").Layout(400).Lines);

        Assert.Equal(["cba", " d"], line.Fragments.OrderBy(f => f.X).Select(f => string.Concat(f.Glyphs.Select(g => g.Text))));
    }

    [Fact]
    public void An_override_on_a_block_applies_to_its_text()
    {
        var line = Assert.Single(Paragraph("<p style=\"unicode-bidi: bidi-override; direction: rtl\">abc</p>").Layout(400).Lines);

        Assert.Equal("cba", string.Concat(line.Fragments.OrderBy(f => f.X).SelectMany(f => f.Glyphs).Select(g => g.Text)));
    }

    [Fact]
    public void Bdi_keeps_a_name_from_pulling_neighbors_into_its_direction()
    {
        // Without the isolate, ": 3" would follow the Hebrew name's direction and land on its left.
        var line = Assert.Single(Paragraph($"<p>User <bdi>{Hebrew}</bdi>: 3 posts</p>").Layout(400).Lines);
        var texts = line.Fragments.OrderBy(f => f.X).Select(f => string.Concat(f.Glyphs.Select(g => g.Text))).ToList();

        Assert.Equal(["User ", new string([.. Hebrew.Reverse()]), ": 3 posts"], texts);
    }

    [Fact]
    public void An_inline_elements_box_follows_its_direction()
    {
        var decoration = Assert.Single(Paragraph("<p dir=\"rtl\"><span style=\"border: 1px solid\">a</span></p>").Decorations);

        Assert.Equal(TextDirection.RightToLeft, decoration.Direction);
    }

    [Fact]
    public void Isolates_continue_after_a_block_inside_the_element()
    {
        var paragraphs = Flatten(HtmlToPdf.CreateFlowDocument("<div><span dir=\"rtl\">a<div>b</div>c</span></div>").Blocks).OfType<ParagraphBlock>().ToList();

        Assert.Equal("\u2067a", Text(paragraphs[0].Paragraph)); // The paragraph's end closes the isolate (rule X8).
        Assert.Equal("\u2067c\u2069", Text(paragraphs[^1].Paragraph));
    }

    [Fact]
    public void A_right_to_left_paragraph_is_drawn_on_the_right()
    {
        var pdf = HtmlToPdf.Convert($"<body style=\"margin: 0\"><p dir=\"rtl\" style=\"margin: 0; font-size: 30px\">{Hebrew}</p></body>").ToArray();
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));

        var inkX = Enumerable.Range(0, bitmap.Width).Where(x => Enumerable.Range(0, 100).Any(y => bitmap.GetPixel(x, y).Red < 128)).ToList();
        Assert.NotEmpty(inkX);
        Assert.True(inkX.Min() > bitmap.Width / 2, $"ink starts at {inkX.Min()} of {bitmap.Width}");
    }

    private static Paragraph Paragraph(string html) =>
        Flatten(HtmlToPdf.CreateFlowDocument(html).Blocks).OfType<ParagraphBlock>().First().Paragraph;

    private static string Text(Paragraph paragraph) => string.Concat(paragraph.Spans.Select(s => s.Text)).Trim();

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);
}

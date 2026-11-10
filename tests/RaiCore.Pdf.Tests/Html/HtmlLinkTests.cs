using System.Text;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>Hyperlinks (<c>a href</c>) and the destinations they jump to (<c>id</c>, <c>a name</c>).</summary>
public class HtmlLinkTests
{
    private const double Margin = 72 / 2.54 * 2; // The default 2 cm page margins.
    private const double Body = Margin + 6; // The left edge of the body's content: its default 8px margin.

    [Fact]
    public void Web_mail_and_phone_links_open_their_uris_and_script_or_empty_links_are_dropped()
    {
        var doc = HtmlToPdf.Convert(
            """
            <p><a href=" https://example.com/x?q=1 ">web</a> <a href="mailto:info@example.com">mail</a>
            <a href="tel:+4512345678">phone</a> <a href="javascript:alert(1)">script</a> <a href="">empty</a>
            <a href="#">top</a> <a>no href</a></p>
            """);

        Assert.Equal(
            ["https://example.com/x?q=1", "mailto:info@example.com", "tel:+4512345678"],
            doc.Pages[0].Links.Select(l => l.Uri!.OriginalString));
    }

    [Fact]
    public void Relative_urls_resolve_against_the_base_uri()
    {
        var html = """<p><a href="guide.html">a</a> <a href="/about">b</a> <a href="../up?x=1#frag">c</a> <a href="https://other.example/">d</a></p>""";

        var resolved = HtmlToPdf.Convert(html, new HtmlConverterOptions { BaseUri = new Uri("https://example.com/docs/") });
        var unresolved = HtmlToPdf.Convert(html);

        Assert.Equal(
            ["https://example.com/docs/guide.html", "https://example.com/about", "https://example.com/up?x=1#frag", "https://other.example/"],
            resolved.Pages[0].Links.Select(l => l.Uri!.AbsoluteUri));
        Assert.Equal(["guide.html", "/about", "../up?x=1#frag"], unresolved.Pages[0].Links.Take(3).Select(l => l.Uri!.OriginalString));
        Assert.All(unresolved.Pages[0].Links.Take(3), l => Assert.False(l.Uri!.IsAbsoluteUri));
    }

    [Fact]
    public void Fragment_links_jump_to_the_element_with_that_id_on_another_page()
    {
        var doc = HtmlToPdf.Convert(
            """
            <p><a href="#second">Go to the second chapter</a> or <a href="#caf%C3%A9">the caf&eacute;</a></p>
            <h1 id="second" style="break-before: page">Second</h1>
            <p>Text <span id="caf&eacute;">here</span></p>
            """);
        var pdf = Encoding.Latin1.GetString(doc.ToArray());

        Assert.Equal(["second", "caf\u00E9"], doc.Pages[0].Links.Select(l => l.DestinationName));
        var second = doc.NamedDestinations["second"];
        Assert.Same(doc.Pages[1], second.Page);
        Assert.InRange(second.Top!.Value, doc.Pages[1].Height - Margin - 30, doc.Pages[1].Height - Margin);
        Assert.Same(doc.Pages[1], doc.NamedDestinations["caf\u00E9"].Page);
        Assert.True(doc.NamedDestinations["caf\u00E9"].Top < second.Top);
        Assert.Contains("/Dest (second)", pdf, StringComparison.Ordinal);
        Assert.Contains("/Names <</Dests", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void Named_anchors_mark_the_line_they_are_on_even_without_text()
    {
        var doc = HtmlToPdf.Convert(
            """
            <p><a href="#inline">1</a> <a href="#empty">2</a> <a href="#image">3</a></p>
            <p style="break-before: page; width: 60px">One two three <a name="inline"></a>four five</p>
            <div><a name="empty"></a></div>
            <p><img id="image" alt="missing image" src="missing.png"></p>
            """);

        Assert.Equal(3, doc.Pages[0].Links.Count);
        var inline = doc.NamedDestinations["inline"];
        var empty = doc.NamedDestinations["empty"];
        Assert.All([inline, empty, doc.NamedDestinations["image"]], d => Assert.Same(doc.Pages[1], d.Page));

        // The anchor is on a later line than the paragraph's first, which starts at the top of the content area.
        Assert.True(inline.Top < doc.Pages[1].Height - Margin - 20);
        Assert.True(empty.Top < inline.Top);
    }

    [Fact]
    public void Links_to_unknown_ids_are_dropped()
    {
        var bytes = HtmlToPdf.ConvertToBytes("<p><a href='#nowhere'>Nowhere</a></p><p id='somewhere'>Somewhere</p>");

        Assert.DoesNotContain("/Annots", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrapped_link_has_one_rectangle_per_line()
    {
        var doc = HtmlToPdf.Convert("<p style='width: 150px'>See <a href='https://example.com/'>the documentation of the layout engine</a>.</p>");

        var links = doc.Pages[0].Links;
        Assert.True(links.Count >= 2, $"{links.Count} rectangles");
        Assert.All(links, l => Assert.Equal("https://example.com/", l.Uri!.AbsoluteUri));
        for (var i = 1; i < links.Count; i++)
        {
            Assert.Equal(links[i - 1].Y, links[i].Y + links[i].Height, 6); // Line boxes stack without gaps or overlaps.
            Assert.Equal(Body, links[i].X, 6);
        }

        Assert.True(links[0].X > Body);
    }

    [Fact]
    public void Links_around_blocks_and_block_links_cover_the_boxes()
    {
        var doc = HtmlToPdf.Convert(
            """
            <a href="https://a.example/"><div style="padding: 10px">Card</div></a>
            <a href="https://b.example/" style="display: block; width: 200px; height: 50px">Button</a>
            """);

        var links = doc.Pages[0].Links;
        Assert.Equal(2, links.Count);
        var contentWidth = doc.Pages[0].Width - (2 * Body);
        Assert.Equal((Body, contentWidth), (links[0].X, links[0].Width));
        Assert.True(links[0].Height > 15);
        Assert.Equal(150, links[1].Width, 6);
        Assert.Equal(37.5, links[1].Height, 6);
        Assert.Equal(links[0].Y, links[1].Y + links[1].Height, 6);
    }

    [Fact]
    public void Links_in_table_cells_flex_items_and_inline_blocks_are_clickable()
    {
        var doc = HtmlToPdf.Convert(
            """
            <table style="border-collapse: collapse"><tr><td style="width: 100px">Left</td><td><a href="https://cell.example/">Cell</a></td></tr></table>
            <div style="display: flex"><div style="width: 100px">Left</div><a href="https://flex.example/" style="width: 80px">Item</a></div>
            <p>Before <span style="display: inline-block; padding: 4px"><a href="https://box.example/">Box</a></span></p>
            """);

        var links = doc.Pages[0].Links;
        Assert.Equal(["https://cell.example/", "https://flex.example/", "https://box.example/"], links.Select(l => l.Uri!.AbsoluteUri));
        Assert.True(links[0].X > Body + 75);
        Assert.Equal((Body + 75, 60.0), (links[1].X, links[1].Width));
        Assert.True(links[2].X > Body + 30);
        Assert.True(links[0].Y > links[1].Y && links[1].Y > links[2].Y);
    }

    [Fact]
    public void Link_text_is_underlined_by_the_user_agent_style_sheet()
    {
        var paragraph = Paragraphs(HtmlToPdf.CreateFlowDocument("<p>Visit <a href='https://example.com/'>the site</a> now</p>").Blocks).Single();

        var link = Assert.Single(paragraph.Links);
        var linked = SpansIn(paragraph, link.Start, link.End).ToList();
        Assert.Equal("the site", string.Concat(linked.Select(s => s.Text)));
        Assert.All(linked, s => Assert.Equal(TextDecorationLines.Underline, Assert.Single(s.Style.Decorations).Lines));
        Assert.All(SpansIn(paragraph, 0, link.Start), s => Assert.Empty(s.Style.Decorations));
    }

    [Fact]
    public void Generated_content_of_a_link_is_part_of_the_link()
    {
        const string Css = "a.icon::before { content: '\\2192  ' } a.icon::after { content: ' [' attr(href) ']' } a.card::before { content: 'Card'; display: block }";
        var html = $"<style>{Css}</style><p><a class='icon' href='https://example.com/'>site</a> next</p><p><a class='card' href='https://card.example/'>text</a></p>";

        var paragraph = Paragraphs(HtmlToPdf.CreateFlowDocument(html).Blocks).First();
        var link = Assert.Single(paragraph.Links);
        Assert.Equal("\u2192 site [https://example.com/]", string.Concat(SpansIn(paragraph, link.Start, link.End).Select(s => s.Text)));

        // The block ::before is linked as a block, the text after it inline.
        var links = HtmlToPdf.Convert(html).Pages[0].Links;
        Assert.Equal(["https://example.com/", "https://card.example/", "https://card.example/"], links.Select(l => l.Uri!.AbsoluteUri));
        Assert.Equal(Body, links[1].X, 6);
        Assert.Equal(links[1].Y, links[2].Y + links[2].Height, 6);
    }

    [Fact]
    public void Passes_qpdf_structural_check() =>
        ExternalTools.AssertQpdfClean(HtmlToPdf.ConvertToBytes(
            """
            <p><a href="#end">To the end</a>, <a href="https://example.com/a b">the web</a> and <a href="mailto:x@example.com">mail</a>.</p>
            <h1 id="end" style="break-before: page">End</h1>
            """));

    private static IEnumerable<Paragraph> Paragraphs(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b switch
        {
            ParagraphBlock p => [p.Paragraph],
            ContainerBlock c => Paragraphs(c.Children),
            _ => [],
        });

    /// <summary>The spans (or their parts) within a range of the paragraph's text.</summary>
    private static IEnumerable<ParagraphSpan> SpansIn(Paragraph paragraph, int start, int end)
    {
        var position = 0;
        foreach (var span in paragraph.Spans)
        {
            var (from, to) = (Math.Max(start, position), Math.Min(end, position + span.Text.Length));
            if (from < to)
            {
                yield return span with { Text = span.Text[(from - position)..(to - position)] };
            }

            position += span.Text.Length;
        }
    }
}

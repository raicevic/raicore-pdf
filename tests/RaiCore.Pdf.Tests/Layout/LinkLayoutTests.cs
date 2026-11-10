using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Links in text and on blocks, and the named destinations they jump to. Uses a 200 x 200 pt page with 20 pt margins
/// (content from x = 20, PDF y = 180 down to 20) and 10 pt lines, so 16 lines fit on a page.
/// </summary>
public class LinkLayoutTests
{
    private const double ContentTop = 180; // PDF y of the top of the content area.

    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));
    private static readonly LinkTarget Web = LinkTarget.ToUri(new Uri("https://example.com/"));

    [Fact]
    public void A_wrapped_link_has_a_piece_on_every_line_it_covers()
    {
        var lines = new Paragraph(Text).Add("aa ").AddLink("bb\ncc\ndd", Web).Add(" ee").Layout(160).Lines;

        Assert.Equal(3, lines.Count);
        var pieces = lines.Select(l => Assert.Single(l.Links)).ToList();
        Assert.Equal((W("aa "), W("aa bb")), (pieces[0].X0, pieces[0].X1));
        Assert.Equal((0, W("cc")), (pieces[1].X0, pieces[1].X1));
        Assert.Equal((0, W("dd")), (pieces[2].X0, pieces[2].X1));
        Assert.All(pieces, p => Assert.Same(Web, p.Link.Target));
    }

    [Fact]
    public void A_link_wrapped_at_the_available_width_gets_one_rectangle_per_line_as_high_as_the_line()
    {
        var paragraph = new Paragraph(Text).Add("Read ").AddLink("the documentation of the layout engine for details", Web).Add(" here.");

        var doc = Flow(new ParagraphBlock(paragraph)).Render();
        var lines = paragraph.Layout(160).Lines;

        var links = doc.Pages[0].Links;
        Assert.True(lines.Count >= 2);
        Assert.Equal(lines.Count, links.Count);
        for (var i = 0; i < links.Count; i++)
        {
            Assert.Equal(ContentTop - ((i + 1) * 10), links[i].Y, 6);
            Assert.Equal(10, links[i].Height, 6);
            Assert.Equal(20 + lines[i].X + lines[i].Links[0].X0, links[i].X, 6);
            Assert.Same(Web.Uri, links[i].Uri);
        }

        Assert.Equal(20 + W("Read "), links[0].X, 6);
        Assert.Equal(20, links[1].X, 6); // Continued lines start at the line's start.
    }

    [Fact]
    public void A_link_split_across_pages_gets_rectangles_on_both_pages()
    {
        // Lines 14 to 17 are a link; 16 lines fit on the first page.
        var paragraph = new Paragraph(Text).Add(Lines(1, 13) + "\n").AddLink(Lines(14, 17), Web).Add("\n" + Lines(18, 20));

        var doc = Flow(new ParagraphBlock(paragraph)).Render();

        Assert.Equal([40.0, 30.0, 20.0], doc.Pages[0].Links.Select(l => l.Y));
        Assert.Equal(ContentTop - 10, Assert.Single(doc.Pages[1].Links).Y);
    }

    [Fact]
    public void A_block_link_covers_the_border_box_on_every_page()
    {
        var block = new ContainerBlock(new BoxStyle { Margin = new(0, 10, 0, 5), Padding = Thickness.All(2) }, Para(20)) { Link = Web };

        var doc = Flow(block).Render();

        var first = Assert.Single(doc.Pages[0].Links);
        var second = Assert.Single(doc.Pages[1].Links);
        Assert.Equal((25.0, 20.0, 145.0, 160.0), Rect(first));
        Assert.Equal(25, second.X);
        Assert.Equal(ContentTop, second.Y + second.Height, 6);
        Assert.True(second.Height > 10);
    }

    [Fact]
    public void Block_links_work_on_flex_items_table_cells_and_inline_boxes()
    {
        var flex = new FlexBlock(null, new FlexItem(Para(1)) { Basis = FlexBasis.Points(60) }, new FlexItem(new ContainerBlock(null, Para(1)) { Link = Web }) { Basis = FlexBasis.Points(60) });
        var table = new TableBlock { ColumnWidths = { TableWidth.Points(80), TableWidth.Points(80) } }
            .Add(new TableRow(new TableCell(Para(1)), new TableCell(new ParagraphBlock(new Paragraph(Text).AddLink("Cell", Web)))));
        var inline = new ParagraphBlock(new Paragraph(Text).Add("ab ").AddBox(new ContainerBlock(new BoxStyle { Width = 50 }, new ParagraphBlock(new Paragraph(Text).AddLink("Box", Web)))));

        var links = Flow(flex, table, inline).Render().Pages[0].Links;

        Assert.Equal(3, links.Count);
        Assert.Equal((80.0, ContentTop - 10, 60.0, 10.0), Rect(links[0])); // The second flex item.
        Assert.Equal((100.0, ContentTop - 20), (links[1].X, links[1].Y));
        Assert.Equal(W("Cell"), links[1].Width, 6);
        Assert.Equal(20 + W("ab "), links[2].X, 6); // Inside the box, moved to its place on the line.
        Assert.Equal(W("Box"), links[2].Width, 6);
    }

    [Fact]
    public void Ids_and_paragraph_anchors_become_named_destinations()
    {
        // The second paragraph's anchor is on its 18th line: 15 lines fit on the first page, so the third of the next.
        var deep = new Paragraph(Text).Add(Lines(1, 17) + "\n").AddAnchor("deep").Add(Lines(18, 20));
        var flow = Flow(
            new ParagraphBlock(new Paragraph(Text).AddLink("Jump", LinkTarget.ToDestination("deep"))) { Id = "top" },
            new ParagraphBlock(deep),
            Para(2, id: "last"));

        var doc = flow.Render();

        Assert.Equal("deep", Assert.Single(doc.Pages[0].Links).DestinationName);
        Assert.Equal((doc.Pages[0], ContentTop), Destination(doc, "top"));
        Assert.Equal((doc.Pages[1], ContentTop - 20), Destination(doc, "deep"));
        Assert.Equal((doc.Pages[1], ContentTop - 50), Destination(doc, "last"));
    }

    [Fact]
    public void A_destination_follows_its_block_to_the_next_page()
    {
        // 15 lines leave one line of space; the target needs two (orphans), so it starts the next page.
        var doc = Flow(Para(15), Para(2, id: "target"), new ContainerBlock { Id = "empty" }, Para(1)).Render();

        Assert.Equal((doc.Pages[1], ContentTop), Destination(doc, "target"));
        Assert.Equal((doc.Pages[1], ContentTop - 20), Destination(doc, "empty"));
    }

    [Fact]
    public void An_anchor_at_the_end_of_the_text_belongs_to_the_last_line_and_the_first_id_wins()
    {
        var paragraph = new Paragraph(Text).Add("One\nTwo").AddAnchor("end");

        var doc = Flow(new ParagraphBlock(paragraph), Para(1, id: "end"), Para(1, id: "twice"), Para(1, id: "twice")).Render();

        Assert.Equal((doc.Pages[0], ContentTop - 10), Destination(doc, "end"));
        Assert.Equal((doc.Pages[0], ContentTop - 30), Destination(doc, "twice"));
    }

    [Fact]
    public void Links_to_unknown_destinations_are_left_out_of_the_pdf()
    {
        var doc = Flow(new ParagraphBlock(new Paragraph(Text).AddLink("Nowhere", LinkTarget.ToDestination("missing")))).Render();

        Assert.Single(doc.Pages[0].Links);
        Assert.DoesNotContain("/Annots", Encoding.Latin1.GetString(doc.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_links_are_dropped_and_unbalanced_ones_rejected()
    {
        var paragraph = new Paragraph(Text).Add("a").BeginLink(Web).EndLink().Add("b");

        Assert.Empty(paragraph.Links);
        Assert.Empty(paragraph.Layout(100).Lines[0].Links);
        Assert.Throws<InvalidOperationException>(() => new Paragraph(Text).EndLink());
    }

    [Fact]
    public void Blocks_drawn_by_page_decorators_can_have_links()
    {
        var flow = Flow(Para(1));
        flow.PageDecorator = (canvas, page) =>
            FlowDocument.DrawBlocks(canvas, page.Setup.Size.Height, [new ParagraphBlock(new Paragraph(Text).AddLink("Home", Web))], 20, 5, 160);

        var link = Assert.Single(flow.Render().Pages[0].Links);

        Assert.Equal((20.0, 185.0, 10.0), (link.X, link.Y, link.Height));
    }

    [Fact]
    public void Passes_qpdf_structural_check()
    {
        var flow = Flow(
            new ParagraphBlock(new Paragraph(Text).Add("See ").AddLink("the end", LinkTarget.ToDestination("end")).Add(" or ").AddLink("the web", Web)),
            Para(30),
            Para(1, id: "end"));

        ExternalTools.AssertQpdfClean(flow.Render().ToArray());
    }

    private static (PdfPage Page, double? Top) Destination(PdfDocument doc, string name) =>
        (doc.NamedDestinations[name].Page, doc.NamedDestinations[name].Top);

    private static (double, double, double, double) Rect(PdfLink link) => (link.X, link.Y, link.Width, link.Height);

    private static double W(string text) => TextShaper.Shape(text, Text.Fonts).GetWidth(Text.FontSize);

    private static string Lines(int from, int to) => string.Join("\n", Enumerable.Range(from, to - from + 1).Select(i => $"Line {i}"));

    private static ParagraphBlock Para(int lines, BoxStyle? style = null, string? id = null) =>
        new(new Paragraph(Text).Add(Lines(1, lines)), style) { Id = id };

    private static FlowDocument Flow(params Block[] blocks)
    {
        var flow = new FlowDocument(Small);
        foreach (var block in blocks)
        {
            flow.Add(block);
        }

        return flow;
    }
}

using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// <see cref="FlowDocument.AnchorPlaced"/>: where paragraph anchors land as the document is rendered, at their place on
/// their line (checked against where the text after them is drawn), on pages with other margins too.
/// </summary>
public class AnchorPlacedTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly PdfFont Arabic = PdfFont.Load(Fixtures.Path("NotoSansArabic-Subset.ttf"));
    private static readonly TextStyle Text = new(Sans, 10) { LineHeight = LineHeight.Points(12) };

    private static (List<(string Id, PdfDestination Destination)> Placed, byte[] Pdf) Render(FlowDocument flow)
    {
        var placed = new List<(string, PdfDestination)>();
        flow.AnchorPlaced = (id, destination) => placed.Add((id, destination));
        var document = new PdfDocument();
        flow.RenderTo(document);
        return (placed, document.ToArray());
    }

    [Fact]
    public void An_anchor_is_where_the_text_after_it_starts_at_the_top_of_its_line()
    {
        var flow = new FlowDocument(new PageSetup(new PdfPageSize(300, 300), Thickness.All(30)));
        flow.Add(new ParagraphBlock(new Paragraph(Text).Add("First line of text.")));
        flow.Add(new ParagraphBlock(new Paragraph(Text).Add("Hello ").AddAnchor("here").Add("world").AddAnchor("end")));

        var (placed, pdf) = Render(flow);

        Assert.Equal(["here", "end"], placed.Select(p => p.Id));
        var world = Assert.Single(new TextExtractor(PdfReader.Open(pdf)).FindOnPage(0, "world"));
        var here = placed[0].Destination;
        Assert.Equal(world.Bounds.X, here.Left!.Value, 0.5);
        Assert.Equal(300 - 30 - 12, here.Top!.Value, 0.01); // The second line's top.
        Assert.True(here.Top >= world.Bounds.Top);
        Assert.Equal(world.Bounds.Right, placed[1].Destination.Left!.Value, 0.5); // At the end of the text.
        Assert.Equal(here.Top, placed[1].Destination.Top);
    }

    [Fact]
    public void Anchors_on_pages_with_other_margins_are_where_their_text_is()
    {
        var wide = new PageSetup(new PdfPageSize(300, 300), Thickness.All(30));
        var narrow = new PageSetup(new PdfPageSize(300, 300), new Thickness(30, 30, 30, 80));
        var flow = new FlowDocument(wide) { PageSetupResolver = page => page.Name == "n" ? narrow : wide };
        flow.Add(new ParagraphBlock(new Paragraph(Text).Add("On the first page.")));
        flow.Add(new ContainerBlock(new BoxStyle { BreakBefore = PageBreak.Page }, new ParagraphBlock(new Paragraph(Text).Add("A ").AddAnchor("moved").Add("word"))) { PageName = "n" });

        var (placed, pdf) = Render(flow);

        var (id, destination) = Assert.Single(placed);
        Assert.Equal("moved", id);
        Assert.Same(destination.Page.Document!.Pages[1], destination.Page);
        var word = Assert.Single(new TextExtractor(PdfReader.Open(pdf)).FindOnPage(1, "word"));
        Assert.Equal(word.Bounds.X, destination.Left!.Value, 0.5);
        Assert.True(destination.Left > 80);
    }

    [Fact]
    public void An_anchor_before_right_to_left_text_is_at_its_right_edge()
    {
        var style = new TextStyle([Arabic, Sans], 12);
        var flow = new FlowDocument(new PageSetup(new PdfPageSize(300, 300), Thickness.All(30)));
        flow.Add(new ParagraphBlock(new Paragraph(style) { Direction = ParagraphDirection.RightToLeft }
            .Add("\u0633\u0644\u0627\u0645 ").AddAnchor("second").Add("\u0645\u0631\u062D\u0628\u0627")));

        var (placed, _) = Render(flow);

        // The second word is drawn left of the first: its start (the anchor) is its right edge, left of the first word.
        var line = new Paragraph(style) { Direction = ParagraphDirection.RightToLeft }.Add("\u0633\u0644\u0627\u0645 ").Add("\u0645\u0631\u062D\u0628\u0627").Layout(240).Lines[0];
        var second = line.Fragments.OrderBy(f => f.X).First();
        Assert.Equal(30 + line.X + second.X + second.Width, Assert.Single(placed).Destination.Left!.Value, 0.5);
    }

    [Fact]
    public void An_anchor_before_an_image_is_at_the_image_and_a_flow_without_a_listener_renders_as_before()
    {
        var image = PdfImage.Load(Fixtures.Read(Path.Combine("PngSuite", "basn2c08.png")));
        var flow = new FlowDocument(new PageSetup(new PdfPageSize(300, 300), Thickness.All(30)));
        var paragraph = new Paragraph(Text).Add("Pic ").AddAnchor("image").AddImage(image, 20, 10).Add(" after");
        flow.Add(new ParagraphBlock(paragraph));

        var (placed, _) = Render(flow);

        var line = paragraph.Layout(240).Lines[0];
        Assert.Equal(30 + line.X + Assert.Single(line.Images).X, Assert.Single(placed).Destination.Left!.Value, 0.01);

        var plain = new FlowDocument(new PageSetup(new PdfPageSize(300, 300), Thickness.All(30)));
        plain.Add(new ParagraphBlock(new Paragraph(Text).Add("Pic ").AddAnchor("image").AddImage(image, 20, 10).Add(" after")));
        var document = new PdfDocument();
        plain.RenderTo(document);
        Assert.True(document.NamedDestinations.ContainsKey("image"));
    }
}

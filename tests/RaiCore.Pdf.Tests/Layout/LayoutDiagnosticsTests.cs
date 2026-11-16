using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>Content that does not fit (#59): FlowDocument.Diagnostics.</summary>
public class LayoutDiagnosticsTests
{
    private static readonly TextStyle Style = new(PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf")), 10);

    /// <summary>A 30 x 1500 point image: taller than an A4 page.</summary>
    private static readonly PdfImage Tall = PdfImage.LoadSvg("<svg xmlns='http://www.w3.org/2000/svg' width='30pt' height='1500pt'><rect width='30' height='1500' fill='teal'/></svg>");

    private static List<LayoutDiagnostic> Render(FlowDocument flow)
    {
        var found = new List<LayoutDiagnostic>();
        flow.Diagnostics = found.Add;
        flow.Render();
        return found;
    }

    private static ParagraphBlock LongWord() => new(new Paragraph(Style).Add("Short Donaudampfschifffahrtsgesellschaftskapitaenswitwenrente end"));

    [Fact]
    public void A_line_wider_than_its_box_overflows()
    {
        var word = LongWord();
        var flow = new FlowDocument(PageSetup.A4);
        flow.Add(new ContainerBlock(new BoxStyle { Width = 100 }, word));

        var diagnostic = Assert.Single(Render(flow));

        Assert.Equal(LayoutDiagnosticKind.Overflow, diagnostic.Kind);
        Assert.Same(word, diagnostic.Block);
        Assert.Equal(0, diagnostic.Page);
        Assert.Contains("wider than its box (100 pt)", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Overflow_that_a_box_clips_on_purpose_is_not_reported()
    {
        var flow = new FlowDocument(PageSetup.A4);
        flow.Add(new ContainerBlock(new BoxStyle { Width = 100, ClipOverflow = true }, LongWord()));

        Assert.Empty(Render(flow));
    }

    [Fact]
    public void A_table_wider_than_its_container_overflows()
    {
        var table = new TableBlock().Add(new TableRow(
            new TableCell(new Paragraph(Style).Add("Unbreakablewordnumberone")), new TableCell(new Paragraph(Style).Add("Unbreakablewordnumbertwo"))));
        var flow = new FlowDocument(PageSetup.A4);
        flow.Add(new ContainerBlock(new BoxStyle { Width = 100 }, table));

        var diagnostic = Assert.Single(Render(flow));

        Assert.Equal((LayoutDiagnosticKind.Overflow, (Block)table), (diagnostic.Kind, diagnostic.Block));
        Assert.Contains("wider than the 100 pt it has", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("columns cannot be narrower than their content", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_with_a_fixed_width_overflows_when_its_columns_need_more()
    {
        var table = new TableBlock(new BoxStyle { Width = 100 }).Add(new TableRow(
            new TableCell(new Paragraph(Style).Add("Unbreakablewordnumberone")), new TableCell(new Paragraph(Style).Add("Unbreakablewordnumbertwo"))));
        var flow = new FlowDocument(PageSetup.A4);
        flow.Add(table);

        var diagnostic = Assert.Single(Render(flow));

        Assert.Equal((LayoutDiagnosticKind.Overflow, (Block)table), (diagnostic.Kind, diagnostic.Block));
        Assert.Contains("than its 100 pt width", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_box_with_a_fixed_width_wider_than_its_container_overflows()
    {
        var inner = new ContainerBlock(new BoxStyle { Width = 150, Margin = new Thickness(0, 0, 0, 10) }, new ParagraphBlock(new Paragraph(Style).Add("x")));
        var flow = new FlowDocument(PageSetup.A4);
        flow.Add(new ContainerBlock(new BoxStyle { Width = 100 }, inner));

        var diagnostic = Assert.Single(Render(flow));

        Assert.Equal((LayoutDiagnosticKind.Overflow, (Block)inner), (diagnostic.Kind, diagnostic.Block));
        Assert.Contains("60 pt wider than the 100 pt it has", diagnostic.Message, StringComparison.Ordinal); // Its margin counts.
    }

    [Fact]
    public void Overflow_in_a_table_cell_names_the_page_of_the_table()
    {
        var word = LongWord();
        var flow = new FlowDocument(PageSetup.A4);
        flow.Add(new ParagraphBlock(new Paragraph(Style).Add("First page"), new BoxStyle { BreakAfter = PageBreak.Page }));
        flow.Add(new TableBlock(new BoxStyle { Width = 300 }).Add(new TableRow(new TableCell(new ContainerBlock(new BoxStyle { Width = 80 }, word)))));

        var diagnostic = Assert.Single(Render(flow));

        Assert.Same(word, diagnostic.Block);
        Assert.Equal(1, diagnostic.Page);
    }

    [Fact]
    public void A_line_taller_than_a_page_is_clipped()
    {
        var paragraph = new ParagraphBlock(new Paragraph(Style).Add("Before ").AddImage(Tall, 30, 1000).Add(" after"));
        var flow = new FlowDocument(PageSetup.A4);
        flow.Add(paragraph);

        var diagnostic = Assert.Single(Render(flow));

        Assert.Equal((LayoutDiagnosticKind.Clipped, (Block)paragraph), (diagnostic.Kind, diagnostic.Block));
        Assert.Contains("more than a whole page", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_taller_than_a_page_is_scaled_to_fit_and_one_that_fits_is_not()
    {
        var tall = new ImageBlock(Tall);
        var flow = new FlowDocument(PageSetup.A4);
        flow.Add(tall);
        flow.Add(new ImageBlock(Tall, 30, 300));

        var diagnostic = Assert.Single(Render(flow));

        Assert.Equal((LayoutDiagnosticKind.ScaledToFit, (Block)tall), (diagnostic.Kind, diagnostic.Block));
        Assert.Contains("1500 pt tall", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostics_come_once_per_render_and_not_from_counting_pages()
    {
        var flow = new FlowDocument(PageSetup.A4);
        flow.Add(new ContainerBlock(new BoxStyle { Width = 100 }, LongWord()));
        var found = new List<LayoutDiagnostic>();
        flow.Diagnostics = found.Add;

        flow.CountPages();
        flow.MapPages();
        Assert.Empty(found);

        flow.Render();
        flow.Render();
        Assert.Equal(2, found.Count); // Once per render.
    }
}

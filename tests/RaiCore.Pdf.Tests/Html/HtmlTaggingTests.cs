using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Tests.Tagging;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlTaggingTests
{
    private const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private const string Sample = $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <title>Accessible report</title>
          <style>
            @page { @bottom-center { content: "Page " counter(page) } }
            body { font-family: sans-serif; }
            .box { background: #eef; border: 1px solid #99c; padding: 4px; }
            td, th { border: 1px solid #999; }
          </style>
        </head>
        <body>
          <h1>Quarterly report</h1>
          <p class="box">Read the <a href="https://example.com/q3">full figures</a> online.</p>
          <h2>Highlights</h2>
          <ol>
            <li>Revenue grew</li>
            <li>Costs fell</li>
          </ol>
          <ul><li><p>Nested body</p></li></ul>
          <p><img src="{{Pixel}}" alt="Company logo" width="20" height="20"> Logo inline, and <img src="{{Pixel}}" alt="" width="5" height="5"> a spacer.</p>
          <img src="{{Pixel}}" alt="Revenue chart" style="display:block; width: 100px; height: 50px">
          <hr>
          <table>
            <thead><tr><th>Region</th><th>Sales</th></tr></thead>
            <tbody>
              <tr><th>North</th><td>10</td></tr>
              <tr><th>South</th><td>12</td></tr>
            </tbody>
          </table>
          <p lang="da">Tak for i dag.</p>
        </body>
        </html>
        """;

    private static readonly PageSetup SmallPage = new(new PdfPageSize(300, 200), Thickness.All(20));

    private static byte[] Convert(string html, bool pdfUA = false, PageSetup? page = null) =>
        HtmlToPdf.ConvertAsync(html, new HtmlConverterOptions { Tagged = true, PdfUA = pdfUA, Page = page ?? PageSetup.A4 }).GetAwaiter().GetResult().ToArray();

    private static byte[] ConvertUntagged(string html) => HtmlToPdf.ConvertAsync(html).GetAwaiter().GetResult().ToArray();

    [Fact]
    public void Html_semantics_become_the_structure_tree()
    {
        var root = TaggedPdfInspector.Inspect(Convert(Sample));

        Assert.Equal(
            "Document(H1 P(Link) H2 L(LI(Lbl LBody) LI(Lbl LBody)) L(LI(Lbl LBody(P))) P(Figure) Figure Table(THead(TR(TH(P) TH(P))) TBody(TR(TH(P) TD(P)) TR(TH(P) TD(P)))) P)",
            root.ToString());
        Assert.Equal("Quarterly report", root.Children[0].Text);
        Assert.Equal("Read the full figures online.", root.Children[1].Text.Trim());
        Assert.Equal("full figures", root.Children[1].Children[0].Text);

        // Backgrounds, borders and underlines are artifacts: text elements' marked content holds text and no paths.
        Assert.All(root.Descendants().Where(e => e.Type != "Figure"), e => Assert.All(e.ContentTexts, text => Assert.NotEqual(string.Empty, text)));
        Assert.All(root.Descendants(), e => Assert.True(e.PaintedPaths == 0, $"{e.Type} \"{e.Text}\" paints {e.PaintedPaths} paths."));
    }

    [Fact]
    public void Links_carry_their_text_as_description()
    {
        var link = TaggedPdfInspector.Inspect(Convert(Sample)).Descendants().Single(e => e.Type == "Link");
        var annotation = Assert.Single(link.Annotations);
        Assert.Equal("full figures", RaiCore.Pdf.Reading.PdfText.Decode(Assert.IsType<Objects.PdfString>(annotation["Contents"]).Bytes));
    }

    [Fact]
    public void Lists_have_labels_bodies_and_numbering()
    {
        var root = TaggedPdfInspector.Inspect(Convert(Sample));
        var ordered = root.Children.First(e => e.Type == "L");
        Assert.Equal("Decimal", (ordered.Dictionary["A"] as Objects.PdfDictionary)?["ListNumbering"] is Objects.PdfName n ? n.Value : null);
        Assert.Equal("1.", ordered.Children[0].Children[0].Text.Trim(' ', ' '));
        Assert.Equal("Revenue grew", ordered.Children[0].Children[1].Text);
    }

    [Fact]
    public void Figures_have_alternate_text_and_decorative_images_are_artifacts()
    {
        var root = TaggedPdfInspector.Inspect(Convert(Sample));
        var figures = root.Descendants().Where(e => e.Type == "Figure").ToList();
        Assert.Equal(["Company logo", "Revenue chart"], figures.Select(f => f.Entry("Alt")));
        Assert.All(figures, figure => Assert.NotEmpty(figure.Content)); // The image is drawn in the figure.
    }

    [Fact]
    public void Header_cells_have_scopes_and_languages_are_kept()
    {
        var root = TaggedPdfInspector.Inspect(Convert(Sample));
        var headers = root.Descendants().Where(e => e.Type == "TH").Select(e => ((e.Dictionary["A"] as Objects.PdfDictionary)?["Scope"] as Objects.PdfName)?.Value).ToList();
        Assert.Equal(["Column", "Column", "Row", "Row"], headers);
        Assert.Equal("da", root.Children[^1].Entry("Lang"));
        Assert.Equal("Tak for i dag.", root.Children[^1].Text);
    }

    [Fact]
    public void Header_cells_in_header_rows_are_column_headers_even_beside_data_cells()
    {
        const string html = "<html lang=\"en\"><head><title>T</title></head><body><table><thead><tr><th>A</th><td>b</td></tr></thead><tbody><tr><th>R</th><td>1</td></tr></tbody></table></body></html>";
        var root = TaggedPdfInspector.Inspect(Convert(html));
        var scopes = root.Descendants().Where(e => e.Type == "TH").Select(e => ((e.Dictionary["A"] as Objects.PdfDictionary)?["Scope"] as Objects.PdfName)?.Value);
        Assert.Equal(["Column", "Row"], scopes);
    }

    [Fact]
    public void Repeated_table_headers_and_page_margin_boxes_are_artifacts()
    {
        var rows = string.Concat(Enumerable.Range(1, 30).Select(i => $"<tr><td>Row {i}</td></tr>"));
        var html = $"<html lang=\"en\"><head><title>T</title><style>@page {{ @top-center {{ content: \"Running head\" }} }}</style></head><body><table><thead><tr><th>Head</th></tr></thead><tbody>{rows}</tbody></table></body></html>";
        var bytes = Convert(html, page: SmallPage);
        var root = TaggedPdfInspector.Inspect(bytes);

        Assert.True(PdfReader.Open(bytes).Pages.Count > 2);
        var header = Assert.Single(root.Descendants(), e => e.Type == "TH");
        Assert.Equal("Head", header.Text); // Once, not once per page.
        Assert.Equal(30, root.Descendants().Count(e => e.Type == "TD"));
        Assert.DoesNotContain(root.Descendants(), e => e.Text.Contains("Running head", StringComparison.Ordinal));
    }

    [Fact]
    public void Untagged_conversion_is_unchanged_by_tags()
    {
        var text = System.Text.Encoding.Latin1.GetString(ConvertUntagged(Sample));
        Assert.DoesNotContain("/StructTreeRoot", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/MarkInfo", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Flow_documents_get_default_tags()
    {
        var flow = new FlowDocument(SmallPage);
        var style = new TextStyle(PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf")), 10);
        flow.Add(new ParagraphBlock(new Paragraph(style).Add("Title"), new BoxStyle { Background = PdfColor.FromRgb(230, 230, 230) }) { Tag = StructureTag.Heading(1) });
        flow.Add(new ParagraphBlock(new Paragraph(style).Add("Body text")));
        flow.Add(new ContainerBlock(new BoxStyle(), new ParagraphBlock(new Paragraph(style).Add("Decor"))) { Tag = StructureTag.Artifact });
        flow.Add(new TableBlock().Add(new TableRow(new TableCell(new Paragraph(style).Add("Head"))) { IsHeader = true }).Add(new TableRow(new TableCell(new Paragraph(style).Add("Cell")))));
        flow.PageDecorator = (canvas, info) => canvas.DrawText($"{info.Number}", style.Fonts[0], 8, 140, 10);
        var doc = new PdfDocument { Tagged = true, CompressContent = false };
        flow.RenderTo(doc);

        var root = TaggedPdfInspector.Inspect(doc.ToArray());
        Assert.Equal("Document(H1 P Table(THead(TR(TH(P))) TBody(TR(TD(P)))))", root.ToString());
        Assert.Equal("Column", ((root.Children[2].Descendants().Single(e => e.Type == "TH").Dictionary["A"] as Objects.PdfDictionary)?["Scope"] as Objects.PdfName)?.Value);
        Assert.Equal(0, root.Children[0].PaintedPaths); // Its background is an artifact.
        Assert.Equal("Title", root.Children[0].Text);
        Assert.Equal("Body text", root.Children[1].Text);
    }

    [Fact]
    public void The_sample_validates_as_PdfUA_with_veraPDF()
    {
        ExternalTools.AssertVeraPdfCompliant(Convert(Sample, pdfUA: true), "ua1");
    }

    [Fact]
    public void A_long_table_validates_as_PdfUA_with_veraPDF()
    {
        var rows = string.Concat(Enumerable.Range(1, 40).Select(i => $"<tr><th scope=\"row\">{i}</th><td style=\"opacity:.6\">Item {i}</td></tr>"));
        var html = $"<html lang=\"en\"><head><title>Long table</title></head><body><table><thead><tr><th>#</th><th>Item</th></tr></thead><tbody>{rows}</tbody><tfoot><tr><td colspan=\"2\">Total</td></tr></tfoot></table></body></html>";
        ExternalTools.AssertVeraPdfCompliant(Convert(html, pdfUA: true, page: SmallPage), "ua1");
    }
}

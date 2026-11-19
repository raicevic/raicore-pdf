using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Documents.Rendering;
using RaiCore.Pdf.Layout;
using static RaiCore.Pdf.Tests.WordDocuments.Docx;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>The renderer's layout blocks for model content: borders, images and spacing.</summary>
public class DocumentRendererTests
{
    private static List<Block> Render(byte[] docx, double width = 468)
    {
        var context = new ConversionContext(ResourceLimits.Default, null, CancellationToken.None);
        var document = Read(docx);
        return new DocumentRenderer(document, new DocxConverterOptions(), context).RenderBlocks(document.Sections[0].Blocks, width, inTable: false);
    }

    [Fact]
    public void Table_cells_take_the_outer_borders_at_the_edges_and_the_inside_borders_between()
    {
        var blocks = Render(Document(
            """
            <w:tbl><w:tblPr><w:tblBorders><w:top w:val="single" w:sz="24"/><w:left w:val="single" w:sz="16"/><w:bottom w:val="single" w:sz="12"/>
              <w:right w:val="single" w:sz="8"/><w:insideH w:val="single" w:sz="4"/><w:insideV w:val="dotted" w:sz="2"/></w:tblBorders></w:tblPr>
              <w:tblGrid><w:gridCol w:w="2000"/><w:gridCol w:w="2000"/></w:tblGrid>
              <w:tr><w:tc><w:p/></w:tc><w:tc><w:tcPr><w:tcBorders><w:top w:val="double" w:sz="4"/></w:tcBorders></w:tcPr><w:p/></w:tc></w:tr>
              <w:tr><w:tc><w:p/></w:tc><w:tc><w:p/></w:tc></w:tr></w:tbl>
            """));
        var table = Assert.IsType<TableBlock>(Assert.Single(blocks));
        var topLeft = table.Rows[0].Cells[0].Style.Border;
        Assert.Equal(3, topLeft.Top.Width);
        Assert.Equal(2, topLeft.Left.Width);
        Assert.Equal(0.25, topLeft.Right.Width);
        Assert.Equal(BorderStyle.Dotted, topLeft.Right.Style);
        Assert.Equal(0.5, topLeft.Bottom.Width);
        Assert.Equal(BorderStyle.Double, table.Rows[0].Cells[1].Style.Border.Top.Style); // The cell's own.
        Assert.Equal(1, table.Rows[0].Cells[1].Style.Border.Right.Width);
        var bottomLeft = table.Rows[1].Cells[0].Style.Border;
        Assert.Equal(0.5, bottomLeft.Top.Width);
        Assert.Equal(1.5, bottomLeft.Bottom.Width);
    }

    [Fact]
    public void Paragraphs_with_the_same_borders_share_a_box_with_the_border_between()
    {
        const string pPr = "<w:pBdr><w:top w:val=\"single\" w:sz=\"8\"/><w:bottom w:val=\"single\" w:sz=\"8\"/><w:between w:val=\"single\" w:sz=\"4\"/></w:pBdr>";
        var blocks = Render(Document(P("one", pPr) + P("two", pPr) + P("three")));
        var (first, second, third) = (blocks[0].Style.Border, blocks[1].Style.Border, blocks[2].Style.Border);
        Assert.Equal(1, first.Top.Width);
        Assert.Equal(0, first.Bottom.Width);
        Assert.Equal(0.5, second.Top.Width); // The border between them.
        Assert.Equal(1, second.Bottom.Width);
        Assert.Equal(0, third.Top.Width);
    }

    [Fact]
    public void Images_wider_than_the_column_are_scaled_down_keeping_their_proportions()
    {
        var png = System.Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAADElEQVR4nGP8zwACAAYIAQFazwZIAAAAAElFTkSuQmCC");
        var blocks = Render(Document(
            "<w:p><w:r><w:drawing><wp:anchor><wp:extent cx=\"12700000\" cy=\"6350000\"/><wp:wrapTopAndBottom/><wp:docPr id=\"1\" name=\"p\"/><a:graphic>"
            + "<a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:blipFill><a:blip r:embed=\"rI\"/></pic:blipFill></pic:pic>"
            + "</a:graphicData></a:graphic></wp:anchor></w:drawing></w:r></w:p>",
            parts: [("word/media/a.png", png)], relationships: [("rI", "image", "media/a.png", false)]), width: 400);
        var image = Assert.IsType<ImageBlock>(blocks[0]);
        Assert.Equal(400, image.Width!.Value, 6); // 1000 by 500 points in a 400-point column.
        Assert.Equal(200, image.Height!.Value, 6);
    }
}

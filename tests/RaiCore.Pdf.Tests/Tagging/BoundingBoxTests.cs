using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Tests.Reading;

namespace RaiCore.Pdf.Tests.Tagging;

public class BoundingBoxTests
{
    private const double Kappa = 0.5522847498;

    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));

    private static PdfDocument Tagged() => new() { Tagged = true, StrictTagging = true, Language = "en-US", Info = { Title = "Boxes" } };

    [Fact]
    public void A_figure_drawn_with_paths_gets_the_box_of_its_fills_and_strokes()
    {
        var doc = Tagged();
        var canvas = doc.AddPage().Canvas;
        var ellipse = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        var outline = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        canvas.BeginTag(ellipse);
        Ellipse(canvas, 200, 300, 50, 30);
        canvas.Fill().EndTag();
        canvas.BeginTag(outline).SetLineWidth(2).MoveTo(100, 100).LineTo(140, 120).Stroke().EndTag();
        var arch = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        var thin = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        canvas.BeginTag(arch).MoveTo(300, 100).CurveTo(300, 140, 340, 140, 340, 100).ClosePath().LineTo(320, 90).Fill().EndTag();
        canvas.SaveState().SetLineWidth(10).RestoreState().BeginTag(thin).MoveTo(400, 100).LineTo(400, 120).Stroke().EndTag();
        var closed = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        canvas.BeginTag(closed).MoveTo(300, 200).LineTo(400, 250).ClosePath().CurveTo(300, 160, 300, 160, 300, 200).Fill().EndTag();

        var root = TaggedPdfInspector.Inspect(doc.ToArray());

        AssertBox([150, 270, 250, 330], BBox(root.Children[0]));
        AssertBox([98.5, 98.5, 141.5, 121.5], BBox(root.Children[1])); // Three quarters of the line width around it.
        AssertBox([300, 90, 340, 130], BBox(root.Children[2])); // The curve's top, not its control points; closing returns to its start.
        AssertBox([398.5, 98.5, 401.5, 121.5], BBox(root.Children[3])); // Width 2 again: the 10 set inside SaveState is gone.
        AssertBox([300, 170, 400, 250], BBox(root.Children[4])); // After closing, the curve starts at the subpath's start.
    }

    [Fact]
    public void The_box_follows_transformations_and_groups_and_is_cut_to_the_clip()
    {
        var doc = Tagged();
        var canvas = doc.AddPage().Canvas;
        var scaled = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        var clipped = doc.StructureRoot.AddChild(PdfStructureTypes.Formula);
        var faded = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);

        canvas.BeginTag(scaled).SaveState().Transform(2, 0, 0, 2, 10, 20).Rectangle(0, 0, 10, 10).Fill().RestoreState().EndTag();

        // The clip is a path that needs no tag; only what is painted inside it counts.
        canvas.SaveState().Rectangle(300, 300, 20, 20).Clip();
        canvas.BeginTag(clipped).Rectangle(250, 250, 200, 200).Fill().Rectangle(0, 0, 10, 10).Fill().EndTag().RestoreState();

        canvas.SaveState().Transform(1, 0, 0, 1, 400, 500).BeginGroup(0.5).BeginTag(faded).Rectangle(0, 0, 30, 40).Fill().EndTag().EndGroup().RestoreState();

        // A clip set inside a group ends with it.
        var after = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        canvas.BeginGroup(0.5).Rectangle(0, 0, 1, 1).Clip().EndGroup();
        canvas.BeginTag(after).Rectangle(100, 100, 10, 10).Fill().EndTag();

        // A gradient fills the clip.
        var gradient = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        canvas.SaveState().Rectangle(500, 600, 40, 30).Clip().BeginTag(gradient)
            .PaintShading(new AxialGeometry(500, 600, 540, 600), [(0, new PdfColor(1, 0, 0)), (1, new PdfColor(0, 0, 1))]).EndTag().RestoreState();

        var root = TaggedPdfInspector.Inspect(doc.ToArray());

        AssertBox([10, 20, 30, 40], BBox(root.Children[0]));
        AssertBox([300, 300, 320, 320], BBox(root.Children[1]));
        AssertBox([400, 500, 430, 540], BBox(root.Children[2]));
        AssertBox([100, 100, 110, 110], BBox(root.Children[3]));
        AssertBox([500, 600, 540, 630], BBox(root.Children[4]));
    }

    [Fact]
    public void Text_and_images_count_with_their_advance_and_rectangle()
    {
        var doc = Tagged();
        var canvas = doc.AddPage().Canvas;
        var chart = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        var image = PdfImage.LoadSvg("""<svg xmlns="http://www.w3.org/2000/svg" width="40" height="20"><rect width="40" height="20" fill="teal"/></svg>""");
        canvas.BeginTag(chart).DrawImage(image, 100, 400, 80, 40);
        canvas.BeginTag(chart.AddChild(PdfStructureTypes.Caption)).DrawText("Sales", Sans, 10, 100, 380).EndTag().EndTag();

        var box = BBox(TaggedPdfInspector.Inspect(doc.ToArray()).Children[0])!;

        // The caption is inside the figure, so it counts: the box reaches from the text's descent to the image's top.
        Assert.Equal(100, box[0], 3);
        Assert.Equal(Math.Max(180, 100 + Sans.MeasureText("Sales", 10)), box[2], 3);
        Assert.True(box[1] <= 380 + (Sans.Descent * 10), $"bottom {box[1]}");
        Assert.Equal(440, box[3], 3);
    }

    [Fact]
    public void Shaped_glyphs_count_with_their_offsets()
    {
        var doc = Tagged();
        var figure = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        PositionedGlyph[] glyphs = [new(1, 0.5, -1, 0, "a"), new(2, 0.5, 0, 0.5, "b")];
        doc.AddPage().Canvas.BeginTag(figure).DrawGlyphs(glyphs, Sans, 10, 100, 200).EndTag();

        var box = BBox(TaggedPdfInspector.Inspect(doc.ToArray()).Children[0])!;

        Assert.Equal(90, box[0], 3); // The first glyph is moved a font size to the left.
        Assert.Equal(110, box[2], 3);
        Assert.True(box[3] >= 200 + 5 + (Sans.Ascent * 10), $"top {box[3]}"); // The second is raised half a font size.
    }

    [Fact]
    public void Tables_get_a_box_and_other_elements_get_none_unless_one_is_set()
    {
        var doc = Tagged();
        var canvas = doc.AddPage().Canvas;
        var table = doc.StructureRoot.AddChild(PdfStructureTypes.Table);
        var header = table.AddChild(PdfStructureTypes.TableRow).AddChild(PdfStructureTypes.TableHeaderCell);
        header.Scope = PdfTableScope.Column;
        header.BoundingBox = new PdfRectangle(50, 700, 100, 20);
        var paragraph = doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        var placed = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        placed.BoundingBox = new PdfRectangle(1, 2, 3, 4);
        canvas.BeginTag(header).Rectangle(60, 705, 30, 10).Fill().EndTag()
            .BeginTag(paragraph).Rectangle(60, 600, 30, 10).Fill().EndTag()
            .BeginTag(placed).Rectangle(60, 500, 30, 10).Fill().EndTag();

        var root = TaggedPdfInspector.Inspect(doc.ToArray());
        var th = root.Children[0].Descendants().Single(e => e.Type == "TH");

        AssertBox([60, 705, 90, 715], BBox(root.Children[0]));
        AssertBox([50, 700, 150, 720], BBox(th)); // Set on a cell, next to its Table attributes.
        Assert.Equal("Column", (Attributes(th).Single(a => a["O"] is PdfName { Value: "Table" })["Scope"] as PdfName)?.Value);
        Assert.Null(BBox(root.Children[1]));
        AssertBox([1, 2, 4, 6], BBox(root.Children[2])); // The box set wins over the one drawn.
    }

    [Fact]
    public void Imported_pages_count_as_drawn_and_boxes_are_measured_from_the_media_box()
    {
        var source = PdfReader.Open(TestPdf.Pages("/MediaBox [100 200 400 500]", ("", "0 0 1 rg 100 200 300 300 re f")));
        var doc = Tagged();
        var imported = doc.ImportPages(source)[0];
        var stamp = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        var mark = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        imported.Canvas.BeginTag(stamp).DrawPage(source.Pages[0], 10, 20, 30, 30).EndTag()
            .BeginTag(mark).Rectangle(0, 0, 50, 50).Fill().EndTag();

        var root = TaggedPdfInspector.Inspect(doc.ToArray());

        AssertBox([110, 220, 140, 250], BBox(root.Children[0]));
        AssertBox([100, 200, 150, 250], BBox(root.Children[1]));
    }

    [Fact]
    public void A_figure_drawn_on_several_pages_gets_no_box()
    {
        var doc = Tagged();
        var figure = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        var first = doc.AddPage();
        first.Canvas.BeginTag(figure).Rectangle(10, 10, 10, 10).Fill().EndTag();
        doc.AddPage().Canvas.BeginTag(figure).Rectangle(10, 10, 10, 10).Fill().EndTag();
        first.Canvas.BeginTag(figure).Rectangle(30, 30, 10, 10).Fill().EndTag(); // Back on the first page: still several.

        Assert.Null(BBox(TaggedPdfInspector.Inspect(doc.ToArray()).Children[0]));
    }

    [Fact]
    public void Layout_figures_get_the_box_they_are_placed_in()
    {
        var flow = new FlowDocument(new PageSetup(new PdfPageSize(300, 200), Thickness.All(20)));
        var style = new TextStyle(Sans, 10);
        flow.Add(new ImageBlock(PdfImage.Load(Fixtures.Path("Images/rgb.jpg")), 60, 40) { Tag = new StructureTag(PdfStructureTypes.Figure) { AlternateText = "Photo" } });
        flow.Add(new ParagraphBlock(new Paragraph(style).Add("After")));
        var doc = Tagged();
        flow.RenderTo(doc);

        var root = TaggedPdfInspector.Inspect(doc.ToArray());

        Assert.Equal("Document(Figure P)", root.ToString());
        AssertBox([20, 140, 80, 180], BBox(root.Children[0]));
    }

    [Fact]
    public async Task Html_images_get_a_box_of_their_size()
    {
        const string Svg = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='40' height='20'%3E%3Crect width='40' height='20' fill='teal'/%3E%3C/svg%3E";
        var html = $"<html lang=\"en\"><head><title>Image</title></head><body><p>Before</p><img alt=\"Teal\" width=\"40\" height=\"20\" src=\"{Svg}\"></body></html>";
        var options = new HtmlConverterOptions { Tagged = true, PdfUA = true, StrictTagging = true };

        var bytes = (await HtmlToPdf.ConvertAsync(html, options, TestContext.Current.CancellationToken)).ToArray();
        var figure = TaggedPdfInspector.Inspect(bytes).Descendants().Single(e => e.Type == "Figure");
        var box = BBox(figure)!;

        Assert.Equal(56.69 + 6, box[0], 1); // The page margin and the body's 8px margin.
        Assert.Equal(30, box[2] - box[0], 1); // 40 CSS pixels are 30 points.
        Assert.Equal(15, box[3] - box[1], 1);
        ExternalTools.AssertVeraPdfCompliant(bytes, "ua1");
    }

    /// <summary>An ellipse as four Bezier curves, whose control points lie on its bounding box.</summary>
    private static void Ellipse(PdfCanvas canvas, double cx, double cy, double rx, double ry)
    {
        var (kx, ky) = (Kappa * rx, Kappa * ry);
        canvas.MoveTo(cx + rx, cy)
            .CurveTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry)
            .CurveTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy)
            .CurveTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry)
            .CurveTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy)
            .ClosePath();
    }

    private static List<PdfDictionary> Attributes(InspectedElement element) => element.Dictionary["A"] switch
    {
        PdfDictionary single => [single],
        PdfArray several => [.. several.Items.OfType<PdfDictionary>()],
        _ => [],
    };

    /// <summary>The element's Layout attribute <c>/BBox</c> as [left, bottom, right, top], or <c>null</c>.</summary>
    private static double[]? BBox(InspectedElement element)
    {
        var layout = Attributes(element).FirstOrDefault(a => a["O"] is PdfName { Value: "Layout" });
        return layout?["BBox"] is PdfArray box
            ? [.. box.Items.Select(n => n switch { PdfInteger i => i.Value, PdfReal r => r.Value, _ => double.NaN })]
            : null;
    }

    private static void AssertBox(double[] expected, double[]? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(4, actual.Length);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) < 0.01, $"BBox [{string.Join(' ', actual)}], expected [{string.Join(' ', expected)}]");
        }
    }
}

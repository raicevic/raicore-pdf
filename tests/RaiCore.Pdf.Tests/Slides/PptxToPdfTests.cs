using System.Text;
using System.Text.Json;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Slides;
using static RaiCore.Pdf.Tests.Slides.Deck;

namespace RaiCore.Pdf.Tests.Slides;

/// <summary>
/// Converting PPTX presentations to PDF: pages of the slide size, text where the shapes are (checked against
/// python-pptx, an independent reader), pictures, links, tagging, malformed and hostile packages, limits.
/// </summary>
public class PptxToPdfTests
{
    private static byte[] Fixture(string name) => Fixtures.Read(Path.Combine("Slides", name));

    private static string[] Pages(byte[] pdf) => [.. new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)];

    private static IReadOnlyList<TextMatch> Find(byte[] pdf, int page, string text) => new TextExtractor(PdfReader.Open(pdf)).FindOnPage(page, text);

    private static void AssertValid(byte[] pdf)
    {
        if (ExternalTools.Find("qpdf") is not null)
        {
            ExternalTools.AssertQpdfClean(pdf);
        }
    }

    /// <summary>The decoded content stream of a page.</summary>
    internal static string Content(byte[] pdf, int page = 0)
    {
        var reader = PdfReader.Open(pdf);
        var contents = reader.Resolve(reader.Pages[page].Dictionary["Contents"]);
        var streams = contents is PdfArray array ? array.Items.Select(reader.Resolve) : [contents];
        return string.Concat(streams.OfType<PdfStreamObject>().Select(s => Encoding.Latin1.GetString(Filters.StreamDecoder.Decode(s, reader.Resolve))));
    }

    [Fact]
    public void Each_visible_slide_is_a_page_of_the_slide_size()
    {
        var pdf = PptxToPdf.ConvertToBytes(Fixture("basic.pptx"));
        var reader = PdfReader.Open(pdf);
        Assert.Equal(7, reader.Pages.Count);
        Assert.All(reader.Pages, p => Assert.Equal(new PdfRectangle(0, 0, 720, 540), p.MediaBox));
        var pages = Pages(pdf);
        Assert.Contains("Quarterly Review", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain(pages, p => p.Contains("Hidden slide", StringComparison.Ordinal));
        AssertValid(pdf);

        var wide = PdfReader.Open(PptxToPdf.ConvertToBytes(Fixture("widescreen.pptx")));
        Assert.Equal(960, wide.Pages[0].MediaBox.Width, 0);
        Assert.Equal(540, wide.Pages[0].MediaBox.Height, 3);
    }

    [Fact]
    public void Hidden_slides_and_chosen_slides()
    {
        var all = PptxToPdf.Convert(Fixture("basic.pptx"), new PptxConverterOptions { IncludeHiddenSlides = true });
        Assert.Equal(8, all.Pages.Count);
        var chosen = PptxToPdf.ConvertToBytes(Fixture("basic.pptx"), new PptxConverterOptions { SlideNumbers = [7, 2] });
        var pages = Pages(chosen);
        Assert.Equal(2, pages.Length);
        Assert.Contains("Agenda", pages[0], StringComparison.Ordinal);
        Assert.Contains("Hidden slide", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Document_properties_come_from_the_presentation_or_the_options()
    {
        var pdf = PptxToPdf.Convert(Fixture("basic.pptx"));
        Assert.Equal("Basic deck", pdf.Info.Title);
        Assert.Equal("RaiCore tests", pdf.Info.Author);
        Assert.Equal("RaiCore.Pdf.Slides", pdf.Info.Creator);
        Assert.Equal("Mine", PptxToPdf.Convert(Fixture("basic.pptx"), new PptxConverterOptions { Title = "Mine" }).Info.Title);
    }

    /// <summary>The fixtures and samples with what python-pptx read in them (expected.json, make-expected.py).</summary>
    public static TheoryData<string> Presentations()
    {
        var data = new TheoryData<string>();
        foreach (var property in Expected().RootElement.EnumerateObject())
        {
            data.Add(property.Name);
        }

        return data;
    }

    private static JsonDocument Expected() => JsonDocument.Parse(File.ReadAllText(Fixtures.Path(Path.Combine("Slides", "expected.json"))));

    [Theory]
    [MemberData(nameof(Presentations))]
    public void Text_of_every_shape_is_on_its_page_inside_its_box(string name)
    {
        using var expected = Expected();
        var deck = expected.RootElement.GetProperty(name);
        var pdf = PptxToPdf.ConvertToBytes(Fixtures.Read(name), new PptxConverterOptions { IncludeHiddenSlides = true });
        var reader = PdfReader.Open(pdf);
        var extractor = new TextExtractor(reader);
        var slides = deck.GetProperty("slides").EnumerateArray().ToList();
        Assert.Equal(slides.Count, reader.Pages.Count);
        Assert.Equal(deck.GetProperty("width").GetDouble(), reader.Pages[0].MediaBox.Width, 0);
        Assert.Equal(deck.GetProperty("height").GetDouble(), reader.Pages[0].MediaBox.Height, 0);
        for (var page = 0; page < slides.Count; page++)
        {
            var pageHeight = reader.Pages[page].MediaBox.Height;
            foreach (var shape in slides[page].GetProperty("shapes").EnumerateArray())
            {
                // The first line with words, and its first word, which no wrapping splits.
                var line = shape.GetProperty("text").GetString()!.Split('\n').Select(l => l.Trim()).First(l => l.Length > 0);
                var word = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                var matches = extractor.FindOnPage(page, word, ignoreCase: true);
                Assert.True(matches.Count > 0, $"{name} slide {page + 1}: '{word}' not found");
                if (shape.GetProperty("rotation").GetDouble() != 0 || shape.GetProperty("grouped").GetBoolean() || shape.GetProperty("left").ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                // In PDF coordinates (y up), with a tolerance for text that overflows its box a little.
                var (left, top, width, height) = (shape.GetProperty("left").GetDouble(), shape.GetProperty("top").GetDouble(),
                    shape.GetProperty("width").GetDouble(), shape.GetProperty("height").GetDouble());
                var box = new PdfRectangle(left, pageHeight - top - height, width, height);
                var tolerance = Math.Max(6, height * 0.5);
                Assert.True(matches.Any(m => m.Bounds.X >= box.X - 6 && m.Bounds.Y >= box.Y - tolerance && m.Bounds.Top <= box.Top + tolerance
                        && (shape.GetProperty("wrap").ValueKind == JsonValueKind.False || m.Bounds.Right <= box.Right + 6)),
                    $"{name} slide {page + 1}: '{word}' at {string.Join(", ", matches.Select(m => m.Bounds))}, not in the shape's box {box}");
            }
        }

        AssertValid(pdf);
    }

    [Fact]
    public void Titles_bullets_and_levels_are_laid_out_by_their_inherited_styles()
    {
        var pdf = PptxToPdf.ConvertToBytes(Fixture("basic.pptx"));
        var title = Find(pdf, 1, "Agenda").Single().Bounds;
        var first = Find(pdf, 1, "First level point").Single().Bounds;
        var second = Find(pdf, 1, "Second level detail").Single().Bounds;
        var third = Find(pdf, 1, "Third level note").Single().Bounds;

        // The title is centered and larger; each level is indented further and smaller.
        Assert.Equal(360, title.X + (title.Width / 2), 0);
        Assert.True(title.Height > first.Height);
        Assert.True(first.X < second.X && second.X < third.X);
        Assert.True(first.Height > second.Height && second.Height > third.Height);
        Assert.True(first.Top > second.Top && second.Top > third.Top);

        // The bullets are drawn before the first lines.
        var text = Pages(pdf)[1];
        Assert.Contains("\u2022", text, StringComparison.Ordinal);
        Assert.Contains("\u2013", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Links_of_runs_and_shapes_become_link_annotations()
    {
        var pdf = PptxToPdf.Convert(Fixture("basic.pptx"));
        var link = Assert.Single(pdf.Pages[1].Links);
        Assert.Equal("https://example.com/slides", link.Uri!.AbsoluteUri);
        var bytes = pdf.ToArray();
        var bounds = Find(bytes, 1, "a link").Single().Bounds;
        Assert.InRange(bounds.X + (bounds.Width / 2), link.X, link.X + link.Width);

        var deck = new Deck { SlideRelationships = $"<Relationship Id=\"rId5\" Type=\"{R}/hyperlink\" Target=\"https://example.com/shape\" TargetMode=\"External\"/>" }
            .With("<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Linked\"><a:hlinkClick r:id=\"rId5\"/></p:cNvPr><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr>" + Xfrm(100, 100, 200, 50, "rot=\"5400000\"") + "</p:spPr></p:sp>");
        var shapeLink = Assert.Single(PptxToPdf.Convert(deck.Build()).Pages[0].Links);
        Assert.Equal("https://example.com/shape", shapeLink.Uri!.AbsoluteUri);

        // Turned a quarter: the link covers the turned box, 50 wide and 200 high, around the same center.
        Assert.Equal((175.0, 315.0, 50.0, 200.0), (Math.Round(shapeLink.X, 3), Math.Round(shapeLink.Y, 3), Math.Round(shapeLink.Width, 3), Math.Round(shapeLink.Height, 3)));
    }

    [Fact]
    public void Pictures_are_drawn_cropped_in_their_boxes()
    {
        var pdf = PptxToPdf.ConvertToBytes(Fixture("basic.pptx"));
        var images = new ImageExtractor(PdfReader.Open(pdf)).ExtractPage(3).Images;
        Assert.Equal(2, images.Count);
        Assert.All(images, i => Assert.Equal((200, 100), (i.PixelWidth, i.PixelHeight)));

        // The whole picture fills its 4 x 2 inch box; the cropped one is drawn twice as wide as its 2 inch box, its
        // left half outside (clipped).
        var whole = images.Single(i => Math.Abs(i.Bounds.X - 36) < 0.5).Bounds;
        Assert.Equal((288.0, 144.0), (Math.Round(whole.Width, 3), Math.Round(whole.Height, 3)));
        var cropped = images.Single(i => Math.Abs(i.Bounds.X - 36) >= 0.5).Bounds;
        Assert.Equal((216.0, 288.0, 144.0), (Math.Round(cropped.X, 3), Math.Round(cropped.Width, 3), Math.Round(cropped.Height, 3)));
    }

    [Fact]
    public void Tables_lay_out_rows_merged_cells_and_their_text()
    {
        var pdf = PptxToPdf.ConvertToBytes(Fixture("basic.pptx"));
        var page = 4;
        var region = Find(pdf, page, "Region").Single().Bounds;
        var q3 = Find(pdf, page, "Q3").Single().Bounds;
        var merged = Find(pdf, page, "North and South").Single().Bounds;
        var total = Find(pdf, page, "Total").Single().Bounds;
        Assert.Equal(region.Top, q3.Top, 1);
        Assert.True(q3.X > merged.Right);
        Assert.True(region.Top > merged.Top && merged.Top > total.Top);

        // "98" is in the second row of the merged cell, below "120" and in its column.
        var (a, b) = (Find(pdf, page, "120").Single().Bounds, Find(pdf, page, "98").Single().Bounds);
        Assert.Equal(a.X, b.X, 1);
        Assert.True(a.Top > b.Top);
    }

    [Fact]
    public void Rows_grow_to_fit_their_text()
    {
        static string Table(string text) =>
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>"
            + $"<p:xfrm><a:off x=\"{Emu(100)}\" y=\"{Emu(100)}\"/><a:ext cx=\"{Emu(100)}\" cy=\"{Emu(40)}\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\">"
            + $"<a:tbl><a:tblGrid><a:gridCol w=\"{Emu(100)}\"/></a:tblGrid><a:tr h=\"{Emu(20)}\"><a:tc><a:txBody><a:bodyPr/>{Para(text)}</a:txBody></a:tc></a:tr>"
            + $"<a:tr h=\"{Emu(20)}\"><a:tc><a:txBody><a:bodyPr/>{Para("Below")}</a:txBody></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        var shortPdf = PptxToPdf.ConvertToBytes(Deck.Of(Table("Short")).Build());
        var longPdf = PptxToPdf.ConvertToBytes(Deck.Of(Table("Many words that wrap onto several lines in a narrow column")).Build());
        var shortBelow = Find(shortPdf, 0, "Below").Single().Bounds;
        var longBelow = Find(longPdf, 0, "Below").Single().Bounds;
        Assert.True(shortBelow.Top - longBelow.Top > 40, $"{shortBelow} vs {longBelow}");

        // The next row starts below the last line of the grown row.
        var last = Find(longPdf, 0, "column").Single().Bounds;
        Assert.True(longBelow.Top <= last.Y + 1, $"{longBelow} overlaps {last}");
    }

    [Fact]
    public void Groups_map_their_children_onto_their_box()
    {
        // The group's children are in a space twice its size: a child at (200, 100) of 200 x 100 lands at (200, 150)
        // on the slide, half as big.
        var group = "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"5\" name=\"G\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr>"
            + $"<a:xfrm><a:off x=\"{Emu(100)}\" y=\"{Emu(100)}\"/><a:ext cx=\"{Emu(200)}\" cy=\"{Emu(100)}\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"{Emu(400)}\" cy=\"{Emu(200)}\"/></a:xfrm></p:grpSpPr>"
            + Shape(200, 100, 200, 100, spPr: "<a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill>", bodyPr: "<a:bodyPr lIns=\"0\" tIns=\"0\"/>", text: Para("Scaled")) + "</p:grpSp>";
        var pdf = PptxToPdf.ConvertToBytes(Deck.Of(group).Build());
        var text = Find(pdf, 0, "Scaled").Single().Bounds;
        Assert.Equal(200, text.X, 0);
        Assert.InRange(540 - 150 - text.Top, 0, 6);
        Assert.Contains("200 390 m", Content(pdf), StringComparison.Ordinal);
        Assert.Contains("300 340 l", Content(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void Master_shapes_are_artifacts_in_tagged_slides()
    {
        var pdf = PptxToPdf.Convert(new Deck { MasterShapes = Shape(0, 0, 200, 50, Para("Logo")) }.With(Shape(0, 100, 200, 50, Para("Content"))).Build(), new PptxConverterOptions { Tagged = true });
        var section = Assert.Single(pdf.StructureRoot.Children);
        Assert.Equal("P", Assert.Single(section.Children).Type);
        Assert.Contains("Logo", Pages(pdf.ToArray())[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Office_fonts_get_their_metric_compatible_substitutes()
    {
        // Liberation Mono stands in for Carlito here, so that the substitute shows.
        var mono = RaiCore.Pdf.Fonts.BundledFonts.CreateCollection().Find("Liberation Mono")!;
        var fonts = RaiCore.Pdf.Fonts.BundledFonts.CreateCollection().AddFont(() => mono, "Carlito");
        var pdf = Encoding.Latin1.GetString(PptxToPdf.ConvertToBytes(Deck.Of(Shape(10, 10, 300, 100, Para("Calibri text", rPr: "><a:latin typeface=\"Calibri\"/></a:rPr><a:rPr"))).Build(),
            new PptxConverterOptions { Fonts = fonts }));
        Assert.Contains("LiberationMono", pdf, StringComparison.Ordinal);
        Assert.DoesNotContain("LiberationSerif", pdf, StringComparison.Ordinal);
        Assert.DoesNotContain("LiberationSans", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_is_anchored_aligned_and_turned_with_its_shape()
    {
        var pdf = PptxToPdf.ConvertToBytes(Deck.Of(
            Shape(100, 100, 200, 100, Para("Top"))
            + Shape(100, 300, 200, 100, Para("Bottom", pPr: "<a:pPr algn=\"r\"/>"), bodyPr: "<a:bodyPr anchor=\"b\"/>")
            + Shape(400, 100, 200, 100, Para("Middle", pPr: "<a:pPr algn=\"ctr\"/>"), bodyPr: "<a:bodyPr anchor=\"ctr\"/>")
            + Shape(400, 300, 200, 100, Para("Upward"), bodyPr: "<a:bodyPr vert=\"vert270\"/>")).Build());
        var top = Find(pdf, 0, "Top").Single().Bounds;
        Assert.Equal(107.2, top.X, 0);
        Assert.InRange(540 - 100 - top.Top, 0, 8);
        var bottom = Find(pdf, 0, "Bottom").Single().Bounds;
        Assert.Equal(300 - 7.2, bottom.Right, 0);
        Assert.InRange(bottom.Y - (540 - 400), 3, 12);
        var middle = Find(pdf, 0, "Middle").Single().Bounds;
        Assert.Equal(500, middle.X + (middle.Width / 2), 0);
        Assert.Equal(540 - 150, middle.Y + (middle.Height / 2), 0);

        // Turned counter-clockwise: the word is taller than wide, reading up from the box's bottom.
        var upward = Find(pdf, 0, "Upward").Single().Bounds;
        Assert.True(upward.Height > upward.Width * 2, upward.ToString());
        Assert.InRange(upward.Y, 540 - 400, 540 - 400 + 12);
    }

    [Fact]
    public void Text_that_overflows_a_frame_set_to_shrink_is_made_smaller()
    {
        var text = string.Concat(Enumerable.Range(1, 8).Select(i => Para($"Line {i} of a long list")));
        var fixedPdf = PptxToPdf.ConvertToBytes(Deck.Of(Shape(100, 100, 300, 100, text, bodyPr: "<a:bodyPr><a:noAutofit/></a:bodyPr>")).Build());
        var shrunkPdf = PptxToPdf.ConvertToBytes(Deck.Of(Shape(100, 100, 300, 100, text, bodyPr: "<a:bodyPr><a:normAutofit/></a:bodyPr>")).Build());
        var scaledPdf = PptxToPdf.ConvertToBytes(Deck.Of(Shape(100, 100, 300, 300, Para("Scaled"), bodyPr: "<a:bodyPr><a:normAutofit fontScale=\"50000\"/></a:bodyPr>")).Build());
        var unscaledPdf = PptxToPdf.ConvertToBytes(Deck.Of(Shape(100, 100, 300, 300, Para("Scaled"))).Build());
        var fixedLast = Find(fixedPdf, 0, "Line 8").Single().Bounds;
        var shrunkLast = Find(shrunkPdf, 0, "Line 8").Single().Bounds;
        Assert.True(fixedLast.Y < 540 - 200 - 20, "the unshrunk text overflows");
        Assert.True(shrunkLast.Y >= 540 - 200 - 4, $"the shrunk text fits: {shrunkLast}");
        Assert.True(shrunkLast.Height < fixedLast.Height);
        Assert.Equal(0.5, Find(scaledPdf, 0, "Scaled").Single().Bounds.Height / Find(unscaledPdf, 0, "Scaled").Single().Bounds.Height, 1);
    }

    [Fact]
    public void Lines_do_not_wrap_when_the_frame_does_not()
    {
        var words = "Words that would wrap in a narrow box";
        var wrapped = PptxToPdf.ConvertToBytes(Deck.Of(Shape(100, 100, 80, 200, Para(words))).Build());
        var single = PptxToPdf.ConvertToBytes(Deck.Of(Shape(100, 100, 80, 200, Para(words), bodyPr: "<a:bodyPr wrap=\"none\"/>")).Build());
        var centered = PptxToPdf.ConvertToBytes(Deck.Of(Shape(100, 100, 80, 200, Para(words, pPr: "<a:pPr algn=\"ctr\"/>"), bodyPr: "<a:bodyPr wrap=\"none\"/>")).Build());
        Assert.True(Pages(wrapped)[0].Split('\n').Length > 2);
        Assert.Single(Pages(single)[0].Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var line = Find(centered, 0, words).Single().Bounds;
        Assert.Equal(140, line.X + (line.Width / 2), 0);
    }

    [Fact]
    public void Text_of_mirrored_shapes_is_not_mirrored()
    {
        var pdf = PptxToPdf.ConvertToBytes(Deck.Of(
            Shape(100, 100, 200, 100, Para("Plain"), xfrmAttributes: "flipH=\"1\"")
            + Shape(100, 300, 200, 100, Para("Upside"), xfrmAttributes: "flipV=\"1\"")).Build());
        Assert.Contains("Plain", Pages(pdf)[0], StringComparison.Ordinal);
        var content = Content(pdf);

        // Mirrored text would be drawn with a negative horizontal scale (cm a < 0 with d > 0).
        Assert.Contains("Upside", Pages(pdf)[0], StringComparison.Ordinal);
        Assert.DoesNotContain("-1 0 0 1 ", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Shapes_are_filled_and_stroked_with_their_colors_dashes_and_arrowheads()
    {
        var pdf = PptxToPdf.ConvertToBytes(Deck.Of(
            Shape(10, 10, 100, 50, spPr: "<a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill><a:ln w=\"25400\"><a:solidFill><a:srgbClr val=\"0000FF\"/></a:solidFill><a:prstDash val=\"sysDot\"/></a:ln>")
            + Shape(10, 100, 100, 0, spPr: "<a:prstGeom prst=\"line\"/><a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"00FF00\"/></a:solidFill><a:tailEnd type=\"triangle\"/></a:ln>")).Build());
        var content = Content(pdf);
        Assert.Contains("1 0 0 rg", content, StringComparison.Ordinal);
        Assert.Contains("0 0 1 RG", content, StringComparison.Ordinal);
        Assert.Contains("[2 2] 0 d", content, StringComparison.Ordinal);
        Assert.Contains("0 1 0 RG", content, StringComparison.Ordinal);

        // The arrowhead: a triangle filled in the line's color, its tip at the line's end (110, 440), three line widths
        // long and wide.
        Assert.Contains("0 1 0 rg", content, StringComparison.Ordinal);
        Assert.Contains("110 440 m", content, StringComparison.Ordinal);
        Assert.Contains("107 441.5 l", content, StringComparison.Ordinal);
        Assert.Contains("107 438.5 l", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Gradients_are_drawn_in_bands_clipped_to_the_shape()
    {
        var gradient = "<a:gradFill><a:gsLst><a:gs pos=\"0\"><a:srgbClr val=\"FFFFFF\"/></a:gs><a:gs pos=\"100000\"><a:srgbClr val=\"000000\"/></a:gs></a:gsLst><a:lin ang=\"0\"/></a:gradFill>";
        var translucent = "<a:gradFill><a:gsLst><a:gs pos=\"0\"><a:srgbClr val=\"FFFFFF\"><a:alpha val=\"50000\"/></a:srgbClr></a:gs><a:gs pos=\"100000\"><a:srgbClr val=\"000000\"><a:alpha val=\"50000\"/></a:srgbClr></a:gs></a:gsLst><a:path path=\"circle\"/></a:gradFill>";
        var pdf = PptxToPdf.Convert(Deck.Of(Shape(10, 10, 100, 50, spPr: gradient) + Shape(200, 10, 100, 50, spPr: translucent)).Build());
        var bytes = pdf.ToArray();
        var content = Content(bytes);
        Assert.Contains("W n", content, StringComparison.Ordinal);
        Assert.True(content.Split(" rg").Length > 60, "the bands of the opaque gradient");

        // The first band of the left-to-right gradient is nearly white, the last nearly black.
        Assert.Contains("0.99", content, StringComparison.Ordinal);
        Assert.Contains("0.00781 0.00781 0.00781 rg", content, StringComparison.Ordinal);

        // The translucent one is drawn opaque in a transparency group of its opacity.
        Assert.Contains("/ca 0.5", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        AssertValid(bytes);
    }

    [Fact]
    public void Backgrounds_fill_the_page()
    {
        var pdf = PptxToPdf.ConvertToBytes(Fixture("widescreen.pptx"));
        var content = Content(pdf);
        Assert.StartsWith("0.06275 0.12549 0.25098 rg", content.TrimStart().Split('\n').First(l => l.EndsWith(" rg", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("Slide 1", Pages(pdf)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_pictures_are_reported_and_left_out()
    {
        var reports = new List<SlideDiagnostic>();
        var picture = "<p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"Pic\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rId7\"/></p:blipFill><p:spPr>" + Xfrm(10, 20, 30, 40) + "</p:spPr></p:pic>";
        var deck = new Deck { SlideRelationships = $"<Relationship Id=\"rId7\" Type=\"{R}/image\" Target=\"../media/image1.emf\"/>" }.With(picture, picture);
        deck.Parts.Add(("ppt/media/image1.emf", [1, 0, 0, 0, 0x6C, 0, 0, 0, 5, 6, 7, 8]));
        var pdf = PptxToPdf.Convert(deck.Build(), new PptxConverterOptions { Diagnostics = reports.Add });
        Assert.Equal(2, pdf.Pages.Count);
        Assert.Equal([1, 2], reports.Select(r => r.SlideNumber));
        Assert.All(reports, r => Assert.Contains("EMF", r.Message, StringComparison.Ordinal));
        Assert.Empty(new ImageExtractor(PdfReader.Open(pdf.ToArray())).ExtractPage(0).Images);
    }

    [Fact]
    public void Tagged_slides_have_headings_paragraphs_figures_and_tables()
    {
        var pdf = PptxToPdf.Convert(Fixture("basic.pptx"), new PptxConverterOptions { Tagged = true });
        Assert.True(pdf.Tagged);
        var sections = pdf.StructureRoot.Children;
        Assert.Equal(7, sections.Count);
        Assert.All(sections, s => Assert.Equal("Sect", s.Type));
        Assert.Equal("Slide 2", sections[1].Title);
        Assert.Equal(["H1", "P", "P", "P", "P", "P"], sections[1].Children.Select(c => c.Type));
        Assert.Contains(sections[3].Children, c => c.Type == "Figure" && c.AlternateText == "Four color bands");
        var table = Assert.Single(sections[4].Children, c => c.Type == "Table");
        Assert.Equal(4, table.Children.Count);
        Assert.Equal(2, table.Children[1].Children[0].RowSpan);
        var bytes = pdf.ToArray();
        Assert.Contains("/StructTreeRoot", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        AssertValid(bytes);
    }

    [Fact]
    public async Task Streams_paths_and_async_conversion()
    {
        var bytes = Fixture("widescreen.pptx");
        using var stream = new MemoryStream(bytes);
        Assert.Single(PptxToPdf.Convert(stream).Pages);
        stream.Position = 0;
        Assert.NotEmpty(PptxToPdf.ConvertToBytes(stream));
        Assert.Single(PptxToPdf.Convert(Fixtures.Path(Path.Combine("Slides", "widescreen.pptx"))).Pages);
        stream.Position = 0;
        Assert.Single((await PptxToPdf.ConvertAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Pages);
        Assert.Single((await PptxToPdf.ConvertAsync(bytes, cancellationToken: TestContext.Current.CancellationToken)).Pages);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PptxToPdf.ConvertAsync(bytes, cancellationToken: new CancellationToken(true)));
        Assert.Throws<ArgumentNullException>(() => PptxToPdf.Convert((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => PptxToPdf.Convert((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => PptxToPdf.Convert((string)null!));
        Assert.Throws<ArgumentException>(() => PptxToPdf.Convert(bytes, new PptxConverterOptions { Limits = null! }));
        Assert.Throws<ArgumentNullException>(() => PptxToPdf.Convert(bytes, new PptxConverterOptions { FallbackFontFamilies = null! }));
    }

    [Fact]
    public void Malformed_packages_throw_the_documented_exceptions()
    {
        Assert.Throws<PresentationFormatException>(() => PptxToPdf.Convert("not a zip"u8.ToArray()));
        Assert.Throws<PresentationFormatException>(() => PptxToPdf.Convert("PK\u0003\u0004 truncated"u8.ToArray()));
        Assert.Throws<NotSupportedException>(() => PptxToPdf.Convert([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0]));
        Assert.Throws<PresentationFormatException>(() => PptxToPdf.Convert(Zip([("x.xml", "<x/>"u8.ToArray())])));

        var deck = Deck.Of(Shape(0, 0, 10, 10, Para("A")));
        var parts = Replace(deck.Build(), "ppt/slides/slide1.xml", "<p:sld><unclosed>");
        Assert.Throws<PresentationFormatException>(() => PptxToPdf.Convert(parts));
        var dtd = Replace(deck.Build(), "ppt/presentation.xml", "<!DOCTYPE x [<!ENTITY e \"boom\">]><p:presentation xmlns:p=\"" + P + "\">&e;</p:presentation>");
        Assert.Throws<PresentationFormatException>(() => PptxToPdf.Convert(dtd));
        var notPresentation = Replace(deck.Build(), "ppt/presentation.xml", "<other/>");
        Assert.Throws<PresentationFormatException>(() => PptxToPdf.Convert(notPresentation));
    }

    [Fact]
    public void Packages_without_relationships_find_the_presentation_part_and_fall_back_to_the_office_theme()
    {
        var parts = new List<(string, byte[])>
        {
            ("ppt/presentation.xml", Encoding.UTF8.GetBytes($"<p:presentation xmlns:p=\"{P}\"/>")),
        };
        var blank = PdfReader.Open(PptxToPdf.ConvertToBytes(Zip(parts)));
        Assert.Equal(new PdfRectangle(0, 0, 720, 540), Assert.Single(blank.Pages).MediaBox);

        // A slide without a layout gets the default master: Office colors and fonts.
        var reader = PptxReaderTests.Read(Replace(Deck.Of(Shape(0, 0, 10, 10, spPr: "<a:solidFill><a:schemeClr val=\"accent1\"/></a:solidFill>")).Build(),
            "ppt/slides/_rels/slide1.xml.rels", $"<Relationships xmlns=\"{PackageRelationships}\"/>"));
        var fill = Assert.IsType<RaiCore.Pdf.Slides.Model.SolidFill>(((RaiCore.Pdf.Slides.Model.AutoShape)reader.Slides[0].Shapes[0]).Fill);
        Assert.Equal(PdfColor.FromRgb(0x44, 0x72, 0xC4), fill.Color);
    }

    [Fact]
    public void Limits_bound_slides_bytes_elements_nesting_cells_and_time()
    {
        var two = Deck.Of(Shape(0, 0, 10, 10, Para("A")), Shape(0, 0, 10, 10, Para("B"))).Build();
        Assert.Equal("MaxPages", Assert.Throws<ResourceLimitExceededException>(() =>
            PptxToPdf.Convert(two, new PptxConverterOptions { Limits = ResourceLimits.Default with { MaxPages = 1 } })).LimitName);
        Assert.Single(PptxToPdf.Convert(two, new PptxConverterOptions { SlideNumbers = [2], Limits = ResourceLimits.Default with { MaxPages = 1 } }).Pages);
        Assert.Equal("MaxDecompressedBytes", Assert.Throws<ResourceLimitExceededException>(() =>
            PptxToPdf.Convert(two, new PptxConverterOptions { Limits = ResourceLimits.Default with { MaxDecompressedBytes = 1000 } })).LimitName);
        // The elements of every part read count (all but the content types).
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(two));
        var elements = archive.Entries.Where(e => e.FullName != "[Content_Types].xml").Sum(e =>
        {
            using var stream = e.Open();
            return System.Xml.Linq.XDocument.Load(stream).Descendants().Count();
        });
        Assert.Equal("MaxElements", Assert.Throws<ResourceLimitExceededException>(() =>
            PptxToPdf.Convert(two, new PptxConverterOptions { Limits = ResourceLimits.Default with { MaxElements = elements - 1 } })).LimitName);
        Assert.Equal(2, PptxToPdf.Convert(two, new PptxConverterOptions { Limits = ResourceLimits.Default with { MaxElements = elements } }).Pages.Count);

        var nested = string.Concat(Enumerable.Repeat("<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"5\" name=\"G\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>", 80))
            + Shape(0, 0, 200, 50, Para("Deep")) + string.Concat(Enumerable.Repeat("</p:grpSp>", 80));
        var deep = Deck.Of(nested).Build();
        Assert.Equal("MaxNestingDepth", Assert.Throws<ResourceLimitExceededException>(() =>
            PptxToPdf.Convert(deep, new PptxConverterOptions { Limits = ResourceLimits.Default with { MaxNestingDepth = 64 } })).LimitName);
        Assert.Contains("Deep", Pages(PptxToPdf.ConvertToBytes(deep))[0], StringComparison.Ordinal);

        var table = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>"
            + $"<p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(100)}\" cy=\"{Emu(100)}\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\">"
            + "<a:tbl><a:tblGrid><a:gridCol w=\"100\"/><a:gridCol w=\"100\"/></a:tblGrid>" + string.Concat(Enumerable.Repeat("<a:tr h=\"10\"><a:tc/><a:tc/></a:tr>", 6))
            + "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        Assert.Equal("MaxTableCells", Assert.Throws<ResourceLimitExceededException>(() =>
            PptxToPdf.Convert(Deck.Of(table).Build(), new PptxConverterOptions { Limits = ResourceLimits.Default with { MaxTableCells = 10 } })).LimitName);
        Assert.Equal("Timeout", Assert.Throws<ResourceLimitExceededException>(() =>
            PptxToPdf.Convert(two, new PptxConverterOptions { Limits = ResourceLimits.Default with { Timeout = TimeSpan.FromTicks(1) } })).LimitName);
    }

    [Fact]
    public void Diagnostics_are_reported_once_per_slide_and_issue()
    {
        var reports = new List<SlideDiagnostic>();
        var chart = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"C\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>"
            + $"<p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(10)}\" cy=\"{Emu(10)}\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"/></a:graphic></p:graphicFrame>";
        PptxToPdf.Convert(Deck.Of(chart + chart, chart).Build(), new PptxConverterOptions { Diagnostics = reports.Add });
        const string Missing = "A chart without its chart part is not drawn.";
        Assert.Equal([new SlideDiagnostic(1, "chart", Missing), new SlideDiagnostic(2, "chart", Missing)], reports);
    }

    [Fact]
    public void Fonts_of_the_options_and_fallbacks_are_used()
    {
        var fonts = RaiCore.Pdf.Fonts.BundledFonts.CreateCollection();
        var pdf = Encoding.Latin1.GetString(PptxToPdf.ConvertToBytes(Deck.Of(Shape(10, 10, 300, 100, "<a:p><a:r><a:rPr><a:latin typeface=\"Liberation Serif\"/></a:rPr><a:t>Serif text</a:t></a:r>"
            + "<a:r><a:rPr><a:latin typeface=\"Courier New\"/></a:rPr><a:t>Mono</a:t></a:r><a:r><a:rPr><a:latin typeface=\"Garamond\"/></a:rPr><a:t>Generic serif</a:t></a:r></a:p>")).Build(),
            new PptxConverterOptions { Fonts = fonts, FallbackFontFamilies = ["Liberation Mono"] }));
        Assert.Contains("LiberationSerif", pdf, StringComparison.Ordinal);
        Assert.Contains("LiberationMono", pdf, StringComparison.Ordinal);
    }

    /// <summary>A package with one part's content replaced.</summary>
    private static byte[] Replace(byte[] zip, string name, string content)
    {
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        var parts = archive.Entries.Select(e =>
        {
            using var stream = e.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return (e.FullName, e.FullName == name ? Encoding.UTF8.GetBytes(content) : buffer.ToArray());
        }).ToList();
        return Zip(parts);
    }
}

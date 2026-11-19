using RaiCore.Pdf.Slides;
using RaiCore.Pdf.Slides.Model;
using RaiCore.Pdf.Slides.Pptx;
using static RaiCore.Pdf.Tests.Slides.Deck;

namespace RaiCore.Pdf.Tests.Slides;

/// <summary>
/// Reading PPTX packages into the slide model: inheritance of placeholders from layouts and masters, text styles,
/// theme colors and fonts, bullets, body properties, fills, lines, groups, tables and backgrounds.
/// </summary>
public class PptxReaderTests
{
    /// <summary>The Office theme's colors (as a theme part) with distinct fonts.</summary>
    private const string Theme = """
        <a:clrScheme name="Test"><a:dk1><a:srgbClr val="000000"/></a:dk1><a:lt1><a:srgbClr val="FFFFFF"/></a:lt1>
        <a:dk2><a:srgbClr val="1F497D"/></a:dk2><a:lt2><a:srgbClr val="EEECE1"/></a:lt2><a:accent1><a:srgbClr val="4F81BD"/></a:accent1>
        <a:accent2><a:srgbClr val="C0504D"/></a:accent2><a:accent3><a:srgbClr val="9BBB59"/></a:accent3><a:accent4><a:srgbClr val="8064A2"/></a:accent4>
        <a:accent5><a:srgbClr val="4BACC6"/></a:accent5><a:accent6><a:srgbClr val="F79646"/></a:accent6><a:hlink><a:srgbClr val="0000FF"/></a:hlink>
        <a:folHlink><a:srgbClr val="800080"/></a:folHlink></a:clrScheme>
        <a:fontScheme name="Test"><a:majorFont><a:latin typeface="Major Face"/></a:majorFont><a:minorFont><a:latin typeface="Minor Face"/></a:minorFont></a:fontScheme>
        <a:fmtScheme name="Test"><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
        <a:solidFill><a:schemeClr val="phClr"><a:shade val="50000"/></a:schemeClr></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst>
        <a:lnStyleLst><a:ln w="9525"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="25400"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln>
        <a:ln w="38100"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst>
        <a:effectStyleLst/><a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"><a:tint val="50000"/></a:schemeClr></a:solidFill>
        <a:solidFill><a:srgbClr val="123456"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme>
        """;

    private const string Style = "<p:style><a:lnRef idx=\"2\"><a:schemeClr val=\"accent1\"/></a:lnRef><a:fillRef idx=\"1\"><a:schemeClr val=\"accent1\"/></a:fillRef>"
        + "<a:effectRef idx=\"0\"><a:schemeClr val=\"accent1\"/></a:effectRef><a:fontRef idx=\"minor\"><a:schemeClr val=\"lt1\"/></a:fontRef></p:style>";

    internal static Presentation Read(byte[] pptx, PptxConverterOptions? options = null) => PptxConverter.Read(pptx, options ?? new PptxConverterOptions());

    private static Slide One(Deck deck) => Assert.Single(Read(deck.Build()).Slides);

    private static T Only<T>(Slide slide)
        where T : Shape => Assert.IsType<T>(Assert.Single(slide.Shapes));

    private static void Close(double expected, double actual, double tolerance = 0.01) => Assert.InRange(actual, expected - tolerance, expected + tolerance);

    private static void SameColor(PdfColor expected, PdfColor actual)
    {
        Close(expected.Red, actual.Red, 0.01);
        Close(expected.Green, actual.Green, 0.01);
        Close(expected.Blue, actual.Blue, 0.01);
        Close(expected.Alpha, actual.Alpha, 0.01);
    }

    [Fact]
    public void Reads_the_slide_size_order_and_document_properties()
    {
        var deck = new Deck { Size = (960, 540) }.With(Shape(10, 10, 100, 50, Para("One")), Shape(10, 10, 100, 50, Para("Two")));
        deck.Parts.Add(("docProps/core.xml", System.Text.Encoding.UTF8.GetBytes(
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Deck title</dc:title><dc:creator>Someone</dc:creator></cp:coreProperties>")));
        var presentation = Read(deck.Build());
        Assert.Equal(960, presentation.Width, 3);
        Assert.Equal(540, presentation.Height, 3);
        Assert.Equal("Deck title", presentation.Title);
        Assert.Equal("Someone", presentation.Author);
        Assert.Equal([1, 2], presentation.Slides.Select(s => s.Number));
        Assert.Equal("Two", Assert.IsType<AutoShape>(presentation.Slides[1].Shapes[0]).Text!.Paragraphs[0].Runs[0].Text);
    }

    [Fact]
    public void Hidden_slides_are_left_out_unless_included_or_named()
    {
        var deck = Deck.Of(Shape(0, 0, 10, 10, Para("A")), Shape(0, 0, 10, 10, Para("B")), Shape(0, 0, 10, 10, Para("C")));
        deck.SlideAttributes[1] = "show=\"0\"";
        var bytes = deck.Build();
        Assert.Equal([1, 3], Read(bytes).Slides.Select(s => s.Number));
        var all = Read(bytes, new PptxConverterOptions { IncludeHiddenSlides = true }).Slides;
        Assert.Equal([1, 2, 3], all.Select(s => s.Number));
        Assert.True(all[1].Hidden);
        Assert.Equal([2, 3], Read(bytes, new PptxConverterOptions { SlideNumbers = [3, 2, 9] }).Slides.Select(s => s.Number));
    }

    [Fact]
    public void Missing_slide_size_is_the_default_and_unusable_parts_are_reported()
    {
        var reports = new List<SlideDiagnostic>();
        var parts = new List<(string, byte[])>
        {
            ("[Content_Types].xml", "<Types/>"u8.ToArray()),
            ("_rels/.rels", System.Text.Encoding.UTF8.GetBytes($"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{R}/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>")),
            ("ppt/presentation.xml", System.Text.Encoding.UTF8.GetBytes($"<p:presentation xmlns:p=\"{P}\" xmlns:r=\"{R}\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId2\"/><p:sldId id=\"257\" r:id=\"rId3\"/></p:sldIdLst></p:presentation>")),
            ("ppt/_rels/presentation.xml.rels", System.Text.Encoding.UTF8.GetBytes($"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId2\" Type=\"{R}/slide\" Target=\"slides/missing.xml\"/><Relationship Id=\"rId3\" Type=\"{R}/slide\" Target=\"slides/slide1.xml\"/></Relationships>")),
            ("ppt/slides/slide1.xml", System.Text.Encoding.UTF8.GetBytes($"<p:sld xmlns:p=\"{P}\" xmlns:a=\"{A}\"><p:cSld><p:spTree>{Shape(0, 0, 50, 20, Para("Kept"))}</p:spTree></p:cSld></p:sld>")),
        };
        var presentation = Read(Zip(parts), new PptxConverterOptions { Diagnostics = reports.Add });
        Assert.Equal(720, presentation.Width, 3);
        Assert.Equal(540, presentation.Height, 3);
        Assert.Equal(2, Assert.Single(presentation.Slides).Number);
        Assert.Contains(reports, r => r.SlideNumber == 1 && r.Subject == "slide");
    }

    [Fact]
    public void Placeholders_inherit_position_and_text_styles_from_layout_and_master()
    {
        var deck = new Deck
        {
            Theme = Theme,
            MasterShapes = Shape(10, 20, 600, 80, ph: "<p:ph type=\"title\"/>", text: "<a:p/>", bodyPr: "<a:bodyPr anchor=\"b\" lIns=\"0\"/>")
                + Shape(30, 120, 640, 380, ph: "<p:ph type=\"body\" idx=\"1\"/>", text: "<a:p/>"),
            LayoutShapes = Shape(50, 60, 500, 70, ph: "<p:ph type=\"title\"/>", text: "<a:p/>")
                + Shape(0, 0, 0, 0, ph: "<p:ph idx=\"1\"/>", text: "<a:p/>"),
            MasterTextStyles = "<p:titleStyle><a:lvl1pPr algn=\"ctr\"><a:defRPr sz=\"4400\"><a:solidFill><a:schemeClr val=\"tx2\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/></a:defRPr></a:lvl1pPr></p:titleStyle>"
                + "<p:bodyStyle><a:lvl1pPr marL=\"342900\" indent=\"-342900\"><a:buChar char=\"&#8226;\"/><a:defRPr sz=\"3200\"/></a:lvl1pPr>"
                + "<a:lvl2pPr marL=\"742950\" indent=\"-285750\"><a:buChar char=\"-\"/><a:defRPr sz=\"2800\"/></a:lvl2pPr></p:bodyStyle>",
        }.With(Shape(0, 0, 0, 0, ph: "<p:ph type=\"title\"/>", text: Para("Title"))
            + Shape(0, 0, 0, 0, ph: "<p:ph idx=\"1\"/>", text: Para("Point") + Para("Detail", pPr: "<a:pPr lvl=\"1\"/>")));
        var slide = One(deck);
        var title = Assert.IsType<AutoShape>(slide.Shapes[0]);
        Assert.Equal(PlaceholderKind.Title, title.Placeholder);
        Assert.Equal(new ShapeBox(50, 60, 500, 70), title.Box);
        Assert.Equal(TextAnchor.Bottom, title.Text!.Anchor);
        Assert.Equal(0, title.Text.LeftInset);
        var titleParagraph = title.Text.Paragraphs[0];
        Assert.Equal(ParagraphAlignment.Center, titleParagraph.Alignment);
        Assert.Equal(44, titleParagraph.Runs[0].Style.Size);
        Assert.Equal("Major Face", titleParagraph.Runs[0].Style.Font);
        SameColor(PdfColor.FromRgb(0x1F, 0x49, 0x7D), titleParagraph.Runs[0].Style.Color);

        // The body has no position on the slide or layout: the master's; text from the body style by level.
        var body = Assert.IsType<AutoShape>(slide.Shapes[1]);
        Assert.Equal(new ShapeBox(30, 120, 640, 380), body.Box);
        Assert.Equal(PlaceholderKind.Other, body.Placeholder);
        var (point, detail) = (body.Text!.Paragraphs[0], body.Text.Paragraphs[1]);
        Assert.Equal(32, point.Runs[0].Style.Size);
        Assert.Equal("Minor Face", point.Runs[0].Style.Font);
        Assert.Equal("\u2022", point.Bullet!.Text);
        Close(27, point.MarginLeft);
        Close(-27, point.Indent);
        Assert.Equal(1, detail.Level);
        Assert.Equal(28, detail.Runs[0].Style.Size);
        Assert.Equal("-", detail.Bullet!.Text);
        Close(58.5, detail.MarginLeft);
    }

    [Fact]
    public void Placeholders_match_by_index_before_type()
    {
        var deck = new Deck
        {
            LayoutShapes = Shape(10, 10, 100, 50, ph: "<p:ph type=\"body\" idx=\"1\"/>", text: "<a:p/>")
                + Shape(300, 10, 100, 50, ph: "<p:ph type=\"body\" idx=\"2\"/>", text: "<a:p/>"),
        }.With(Shape(0, 0, 0, 0, ph: "<p:ph type=\"body\" idx=\"2\"/>", text: Para("Second"))
            + Shape(0, 0, 0, 0, ph: "<p:ph type=\"body\" idx=\"7\"/>", text: Para("By type")));
        var slide = One(deck);
        Assert.Equal(300, slide.Shapes[0].Box.X);
        Assert.Equal(10, slide.Shapes[1].Box.X);
    }

    [Fact]
    public void Layout_and_master_shapes_are_drawn_behind_unless_hidden_and_their_placeholders_are_not()
    {
        var master = Shape(0, 0, 50, 50, Para("Master logo"), name: "Logo") + Shape(0, 0, 50, 50, ph: "<p:ph type=\"title\"/>", text: Para("Prompt"));
        var layout = Shape(0, 0, 50, 50, Para("Layout band"), name: "Band");
        var slide = One(new Deck { MasterShapes = master, LayoutShapes = layout }.With(Shape(0, 0, 50, 50, Para("Own"))));
        Assert.Equal(["Master logo", "Layout band", "Own"], slide.Shapes.Select(s => ((AutoShape)s).Text!.Paragraphs[0].Runs[0].Text));

        var noMaster = One(new Deck { MasterShapes = master, LayoutShapes = layout, LayoutAttributes = "showMasterSp=\"0\"" }.With(Shape(0, 0, 50, 50, Para("Own"))));
        Assert.Equal(["Layout band", "Own"], noMaster.Shapes.Select(s => ((AutoShape)s).Text!.Paragraphs[0].Runs[0].Text));

        var hiding = new Deck { MasterShapes = master, LayoutShapes = layout }.With(Shape(0, 0, 50, 50, Para("Own")));
        hiding.SlideAttributes[0] = "showMasterSp=\"0\"";
        Assert.Equal(["Own"], One(hiding).Shapes.Select(s => ((AutoShape)s).Text!.Paragraphs[0].Runs[0].Text));
    }

    [Fact]
    public void Hidden_shapes_and_shapes_without_a_position_are_left_out()
    {
        var slide = One(Deck.Of(Shape(0, 0, 50, 50, Para("Hidden"), attributes: "hidden=\"1\"") + Shape(0, 0, 0, 0, Para("Nowhere")) + Shape(0, 0, 50, 50, Para("Shown"))));
        Assert.Equal("Shown", Only<AutoShape>(slide).Text!.Paragraphs[0].Runs[0].Text);
    }

    [Fact]
    public void Style_references_fill_line_and_font_from_the_theme()
    {
        var slide = One(new Deck { Theme = Theme }.With(Shape(0, 0, 100, 50, Para("White"), spPr: "<a:prstGeom prst=\"ellipse\"/>", style: Style)));
        var shape = Only<AutoShape>(slide);
        SameColor(PdfColor.FromRgb(0x4F, 0x81, 0xBD), Assert.IsType<SolidFill>(shape.Fill).Color);
        Assert.Equal(2, shape.Line!.Width, 3);
        SameColor(PdfColor.FromRgb(0x4F, 0x81, 0xBD), shape.Line.Color);
        Assert.Equal("ellipse", Assert.IsType<PresetGeometry>(shape.Geometry).Name);
        var run = shape.Text!.Paragraphs[0].Runs[0].Style;
        SameColor(PdfColor.White, run.Color);
        Assert.Equal("Minor Face", run.Font);
    }

    [Fact]
    public void Own_fill_and_line_win_over_the_style()
    {
        var slide = One(new Deck { Theme = Theme }.With(Shape(0, 0, 100, 50, spPr: "<a:noFill/><a:ln w=\"50800\"><a:prstDash val=\"dash\"/><a:headEnd type=\"triangle\" w=\"lg\" len=\"sm\"/><a:tailEnd type=\"stealth\"/></a:ln>", style: Style)));
        var shape = Only<AutoShape>(slide);
        Assert.Null(shape.Fill);
        Assert.Equal(4, shape.Line!.Width, 3);
        SameColor(PdfColor.FromRgb(0x4F, 0x81, 0xBD), shape.Line.Color);
        Assert.Equal([4, 3], shape.Line.Dash);
        Assert.Equal(new LineEnd(LineEndKind.Triangle, 5, 2), shape.Line.Head);
        Assert.Equal(LineEndKind.Stealth, shape.Line.Tail!.Kind);
    }

    [Theory]
    [InlineData("<a:srgbClr val=\"FF8000\"/>", 255, 128, 0, 1)]
    [InlineData("<a:srgbClr val=\"FF0000\"><a:alpha val=\"50000\"/></a:srgbClr>", 255, 0, 0, 0.5)]
    [InlineData("<a:schemeClr val=\"accent2\"/>", 0xC0, 0x50, 0x4D, 1)]
    [InlineData("<a:schemeClr val=\"tx1\"/>", 0, 0, 0, 1)]
    [InlineData("<a:schemeClr val=\"bg2\"/>", 0xEE, 0xEC, 0xE1, 1)]
    [InlineData("<a:prstClr val=\"red\"/>", 255, 0, 0, 1)]
    [InlineData("<a:sysClr val=\"windowText\" lastClr=\"112233\"/>", 0x11, 0x22, 0x33, 1)]
    [InlineData("<a:sysClr val=\"window\"/>", 255, 255, 255, 1)]
    [InlineData("<a:scrgbClr r=\"100000\" g=\"0\" b=\"0\"/>", 255, 0, 0, 1)]
    [InlineData("<a:hslClr hue=\"7200000\" sat=\"100000\" lum=\"50000\"/>", 0, 255, 0, 1)]
    public void Colors_of_every_kind(string color, int r, int g, int b, double alpha)
    {
        var slide = One(new Deck { Theme = Theme }.With(Shape(0, 0, 10, 10, spPr: $"<a:solidFill>{color}</a:solidFill>")));
        SameColor(PdfColor.FromRgba((byte)r, (byte)g, (byte)b, alpha), Assert.IsType<SolidFill>(Only<AutoShape>(slide).Fill).Color);
    }

    [Fact]
    public void The_color_map_and_its_overrides_choose_the_scheme_colors()
    {
        var deck = new Deck { Theme = Theme, ColorMap = "bg1=\"dk1\" tx1=\"lt1\" bg2=\"dk2\" tx2=\"lt2\" accent1=\"accent1\"" }
            .With(Shape(0, 0, 10, 10, spPr: "<a:solidFill><a:schemeClr val=\"bg1\"/></a:solidFill>"));
        SameColor(PdfColor.Black, Assert.IsType<SolidFill>(Only<AutoShape>(One(deck)).Fill).Color);
    }

    [Fact]
    public void Without_a_color_map_the_aliases_map_as_usual()
    {
        var deck = new Deck { Theme = Theme, ColorMap = string.Empty }.With(Shape(0, 0, 10, 10, spPr: "<a:solidFill><a:schemeClr val=\"bg1\"/></a:solidFill>")
            + Shape(0, 0, 10, 10, spPr: "<a:solidFill><a:schemeClr val=\"tx2\"/></a:solidFill>"));
        var shapes = One(deck).Shapes;
        SameColor(PdfColor.White, Assert.IsType<SolidFill>(((AutoShape)shapes[0]).Fill).Color);
        SameColor(PdfColor.FromRgb(0x1F, 0x49, 0x7D), Assert.IsType<SolidFill>(((AutoShape)shapes[1]).Fill).Color);
    }

    [Fact]
    public void Master_and_layout_shapes_are_background()
    {
        var slide = One(new Deck { MasterShapes = Shape(0, 0, 50, 50, Para("Logo")), LayoutShapes = Shape(0, 0, 50, 50, Para("Band")) }.With(Shape(0, 0, 50, 50, Para("Own"))));
        Assert.Equal([true, true, false], slide.Shapes.Select(s => s.Background));
    }

    [Fact]
    public void Luminance_tint_and_shade_transforms()
    {
        var shapes = string.Concat(
            Shape(0, 0, 10, 10, spPr: "<a:solidFill><a:srgbClr val=\"4F81BD\"><a:lumMod val=\"75000\"/></a:srgbClr></a:solidFill>"),
            Shape(0, 0, 10, 10, spPr: "<a:solidFill><a:srgbClr val=\"4F81BD\"><a:lumMod val=\"40000\"/><a:lumOff val=\"60000\"/></a:srgbClr></a:solidFill>"),
            Shape(0, 0, 10, 10, spPr: "<a:solidFill><a:srgbClr val=\"000000\"><a:tint val=\"50000\"/></a:srgbClr></a:solidFill>"),
            Shape(0, 0, 10, 10, spPr: "<a:solidFill><a:srgbClr val=\"FFFFFF\"><a:shade val=\"50000\"/></a:srgbClr></a:solidFill>"));
        var fills = One(Deck.Of(shapes)).Shapes.Select(s => ((SolidFill)((AutoShape)s).Fill!).Color).ToList();

        // HSL luminance: 4F81BD is h 213, s 0.47, l 0.53.
        SameColor(PdfColor.FromRgb(0x36, 0x5F, 0x92), fills[0]);
        SameColor(PdfColor.FromRgb(0xB9, 0xCD, 0xE5), fills[1]);

        // Tint and shade work in linear light: half of black toward white is 0.735 in sRGB, half of white 0.735 too.
        Close(0.735, fills[2].Red, 0.01);
        Close(0.735, fills[3].Red, 0.01);
    }

    [Fact]
    public void Gradient_pattern_and_group_fills()
    {
        var gradient = "<a:gradFill><a:gsLst><a:gs pos=\"100000\"><a:srgbClr val=\"0000FF\"/></a:gs><a:gs pos=\"0\"><a:srgbClr val=\"FF0000\"/></a:gs></a:gsLst><a:lin ang=\"2700000\"/></a:gradFill>";
        var group = "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"5\" name=\"G\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr>" + Xfrm(0, 0, 10, 10)
            + "<a:solidFill><a:srgbClr val=\"00FF00\"/></a:solidFill></p:grpSpPr>" + Shape(0, 0, 10, 10, spPr: "<a:grpFill/>") + "</p:grpSp>";
        var slide = One(Deck.Of(
            Shape(0, 0, 10, 10, spPr: gradient)
            + Shape(0, 0, 10, 10, spPr: "<a:gradFill><a:gsLst><a:gs pos=\"0\"><a:srgbClr val=\"FFFFFF\"/></a:gs></a:gsLst><a:path path=\"circle\"/></a:gradFill>")
            + Shape(0, 0, 10, 10, spPr: "<a:pattFill prst=\"pct50\"><a:fgClr><a:srgbClr val=\"000000\"/></a:fgClr><a:bgClr><a:srgbClr val=\"FFFFFF\"/></a:bgClr></a:pattFill>")
            + group));
        var linear = Assert.IsType<GradientFill>(((AutoShape)slide.Shapes[0]).Fill);
        Assert.False(linear.Radial);
        Assert.Equal(45, linear.Angle, 3);
        Assert.Equal([0.0, 1.0], linear.Stops.Select(s => s.Position));
        SameColor(new PdfColor(0.5, 0, 0.5), linear.ColorAt(0.5));
        SameColor(PdfColor.FromRgb(255, 0, 0), linear.ColorAt(-1));
        SameColor(PdfColor.FromRgb(0, 0, 255), linear.ColorAt(2));
        Assert.True(Assert.IsType<GradientFill>(((AutoShape)slide.Shapes[1]).Fill).Radial);
        SameColor(new PdfColor(0.5, 0.5, 0.5), Assert.IsType<SolidFill>(((AutoShape)slide.Shapes[2]).Fill).Color);
        var child = Assert.IsType<AutoShape>(Assert.Single(Assert.IsType<GroupShape>(slide.Shapes[3]).Children));
        SameColor(PdfColor.FromRgb(0, 255, 0), Assert.IsType<SolidFill>(child.Fill).Color);
    }

    [Fact]
    public void Groups_keep_their_child_coordinate_space_and_transforms_are_read()
    {
        var group = "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"5\" name=\"G\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr>"
            + $"<a:xfrm rot=\"5400000\"><a:off x=\"{Emu(100)}\" y=\"{Emu(100)}\"/><a:ext cx=\"{Emu(200)}\" cy=\"{Emu(100)}\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"{Emu(400)}\" cy=\"{Emu(200)}\"/></a:xfrm></p:grpSpPr>"
            + Shape(0, 0, 400, 200, Para("Child"), xfrmAttributes: "flipH=\"1\" flipV=\"1\" rot=\"-1800000\"") + "</p:grpSp>";
        var shape = Assert.IsType<GroupShape>(Assert.Single(One(Deck.Of(group)).Shapes));
        Assert.Equal(new ShapeBox(100, 100, 200, 100, 90), shape.Box);
        Assert.Equal((0, 0, 400, 200), (shape.ChildX, shape.ChildY, shape.ChildWidth, shape.ChildHeight));
        var child = Assert.Single(shape.Children);
        Assert.Equal(new ShapeBox(0, 0, 400, 200, -30, true, true), child.Box);
    }

    [Fact]
    public void Groups_without_visible_children_are_left_out()
    {
        var group = "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"5\" name=\"G\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr>" + Xfrm(0, 0, 10, 10) + "</p:grpSpPr>"
            + Shape(0, 0, 10, 10, Para("x"), attributes: "hidden=\"1\"") + "</p:grpSp>";
        Assert.Empty(One(Deck.Of(group)).Shapes);
    }

    [Fact]
    public void Body_properties_insets_anchor_wrap_flow_and_autofit()
    {
        var slide = One(Deck.Of(
            Shape(0, 0, 100, 100, Para("A"), bodyPr: $"<a:bodyPr lIns=\"0\" tIns=\"{Emu(10)}\" rIns=\"{Emu(20)}\" bIns=\"{Emu(30)}\" anchor=\"ctr\" anchorCtr=\"1\" wrap=\"none\" vert=\"vert270\"><a:normAutofit fontScale=\"62500\" lnSpcReduction=\"20000\"/></a:bodyPr>")
            + Shape(0, 0, 100, 100, Para("B"), bodyPr: "<a:bodyPr vert=\"eaVert\"><a:spAutoFit/></a:bodyPr>")));
        var a = ((AutoShape)slide.Shapes[0]).Text!;
        Assert.Equal((0.0, 10.0, 20.0, 30.0), (a.LeftInset, a.TopInset, a.RightInset, a.BottomInset));
        Assert.Equal(TextAnchor.Middle, a.Anchor);
        Assert.True(a.AnchorCenter);
        Assert.False(a.Wrap);
        Assert.Equal(TextFlow.Vertical270, a.Flow);
        Assert.True(a.ShrinkOnOverflow);
        Assert.Equal(0.625, a.FontScale, 6);
        Assert.Equal(0.2, a.LineSpacingReduction, 6);
        var b = ((AutoShape)slide.Shapes[1]).Text!;
        Assert.Equal((7.2, 3.6, 7.2, 3.6), (b.LeftInset, b.TopInset, b.RightInset, b.BottomInset));
        Assert.Equal(TextAnchor.Top, b.Anchor);
        Assert.Equal(TextFlow.Vertical, b.Flow);
        Assert.False(b.ShrinkOnOverflow);
    }

    [Fact]
    public void Paragraph_and_run_properties()
    {
        var text = "<a:p><a:pPr algn=\"just\" marR=\"127000\"><a:lnSpc><a:spcPct val=\"150000\"/></a:lnSpc><a:spcBef><a:spcPts val=\"600\"/></a:spcBef><a:spcAft><a:spcPct val=\"50000\"/></a:spcAft></a:pPr>"
            + "<a:r><a:rPr sz=\"2400\" b=\"1\" i=\"1\" u=\"dbl\" strike=\"sngStrike\" baseline=\"30000\" cap=\"all\" spc=\"200\"><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill><a:latin typeface=\"Georgia\"/><a:ea typeface=\"MS Mincho\"/><a:cs typeface=\"Arial\"/></a:rPr><a:t>Styled</a:t></a:r>"
            + "<a:br><a:rPr sz=\"1000\"/></a:br><a:r><a:rPr><a:noFill/></a:rPr><a:t>a&#x0B;b&#x01;c_x000B_d_x005F_x0041_</a:t></a:r><a:endParaRPr sz=\"900\"/></a:p>"
            + "<a:p><a:pPr algn=\"r\"><a:lnSpc><a:spcPts val=\"3000\"/></a:lnSpc></a:pPr><a:r><a:t>Right</a:t></a:r></a:p>";
        var paragraphs = Only<AutoShape>(One(Deck.Of(Shape(0, 0, 100, 100, text)))).Text!.Paragraphs;
        var p = paragraphs[0];
        Assert.Equal(ParagraphAlignment.Justify, p.Alignment);
        Assert.Equal(10, p.MarginRight, 3);
        Assert.Equal(new Spacing(1.5, false), p.LineSpacing);
        Assert.Equal(new Spacing(6, true), p.SpaceBefore);
        Assert.Equal(new Spacing(0.5, false), p.SpaceAfter);
        Assert.Equal(9, p.EndStyle.Size);
        var styled = p.Runs[0].Style;
        Assert.Equal((24.0, true, true, true, true, true), (styled.Size, styled.Bold, styled.Italic, styled.Underline, styled.DoubleUnderline, styled.Strike));
        Assert.Equal(0.3, styled.Baseline, 6);
        Assert.True(styled.AllCaps);
        Assert.Equal(2, styled.CharacterSpacing, 6);
        Assert.Equal(("Georgia", "MS Mincho", "Arial"), (styled.Font, styled.EastAsianFont, styled.ComplexFont));
        SameColor(PdfColor.FromRgb(255, 0, 0), styled.Color);
        Assert.Equal("\n", p.Runs[1].Text);
        Assert.Equal(10, p.Runs[1].Style.Size);
        Assert.Equal("a\nbc\nd_x0041_", p.Runs[2].Text);
        Assert.Equal(0, p.Runs[2].Style.Color.Alpha);
        Assert.Equal(ParagraphAlignment.Right, paragraphs[1].Alignment);
        Assert.Equal(new Spacing(30, true), paragraphs[1].LineSpacing);
        Assert.Equal(18, paragraphs[1].Runs[0].Style.Size);
        Assert.False(paragraphs[1].Runs[0].Style.Bold);
    }

    [Fact]
    public void Bullets_characters_symbol_fonts_numbers_and_none()
    {
        static string Bullet(string text, string bullet, int level = 0) =>
            $"<a:p><a:pPr lvl=\"{level}\">{bullet}</a:pPr><a:r><a:t>{text}</a:t></a:r></a:p>";
        var number = "<a:buAutoNum type=\"arabicPeriod\"/>";
        var text = Bullet("Char", "<a:buClr><a:srgbClr val=\"00FF00\"/></a:buClr><a:buSzPct val=\"150000\"/><a:buFont typeface=\"Arial\"/><a:buChar char=\"&#8226;\"/>")
            + Bullet("Wingdings", "<a:buFont typeface=\"Wingdings\"/><a:buChar char=\"&#167;\"/>")
            + Bullet("Unknown symbol", "<a:buFont typeface=\"Symbol\"/><a:buChar char=\"Q\"/>")
            + Bullet("One", number) + Bullet("Two", number) + Bullet("Sub a", "<a:buAutoNum type=\"alphaLcParenR\"/>", 1)
            + Bullet("Sub b", "<a:buAutoNum type=\"alphaLcParenR\"/>", 1) + Bullet("Three", number)
            + Bullet("Sub restarted", "<a:buAutoNum type=\"alphaLcParenR\"/>", 1)
            + "<a:p><a:pPr><a:buAutoNum type=\"arabicPeriod\"/></a:pPr></a:p>"
            + Bullet("Four", number) + Bullet("Plain", "<a:buNone/>") + Bullet("Restarted", "<a:buAutoNum type=\"romanUcPeriod\" startAt=\"4\"/>")
            + Bullet("Picture", "<a:buBlip/>") + Bullet("Points", "<a:buSzPts val=\"1400\"/><a:buChar char=\"o\"/>");
        var paragraphs = Only<AutoShape>(One(Deck.Of(Shape(0, 0, 400, 400, text)))).Text!.Paragraphs;
        var first = paragraphs[0].Bullet!;
        Assert.Equal(("\u2022", "Arial", 1.5), (first.Text, first.Font, first.SizeFactor));
        SameColor(PdfColor.FromRgb(0, 255, 0), first.Color!.Value);
        Assert.Equal(("\u25AA", null), (paragraphs[1].Bullet!.Text, paragraphs[1].Bullet!.Font));
        Assert.Equal("\u2022", paragraphs[2].Bullet!.Text);
        Assert.Equal(["1.", "2.", "a)", "b)", "3.", "a)"], paragraphs.Skip(3).Take(6).Select(p => p.Bullet!.Text));
        Assert.Null(paragraphs[9].Bullet);
        Assert.Equal("4.", paragraphs[10].Bullet!.Text);
        Assert.Null(paragraphs[11].Bullet);
        Assert.Equal("IV.", paragraphs[12].Bullet!.Text);
        Assert.Equal("\u2022", paragraphs[13].Bullet!.Text);
        Assert.Equal(14, paragraphs[14].Bullet!.SizePoints);
    }

    [Theory]
    [InlineData("arabicPeriod", 3, "3.")]
    [InlineData("arabicParenR", 12, "12)")]
    [InlineData("arabicParenBoth", 2, "(2)")]
    [InlineData("arabicPlain", 7, "7")]
    [InlineData("romanUcPeriod", 1994, "MCMXCIV.")]
    [InlineData("romanLcParenR", 4, "iv)")]
    [InlineData("alphaUcPeriod", 28, "BB.")]
    [InlineData("alphaLcPeriod", 1, "a.")]
    [InlineData("arabic1Minus", 5, "- 5 -")]
    [InlineData("circleNumDbPlain", 5, "5")]
    public void Numbering_schemes(string scheme, int number, string expected) => Assert.Equal(expected, PptxReader.Number(scheme, number));

    [Fact]
    public void Hyperlinks_slide_numbers_and_shape_links()
    {
        var deck = new Deck
        {
            Theme = Theme,
            SlideRelationships = $"<Relationship Id=\"rId5\" Type=\"{R}/hyperlink\" Target=\"https://example.com/a b\" TargetMode=\"External\"/>"
                + $"<Relationship Id=\"rId6\" Type=\"{R}/hyperlink\" Target=\"javascript:alert(1)\" TargetMode=\"External\"/>",
        }.With(Shape(0, 0, 100, 100, "<a:p><a:r><a:rPr><a:hlinkClick r:id=\"rId5\"/></a:rPr><a:t>Link</a:t></a:r><a:r><a:rPr><a:hlinkClick r:id=\"rId6\"/></a:rPr><a:t>Script</a:t></a:r>"
            + "<a:fld type=\"slidenum\"><a:t>99</a:t></a:fld><a:fld type=\"datetime1\"><a:t>1/1/2026</a:t></a:fld></a:p>")
            + "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Linked\"><a:hlinkClick r:id=\"rId5\"/></p:cNvPr><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr>" + Xfrm(0, 0, 10, 10) + "</p:spPr></p:sp>");
        var slide = One(deck);
        var runs = ((AutoShape)slide.Shapes[0]).Text!.Paragraphs[0].Runs;
        Assert.Equal("https://example.com/a%20b", runs[0].Link!.AbsoluteUri);
        Assert.True(runs[0].Style.Underline);
        SameColor(PdfColor.FromRgb(0, 0, 255), runs[0].Style.Color);
        Assert.Null(runs[1].Link);
        Assert.False(runs[1].Style.Underline);
        Assert.Equal("1", runs[2].Text);
        Assert.Equal("1/1/2026", runs[3].Text);
        Assert.Equal("https://example.com/a%20b", slide.Shapes[1].Link!.AbsoluteUri);
    }

    [Fact]
    public void Theme_fonts_and_default_text_style_of_text_boxes()
    {
        var deck = new Deck
        {
            Theme = Theme,
            DefaultTextStyle = "<a:lvl1pPr><a:defRPr sz=\"2000\"><a:solidFill><a:srgbClr val=\"336699\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/></a:defRPr></a:lvl1pPr>",
            MasterTextStyles = "<p:otherStyle><a:lvl1pPr><a:defRPr sz=\"1000\"/></a:lvl1pPr></p:otherStyle>",
        }.With(Shape(0, 0, 100, 100, Para("Box")));
        var slide = One(deck);
        var style = ((AutoShape)slide.Shapes[0]).Text!.Paragraphs[0].Runs[0].Style;
        Assert.Equal((20.0, "Major Face"), (style.Size, style.Font));
        SameColor(PdfColor.FromRgb(0x33, 0x66, 0x99), style.Color);
    }

    [Fact]
    public void Placeholder_fills_inherit_and_preset_adjustments_and_unknown_presets()
    {
        var reports = new List<SlideDiagnostic>();
        var deck = new Deck
        {
            LayoutShapes = Shape(10, 10, 100, 50, ph: "<p:ph type=\"title\"/>", spPr: "<a:prstGeom prst=\"roundRect\"><a:avLst><a:gd name=\"adj\" fmla=\"val 30000\"/></a:avLst></a:prstGeom><a:solidFill><a:srgbClr val=\"ABCDEF\"/></a:solidFill>"),
        }.With(Shape(0, 0, 0, 0, Para("T"), ph: "<p:ph type=\"title\"/>") + Shape(0, 0, 10, 10, spPr: "<a:prstGeom prst=\"sun\"/><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill>"));
        var slide = Assert.Single(Read(deck.Build(), new PptxConverterOptions { Diagnostics = reports.Add }).Slides);
        var title = (AutoShape)slide.Shapes[0];
        SameColor(PdfColor.FromRgb(0xAB, 0xCD, 0xEF), Assert.IsType<SolidFill>(title.Fill).Color);
        var preset = Assert.IsType<PresetGeometry>(title.Geometry);
        Assert.Equal("roundRect", preset.Name);
        Assert.True(preset.IsKnown);

        // The radius is 30% of the shorter side: the first segment starts 15 points in.
        Assert.Equal(15, preset.Build(100, 50)[0].Segments[0].X, 6);
        Assert.False(Assert.IsType<PresetGeometry>(((AutoShape)slide.Shapes[1]).Geometry).IsKnown);
        Assert.Contains(reports, r => r.Subject == "shape" && r.Message.Contains("'sun'", StringComparison.Ordinal));
    }

    [Fact]
    public void Free_form_geometry_with_guides()
    {
        var custom = "<a:custGeom><a:avLst/><a:gdLst><a:gd name=\"half\" fmla=\"*/ w 1 2\"/></a:gdLst><a:pathLst>"
            + "<a:path w=\"200\" h=\"100\"><a:moveTo><a:pt x=\"0\" y=\"0\"/></a:moveTo><a:lnTo><a:pt x=\"200\" y=\"0\"/></a:lnTo><a:cubicBezTo><a:pt x=\"200\" y=\"50\"/><a:pt x=\"100\" y=\"100\"/><a:pt x=\"0\" y=\"100\"/></a:cubicBezTo><a:close/></a:path>"
            + "<a:path fill=\"none\" stroke=\"0\"><a:moveTo><a:pt x=\"half\" y=\"0\"/></a:moveTo><a:quadBezTo><a:pt x=\"0\" y=\"0\"/><a:pt x=\"0\" y=\"half\"/></a:quadBezTo>"
            + "<a:arcTo wR=\"100\" hR=\"100\" stAng=\"0\" swAng=\"5400000\"/></a:path></a:pathLst></a:custGeom>";
        var shape = Only<AutoShape>(One(Deck.Of(Shape(0, 0, 100, 50, spPr: custom))));
        var geometry = Assert.IsType<CustomGeometry>(shape.Geometry);
        var paths = geometry.Build(100, 50);
        Assert.Equal(2, paths.Count);
        Assert.Equal([SegmentKind.Move, SegmentKind.Line, SegmentKind.Cubic, SegmentKind.Close], paths[0].Segments.Select(s => s.Kind));
        Assert.Equal((100.0, 0.0), (paths[0].Segments[1].X, paths[0].Segments[1].Y));
        Assert.Equal((0.0, 50.0), (paths[0].Segments[2].X, paths[0].Segments[2].Y));
        Assert.True(paths[0].Filled && paths[0].Stroked && !paths[0].IsOpen);
        Assert.False(paths[1].Filled || paths[1].Stroked);

        // The guide is half the width in EMUs, and coordinates without a path size are EMUs.
        Assert.Equal(50, paths[1].Segments[0].X, 6);
        Assert.Equal(50, paths[1].Segments[1].Y, 6);
    }

    [Fact]
    public void Backgrounds_inherit_and_come_from_theme_styles()
    {
        var own = One(new Deck { Theme = Theme, SlideBackground = "<p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"102030\"/></a:solidFill></p:bgPr></p:bg>" }.With(string.Empty));
        SameColor(PdfColor.FromRgb(0x10, 0x20, 0x30), Assert.IsType<SolidFill>(own.Background).Color);
        var master = One(new Deck { Theme = Theme, MasterBackground = "<p:bg><p:bgRef idx=\"1002\"><a:srgbClr val=\"000000\"/></p:bgRef></p:bg>" }.With(string.Empty));
        Close(0.735, Assert.IsType<SolidFill>(master.Background).Color.Red, 0.01);
        var third = One(new Deck { Theme = Theme, MasterBackground = "<p:bg><p:bgRef idx=\"1003\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg>" }.With(string.Empty));
        SameColor(PdfColor.FromRgb(0x12, 0x34, 0x56), Assert.IsType<SolidFill>(third.Background).Color);
        Assert.Null(One(new Deck { Theme = Theme }.With(string.Empty)).Background);
    }

    [Fact]
    public void Pictures_with_crop_border_opacity_and_missing_data()
    {
        var reports = new List<SlideDiagnostic>();
        var picture = "<p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"Pic\" descr=\"A photo\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rId7\"><a:alphaModFix amt=\"40000\"/></a:blip>"
            + "<a:srcRect l=\"10000\" t=\"20000\" r=\"30000\" b=\"-5000\"/><a:stretch/></p:blipFill><p:spPr>" + Xfrm(10, 20, 30, 40) + "<a:prstGeom prst=\"ellipse\"/><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill></a:ln></p:spPr></p:pic>"
            + "<p:pic><p:nvPicPr><p:cNvPr id=\"5\" name=\"Linked\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:link=\"rId8\"/></p:blipFill><p:spPr>" + Xfrm(0, 0, 5, 5) + "</p:spPr></p:pic>";
        var deck = new Deck { SlideRelationships = $"<Relationship Id=\"rId7\" Type=\"{R}/image\" Target=\"../media/image1.png\"/><Relationship Id=\"rId8\" Type=\"{R}/image\" Target=\"http://example.com/x.png\" TargetMode=\"External\"/>" }.With(picture);
        deck.Parts.Add(("ppt/media/image1.png", [1, 2, 3]));
        var slide = Assert.Single(Read(deck.Build(), new PptxConverterOptions { Diagnostics = reports.Add }).Slides);
        var pic = Assert.IsType<PictureShape>(slide.Shapes[0]);
        Assert.Equal([1, 2, 3], pic.Data);
        Assert.Equal("ppt/media/image1.png", pic.Source);
        Assert.Equal(new Crop(0.1, 0.2, 0.3, -0.05), pic.Crop);
        Assert.Equal(0.4, pic.Opacity, 6);
        Assert.Equal("A photo", pic.AltText);
        Assert.Equal("ellipse", Assert.IsType<PresetGeometry>(pic.Geometry).Name);
        Assert.NotNull(pic.Line);
        Assert.IsType<SolidFill>(pic.Fill);
        Assert.Null(Assert.IsType<PictureShape>(slide.Shapes[1]).Data);
        Assert.Contains(reports, r => r.Subject == "picture" && r.Message.Contains("linked", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Alternate_content_uses_the_fallback()
    {
        var content = "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"><mc:Choice Requires=\"p14\">"
            + Shape(0, 0, 10, 10, Para("Choice")) + "</mc:Choice><mc:Fallback>" + Shape(0, 0, 10, 10, Para("Fallback")) + "</mc:Fallback></mc:AlternateContent>"
            + "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"><mc:Choice Requires=\"p14\">"
            + Shape(0, 0, 10, 10, Para("Only choice")) + "</mc:Choice></mc:AlternateContent>";
        var slide = One(Deck.Of(content));
        Assert.Equal(["Fallback", "Only choice"], slide.Shapes.Select(s => ((AutoShape)s).Text!.Paragraphs[0].Runs[0].Text));
    }

    [Fact]
    public void Charts_ink_and_unknown_frames_are_reported()
    {
        static string Frame(string uri, string inner = "") =>
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"F\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>"
            + $"<p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(100)}\" cy=\"{Emu(100)}\"/></p:xfrm><a:graphic><a:graphicData uri=\"{uri}\">{inner}</a:graphicData></a:graphic></p:graphicFrame>";
        var reports = new List<SlideDiagnostic>();
        var deck = Deck.Of(Frame("http://schemas.openxmlformats.org/drawingml/2006/chart") + Frame("urn:other") + "<p:contentPart r:id=\"rId9\"/>"
            + Frame("http://schemas.openxmlformats.org/drawingml/2006/diagram", "<dgm:relIds xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>")
            + Frame("http://schemas.openxmlformats.org/presentationml/2006/ole", "<p:oleObj/>"));
        var slide = Assert.Single(Read(deck.Build(), new PptxConverterOptions { Diagnostics = reports.Add }).Slides);
        Assert.Empty(slide.Shapes);
        Assert.Equal(["chart", "graphic", "ink", "SmartArt", "OLE object"], reports.Select(r => r.Subject));
        Assert.All(reports, r => Assert.Equal(1, r.SlideNumber));
    }

    [Fact]
    public void SmartArt_is_read_from_its_stored_drawing_and_embedded_objects_from_their_picture()
    {
        var frame = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"Diagram\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>"
            + $"<p:xfrm><a:off x=\"{Emu(100)}\" y=\"{Emu(50)}\"/><a:ext cx=\"{Emu(300)}\" cy=\"{Emu(200)}\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\">"
            + "<dgm:relIds xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" r:dm=\"rId20\"/></a:graphicData></a:graphic></p:graphicFrame>";
        var ole = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"Object\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>"
            + $"<p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(80)}\" cy=\"{Emu(60)}\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/presentationml/2006/ole\">"
            + "<p:oleObj><p:embed/><p:pic><p:nvPicPr><p:cNvPr id=\"5\" name=\"\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rId22\"/></p:blipFill><p:spPr>" + Xfrm(1, 1, 1, 1) + "</p:spPr></p:pic></p:oleObj></a:graphicData></a:graphic></p:graphicFrame>";
        var deck = new Deck
        {
            SlideRelationships = $"<Relationship Id=\"rId20\" Type=\"{R}/diagramData\" Target=\"../diagrams/data1.xml\"/>"
                + $"<Relationship Id=\"rId21\" Type=\"http://schemas.microsoft.com/office/2007/relationships/diagramDrawing\" Target=\"../diagrams/drawing1.xml\"/>"
                + $"<Relationship Id=\"rId22\" Type=\"{R}/image\" Target=\"../media/ole.png\"/>",
        }.With(frame + ole);
        deck.Parts.Add(("ppt/diagrams/data1.xml", System.Text.Encoding.UTF8.GetBytes("<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"><dgm:extLst><a:ext xmlns:a=\"" + A + "\"><dsp:dataModelExt xmlns:dsp=\"http://schemas.microsoft.com/office/drawing/2008/diagram\" relId=\"rId21\"/></a:ext></dgm:extLst></dgm:dataModel>")));
        deck.Parts.Add(("ppt/diagrams/drawing1.xml", System.Text.Encoding.UTF8.GetBytes($"<dsp:drawing xmlns:dsp=\"http://schemas.microsoft.com/office/drawing/2008/diagram\" xmlns:a=\"{A}\"><dsp:spTree>"
            + $"<dsp:sp><dsp:nvSpPr><dsp:cNvPr id=\"0\" name=\"\"/><dsp:cNvSpPr/></dsp:nvSpPr><dsp:spPr>{Xfrm(10, 10, 100, 50)}<a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></dsp:spPr>"
            + $"<dsp:txBody><a:bodyPr/><a:lstStyle/>{Para("Step")}</dsp:txBody><dsp:txXfrm><a:off x=\"{Emu(20)}\" y=\"{Emu(15)}\"/><a:ext cx=\"{Emu(80)}\" cy=\"{Emu(40)}\"/></dsp:txXfrm></dsp:sp></dsp:spTree></dsp:drawing>")));
        deck.Parts.Add(("ppt/media/ole.png", [9]));
        var slide = One(deck);
        var group = Assert.IsType<GroupShape>(slide.Shapes[0]);
        Assert.Equal((0.0, 0.0, 300.0, 200.0), (group.ChildX, group.ChildY, group.ChildWidth, group.ChildHeight));
        Assert.Equal(2, group.Children.Count);
        Assert.Null(((AutoShape)group.Children[0]).Text);
        Assert.IsType<SolidFill>(((AutoShape)group.Children[0]).Fill);
        var text = (AutoShape)group.Children[1];
        Assert.Equal(new ShapeBox(20, 15, 80, 40), text.Box);
        Assert.Equal("Step", text.Text!.Paragraphs[0].Runs[0].Text);
        var picture = Assert.IsType<PictureShape>(slide.Shapes[1]);
        Assert.Equal(new ShapeBox(0, 0, 80, 60), picture.Box);
        Assert.Equal([9], picture.Data);
    }

    [Fact]
    public void Tables_with_the_default_style_merges_and_own_cell_properties()
    {
        static string Cell(string text, string attributes = "", string tcPr = "<a:tcPr/>") =>
            $"<a:tc {attributes}><a:txBody><a:bodyPr/><a:lstStyle/>{Para(text)}</a:txBody>{tcPr}</a:tc>";
        var table = "<a:tbl><a:tblPr firstRow=\"1\" bandRow=\"1\"><a:tableStyleId>{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}</a:tableStyleId></a:tblPr>"
            + $"<a:tblGrid><a:gridCol w=\"{Emu(100)}\"/><a:gridCol w=\"{Emu(150)}\"/></a:tblGrid>"
            + $"<a:tr h=\"{Emu(30)}\">{Cell("Head", "gridSpan=\"2\"")}{Cell(string.Empty, "hMerge=\"1\"")}</a:tr>"
            + $"<a:tr h=\"{Emu(20)}\">{Cell("A", "rowSpan=\"2\"")}{Cell("B", tcPr: $"<a:tcPr marL=\"0\" anchor=\"ctr\"><a:lnB w=\"25400\"><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></a:lnB><a:solidFill><a:srgbClr val=\"00FF00\"/></a:solidFill></a:tcPr>")}</a:tr>"
            + $"<a:tr h=\"{Emu(20)}\">{Cell(string.Empty, "vMerge=\"1\"")}{Cell("D")}</a:tr>"
            + $"<a:tr>{Cell("Short row")}</a:tr></a:tbl>";
        var frame = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"Table\" descr=\"Results\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>"
            + $"<p:xfrm><a:off x=\"{Emu(10)}\" y=\"{Emu(20)}\"/><a:ext cx=\"{Emu(250)}\" cy=\"{Emu(70)}\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\">{table}</a:graphicData></a:graphic></p:graphicFrame>";
        var shape = Assert.IsType<TableShape>(Assert.Single(One(new Deck { Theme = Theme }.With(frame)).Shapes));
        Assert.Equal(new ShapeBox(10, 20, 250, 70), shape.Box);
        Assert.Equal([100.0, 150.0], shape.Columns);
        Assert.Equal(4, shape.Rows.Count);
        var head = shape.Rows[0].Cells[0];
        Assert.Equal(2, head.ColumnSpan);
        Assert.True(shape.Rows[0].Cells[1].Merged);
        var headRun = head.Text.Paragraphs[0].Runs[0].Style;
        Assert.True(headRun.Bold);
        SameColor(PdfColor.White, headRun.Color);
        SameColor(PdfColor.FromRgb(0x4F, 0x81, 0xBD), Assert.IsType<SolidFill>(head.Fill).Color);
        Assert.Equal(3, head.Bottom!.Width, 3);

        var a = shape.Rows[1].Cells[0];
        Assert.Equal(2, a.RowSpan);
        Assert.False(a.Text.Paragraphs[0].Runs[0].Style.Bold);
        SameColor(PdfColor.Black, a.Text.Paragraphs[0].Runs[0].Style.Color);
        Assert.True(Assert.IsType<SolidFill>(a.Fill).Color.Blue > 0.8);
        Assert.Equal(1, a.Left!.Width, 3);
        Assert.True(shape.Rows[2].Cells[0].Merged);

        var b = shape.Rows[1].Cells[1];
        SameColor(PdfColor.FromRgb(0, 255, 0), Assert.IsType<SolidFill>(b.Fill).Color);
        Assert.Equal(2, b.Bottom!.Width, 3);
        SameColor(PdfColor.FromRgb(255, 0, 0), b.Bottom.Color);
        Assert.Equal((0.0, 7.2), (b.Text.LeftInset, b.Text.RightInset));
        Assert.Equal(TextAnchor.Middle, b.Text.Anchor);

        // Banded rows alternate: the second data row has the table's fill (accent 1 at 20%), the third the darker band
        // (accent 1 at 40%) as the first; the short row is filled up with empty cells.
        var (second, third) = (((SolidFill)shape.Rows[2].Cells[1].Fill!).Color, ((SolidFill)shape.Rows[3].Cells[0].Fill!).Color);
        Assert.True(second.Red > third.Red, $"{second} {third}");
        Assert.Equal(2, shape.Rows[3].Cells.Count);
        Assert.False(shape.Rows[3].Cells[1].Text.HasText);
    }

    [Fact]
    public void Tables_with_a_style_of_the_presentation_or_none()
    {
        var tableStyles = "<a:tblStyleLst xmlns:a=\"" + A + "\" def=\"{X}\"><a:tblStyle styleId=\"{X}\"><a:wholeTbl><a:tcTxStyle i=\"on\"><a:srgbClr val=\"FF0000\"/></a:tcTxStyle>"
            + "<a:tcStyle><a:tcBdr><a:insideV><a:lnRef idx=\"3\"><a:srgbClr val=\"0000FF\"/></a:lnRef></a:insideV></a:tcBdr><a:fillRef idx=\"2\"><a:srgbClr val=\"FFFFFF\"/></a:fillRef></a:tcStyle></a:wholeTbl>"
            + "<a:lastCol><a:tcTxStyle b=\"on\"><a:fontRef idx=\"major\"/></a:tcTxStyle><a:tcStyle><a:fill><a:noFill/></a:fill></a:tcStyle></a:lastCol></a:tblStyle></a:tblStyleLst>";
        static string Table(string tblPr) =>
            "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>"
            + $"<p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(200)}\" cy=\"{Emu(20)}\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\">"
            + $"<a:tbl>{tblPr}<a:tblGrid><a:gridCol w=\"{Emu(100)}\"/><a:gridCol w=\"{Emu(100)}\"/></a:tblGrid><a:tr h=\"{Emu(20)}\">"
            + $"<a:tc><a:txBody><a:bodyPr/>{Para("x")}</a:txBody></a:tc><a:tc><a:txBody><a:bodyPr/>{Para("y")}</a:txBody></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        var deck = new Deck { Theme = Theme }.With(Table("<a:tblPr lastCol=\"1\"><a:tableStyleId>{X}</a:tableStyleId></a:tblPr>") + Table("<a:tblPr/>") + Table("<a:tblPr><a:tableStyleId>{unknown}</a:tableStyleId></a:tblPr>"));
        deck.Parts.Add(("ppt/tableStyles.xml", System.Text.Encoding.UTF8.GetBytes(tableStyles)));
        var presentationRels = $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{R}/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>"
            + $"<Relationship Id=\"rId10\" Type=\"{R}/slide\" Target=\"slides/slide1.xml\"/><Relationship Id=\"rId3\" Type=\"{R}/tableStyles\" Target=\"tableStyles.xml\"/></Relationships>";
        var parts = ZipParts(deck.Build()).Where(p => p.Name != "ppt/_rels/presentation.xml.rels").Append(("ppt/_rels/presentation.xml.rels", System.Text.Encoding.UTF8.GetBytes(presentationRels)));
        var shapes = One(ZipDeck(parts)).Shapes.Cast<TableShape>().ToList();
        var styled = shapes[0].Rows[0].Cells;
        Assert.True(styled[0].Text.Paragraphs[0].Runs[0].Style.Italic);
        SameColor(PdfColor.FromRgb(255, 0, 0), styled[0].Text.Paragraphs[0].Runs[0].Style.Color);
        Close(0.735, Assert.IsType<SolidFill>(styled[0].Fill).Color.Red);
        Assert.Equal(3, styled[0].Right!.Width, 3);
        SameColor(PdfColor.FromRgb(0, 0, 255), styled[0].Right!.Color);
        Assert.Null(styled[0].Left);
        Assert.Null(styled[1].Fill);
        Assert.True(styled[1].Text.Paragraphs[0].Runs[0].Style.Bold);
        Assert.Equal("Major Face", styled[1].Text.Paragraphs[0].Runs[0].Style.Font);
        Assert.All(shapes.Skip(1), t => Assert.All(t.Rows[0].Cells, c => Assert.Null(c.Fill)));
    }

    [Fact]
    public void Table_style_borders_of_the_outer_edges_and_inside_lines()
    {
        var tableStyles = "<a:tblStyleLst xmlns:a=\"" + A + "\" def=\"{B}\"><a:tblStyle styleId=\"{B}\"><a:wholeTbl><a:tcStyle><a:tcBdr>"
            + "<a:top><a:ln w=\"50800\"><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></a:ln></a:top>"
            + "<a:insideH><a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"00FF00\"/></a:solidFill></a:ln></a:insideH>"
            + "<a:bottom><a:ln w=\"25400\"><a:solidFill><a:srgbClr val=\"0000FF\"/></a:solidFill></a:ln></a:bottom></a:tcBdr></a:tcStyle></a:wholeTbl></a:tblStyle></a:tblStyleLst>";
        var frame = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"T\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>"
            + $"<p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(100)}\" cy=\"{Emu(40)}\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\">"
            + $"<a:tbl><a:tblPr><a:tableStyleId>{{B}}</a:tableStyleId></a:tblPr><a:tblGrid><a:gridCol w=\"{Emu(100)}\"/></a:tblGrid>"
            + string.Concat(Enumerable.Repeat($"<a:tr h=\"{Emu(20)}\"><a:tc><a:txBody><a:bodyPr/>{Para("x")}</a:txBody></a:tc></a:tr>", 3))
            + "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
        var deck = new Deck().With(frame);
        deck.Parts.Add(("ppt/tableStyles.xml", System.Text.Encoding.UTF8.GetBytes(tableStyles)));
        var presentationRels = $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{R}/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>"
            + $"<Relationship Id=\"rId10\" Type=\"{R}/slide\" Target=\"slides/slide1.xml\"/><Relationship Id=\"rId3\" Type=\"{R}/tableStyles\" Target=\"tableStyles.xml\"/></Relationships>";
        var parts = ZipParts(deck.Build()).Where(p => p.Name != "ppt/_rels/presentation.xml.rels").Append(("ppt/_rels/presentation.xml.rels", System.Text.Encoding.UTF8.GetBytes(presentationRels)));
        var rows = Assert.IsType<TableShape>(Assert.Single(One(ZipDeck(parts)).Shapes)).Rows;
        Assert.Equal([4.0, 1.0, 1.0], rows.Select(r => r.Cells[0].Top!.Width));
        Assert.Equal([1.0, 1.0, 2.0], rows.Select(r => r.Cells[0].Bottom!.Width));
    }

    private static Slide One(byte[] pptx) => Assert.Single(Read(pptx).Slides);

    private static byte[] ZipDeck(IEnumerable<(string Name, byte[] Content)> parts) => Zip(parts);

    private static List<(string Name, byte[] Content)> ZipParts(byte[] zip)
    {
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        return [.. archive.Entries.Select(e =>
        {
            using var stream = e.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return (e.FullName, buffer.ToArray());
        })];
    }
}

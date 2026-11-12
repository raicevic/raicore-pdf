using System.Text;
using RaiCore.Pdf.Images;
using RaiCore.Pdf.Svg;

namespace RaiCore.Pdf.Tests.Images;

/// <summary>SVG parsing: path data, transforms, styles and intrinsic sizes.</summary>
public class SvgParsingTests
{
    // ---- encodings ----

    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("ISO-8859-1", false)]
    [InlineData("utf-16le", true)]
    [InlineData("utf-16be", true)]
    [InlineData("utf-8", true)]
    public void Markup_is_decoded_by_its_byte_order_mark_or_declaration(string encodingName, bool byteOrderMark)
    {
        // Decoded before parsing, so the XML reader never switches encodings (fuzzing found it throwing doing so).
        var encoding = Encoding.GetEncoding(encodingName);
        var markup = $"<?xml version=\"1.0\" encoding=\"{encodingName}\"?><svg xmlns=\"http://www.w3.org/2000/svg\"><title>caf\u00E9</title></svg>";
        var data = (byteOrderMark ? encoding.GetPreamble() : []).Concat(encoding.GetBytes(markup)).ToArray();

        var document = SvgDocument.Parse(new MemoryStream(data));

        var title = Assert.Single(document.Root.Elements, e => e.Name == "title");
        Assert.Equal("caf\u00E9", Assert.IsType<SvgTextNode>(Assert.Single(title.Children)).Text);
    }

    [Theory]
    [InlineData("\"?><svg/>")]
    [InlineData("\" encoding=\"utf-8\"?><svg/>")]
    [InlineData("\" encoding=\"no-such-encoding\"?><svg/>")]
    public void Undecodable_bytes_in_the_xml_declaration_load_or_fail_as_a_format_error(string rest)
    {
        // The XML reader's own encoding switch threw ArgumentOutOfRangeException here (found by fuzzing).
        var data = "<?xml version=\"1.0"u8.ToArray().Concat(Enumerable.Repeat((byte)0xFF, 8)).Concat(Encoding.ASCII.GetBytes(rest)).ToArray();

        var error = Record.Exception(() => PdfImage.Load(data));

        Assert.True(error is null or ImageFormatException, error?.ToString());
    }

    // ---- path data ----

    [Fact]
    public void Absolute_and_relative_commands_give_absolute_points()
    {
        var path = SvgPathData.Parse("M10 20 L30 40 l5 5 h10 v-5 H0 V0 z m1 1 l1 0");

        Assert.Equal(
            [(PathOp.MoveTo, 10, 20), (PathOp.LineTo, 30, 40), (PathOp.LineTo, 35, 45), (PathOp.LineTo, 45, 45), (PathOp.LineTo, 45, 40),
             (PathOp.LineTo, 0, 40), (PathOp.LineTo, 0, 0), (PathOp.Close, 10, 20), (PathOp.MoveTo, 11, 21), (PathOp.LineTo, 12, 21)],
            Points(path));
    }

    [Theory]
    [InlineData("M1.5.5L-1-2", new double[] { 1.5, 0.5, -1, -2 })]          // A second dot starts a number; so does a sign.
    [InlineData("M1e2,1E-1 L.5-.5", new double[] { 100, 0.1, 0.5, -0.5 })]  // Exponents; leading dots.
    [InlineData("M 0,0 10,10 20 , 20", new double[] { 0, 0, 10, 10, 20, 20 })] // Pairs after M are implicit lines.
    [InlineData("m1 1 2 2", new double[] { 1, 1, 3, 3 })]                   // ... relative after a relative m.
    [InlineData("M0 0L10 10 20 0", new double[] { 0, 0, 10, 10, 20, 0 })]   // Repeated L.
    [InlineData("M+3-4", new double[] { 3, -4 })]
    public void Numbers_need_no_separators(string data, double[] expected) =>
        Assert.Equal(expected, Points(SvgPathData.Parse(data)).SelectMany(p => new[] { p.X, p.Y }));

    [Theory]
    [InlineData("M10 10 L20 20 X 5 5", 2)] // An unknown command ends the path; what came before is drawn.
    [InlineData("M10 10 L20 20 L30", 2)]   // So does a missing number.
    [InlineData("L10 10", 0)]              // Path data must start with a moveto.
    [InlineData("M10 10 Z 5 5", 2)]        // Z takes no numbers.
    [InlineData("", 0)]
    public void Errors_end_the_path_and_keep_what_came_before(string data, int segments) =>
        Assert.Equal(segments, SvgPathData.Parse(data).Segments.Count);

    [Fact]
    public void Smooth_curves_reflect_the_previous_control_point()
    {
        var path = SvgPathData.Parse("M0 0 C 0 10 10 10 10 0 S 20 -10 20 0");
        var smooth = path.Segments[2];

        Assert.Equal((10.0, -10.0, 20.0, -10.0, 20.0, 0.0), (smooth.X1, smooth.Y1, smooth.X2, smooth.Y2, smooth.X, smooth.Y));

        // Without a previous C or S, the first control point is the current point.
        var alone = SvgPathData.Parse("M5 5 S 20 -10 20 0").Segments[1];
        Assert.Equal((5.0, 5.0), (alone.X1, alone.Y1));
    }

    [Fact]
    public void Quadratic_curves_are_raised_to_cubics()
    {
        var path = SvgPathData.Parse("M0 0 Q 10 10 20 0 T 40 0");
        var (q, t) = (path.Segments[1], path.Segments[2]);

        Assert.Equal(R((20 / 3.0, 20 / 3.0, 40 / 3.0, 20 / 3.0)), R((q.X1, q.Y1, q.X2, q.Y2)));

        // T reflects the quadratic control point (10, 10) about (20, 0) to (30, -10).
        Assert.Equal(R((20 + (20 / 3.0), -20 / 3.0, 40 - (20 / 3.0), -20 / 3.0)), R((t.X1, t.Y1, t.X2, t.Y2)));
    }

    [Theory]
    [InlineData("M0 0 A 10 10 0 0 1 20 0", -10)]  // Sweep 1 runs through the top (negative y) in SVG's y-down space.
    [InlineData("M0 0 A 10 10 0 0 0 20 0", 10)]
    [InlineData("M0 0 A 1 1 0 0 1 20 0", -10)]    // Radii too small to reach the end are scaled up.
    [InlineData("M0 0a10 10 0 0120 0", -10)]      // Flags need no separators.
    public void Arcs_become_cubic_curves_on_the_ellipse(string data, double apexY)
    {
        var path = SvgPathData.Parse(data);

        Assert.All(path.Segments.Skip(1), s => Assert.Equal(PathOp.CubicTo, s.Op));
        Assert.Equal(2, path.Segments.Count - 1); // A half circle takes two quarter curves.
        Assert.Equal((20.0, 0.0), (path.Segments[^1].X, path.Segments[^1].Y));
        foreach (var (x, y) in SampleCurves(path))
        {
            Assert.Equal(10, Math.Sqrt(((x - 10) * (x - 10)) + (y * y)), 0.02); // On the circle around (10, 0).
        }

        var bounds = path.Bounds()!.Value;
        Assert.Equal(apexY, apexY < 0 ? bounds.MinY : bounds.MaxY, 2);
    }

    [Fact]
    public void Large_arcs_and_rotated_ellipses_reach_their_end_point()
    {
        // The large arc of a circle of radius 10 through (0, 0) and (10, 10): three quarters of the circle.
        var large = SvgPathData.Parse("M0 0 A10 10 0 1 0 10 10");
        Assert.Equal(3, large.Segments.Count - 1);
        Assert.Equal((10.0, 10.0), (large.Segments[^1].X, large.Segments[^1].Y));
        Assert.All(SampleCurves(large), p => Assert.Equal(10, Distance(p, (0, 10)), 0.02)); // Center (0, 10).

        // An ellipse with radii 20 and 10 rotated by 90 degrees is 10 wide and 20 tall.
        var rotated = SvgPathData.Parse("M0 0 A20 10 90 0 1 0 40");
        var bounds = rotated.Bounds()!.Value;
        Assert.Equal((0.0, 40.0, 10.0), (Math.Round(bounds.MinY, 6), Math.Round(bounds.MaxY, 6), Math.Round(bounds.Width, 3)));
    }

    [Fact]
    public void Degenerate_arcs_are_lines_or_nothing()
    {
        Assert.Equal(PathOp.LineTo, SvgPathData.Parse("M0 0 A0 10 0 0 1 20 0").Segments[1].Op);
        Assert.Single(SvgPathData.Parse("M5 5 A10 10 0 0 1 5 5").Segments);
    }

    [Fact]
    public void Curve_bounds_are_exact()
    {
        // The control points reach y = 10, the curve itself only 7.5.
        var bounds = SvgPathData.Parse("M0 0 C 0 10 10 10 10 0").Bounds()!.Value;

        Assert.Equal((0.0, 0.0, 10.0, 7.5), (bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY));
    }

    // ---- transforms ----

    [Theory]
    [InlineData("translate(10 20) scale(2)", 1, 1, 12, 22)] // The rightmost transform applies first.
    [InlineData("scale(2) translate(10 20)", 1, 1, 22, 42)]
    [InlineData("translate(10)", 1, 1, 11, 1)]
    [InlineData("scale(2, 3)", 1, 1, 2, 3)]
    [InlineData("rotate(90)", 1, 0, 0, 1)]
    [InlineData("rotate(90 10 10)", 20, 10, 10, 20)]
    [InlineData("rotate(90,10,10)", 20, 10, 10, 20)]
    [InlineData("skewX(45)", 0, 1, 1, 1)]
    [InlineData("skewY(45)", 1, 0, 1, 1)]
    [InlineData("matrix(1 2 3 4 5 6)", 1, 1, 9, 12)]
    [InlineData("translate(5,5),scale(2)", 1, 1, 7, 7)]
    [InlineData("  ", 3, 4, 3, 4)]
    public void Transform_lists_are_parsed_and_composed(string text, double x, double y, double expectedX, double expectedY)
    {
        var (tx, ty) = SvgMatrix.Parse(text)!.Value.Apply(x, y);

        Assert.Equal(R((expectedX, expectedY)), R((tx, ty)));
    }

    [Theory]
    [InlineData("translate(10")]
    [InlineData("scale()")]
    [InlineData("rotate(1 2)")]
    [InlineData("shear(3)")]
    public void Invalid_transforms_are_ignored(string text) => Assert.Null(SvgMatrix.Parse(text));

    [Fact]
    public void Then_applies_the_first_matrix_first()
    {
        var both = SvgMatrix.Scale(2, 2).Then(SvgMatrix.Translate(10, 0));

        Assert.Equal((12.0, 2.0), both.Apply(1, 1));
    }

    [Theory]
    [InlineData(null, 200, 100, 2, 0, 0)]               // xMidYMid meet: scale 2 fits the height...
    [InlineData("xMidYMid meet", 200, 200, 2, 0, 50)]   // ... and centers vertically.
    [InlineData("xMinYMin meet", 200, 200, 2, 0, 0)]
    [InlineData("xMaxYMax meet", 200, 200, 2, 0, 100)]
    [InlineData("xMidYMid slice", 200, 200, 4, -100, 0)]
    public void View_boxes_map_into_viewports(string? aspect, double width, double height, double scale, double tx, double ty)
    {
        var box = SvgViewBox.Parse("0 0 100 50")!.Value;
        var matrix = box.Transform(SvgPreserveAspectRatio.Parse(aspect), width, height);

        Assert.Equal((scale, scale, tx, ty), (matrix.A, matrix.D, matrix.E, matrix.F));
    }

    [Fact]
    public void View_boxes_with_offsets_and_none_stretch()
    {
        var box = SvgViewBox.Parse("10,20 100 50")!.Value;
        var matrix = box.Transform(SvgPreserveAspectRatio.Parse("none"), 200, 200);

        Assert.Equal((2.0, 4.0, -20.0, -80.0), (matrix.A, matrix.D, matrix.E, matrix.F));
        Assert.Null(SvgViewBox.Parse("0 0 0 10"));
        Assert.Null(SvgViewBox.Parse("0 0 10"));
    }

    // ---- styles ----

    [Fact]
    public void Styles_cascade_from_attributes_to_sheets_to_the_style_attribute()
    {
        var doc = Parse("""
            <svg xmlns="http://www.w3.org/2000/svg">
              <style>
                rect { fill: green }
                .a { fill: #00f; stroke: red }
                #b { fill: yellow }
                g > .c { stroke-width: 3 }
                .d { fill: purple !important }
              </style>
              <g fill="red" stroke-width="2">
                <rect id="plain" fill="orange"/>
                <rect id="classed" class="a" fill="orange"/>
                <rect id="b" class="a"/>
                <rect id="inline" class="a" style="fill: black; stroke: none"/>
                <rect id="important" class="d" style="fill: black"/>
                <circle id="inherits" class="c"/>
              </g>
            </svg>
            """);

        Assert.Equal("#008000", Color(Style(doc, "plain").Fill));       // A type selector beats the presentation attribute.
        Assert.Equal("#0000ff", Color(Style(doc, "classed").Fill));
        Assert.Equal("#ffff00", Color(Style(doc, "b").Fill));            // An id beats a class.
        Assert.Equal("#000000", Color(Style(doc, "inline").Fill));       // The style attribute beats sheets.
        Assert.Equal(SvgPaintKind.None, Style(doc, "inline").Stroke.Kind);
        Assert.Equal("#800080", Color(Style(doc, "important").Fill));    // !important beats the style attribute.
        Assert.Equal("#ff0000", Color(Style(doc, "inherits").Fill));     // Inherited from the group.
        Assert.Equal(3, Style(doc, "inherits").StrokeWidth);             // Child combinator.
        Assert.Equal(2, Style(doc, "plain").StrokeWidth);
    }

    [Fact]
    public void Opacity_and_display_are_not_inherited_but_visibility_is()
    {
        var doc = Parse("""
            <svg xmlns="http://www.w3.org/2000/svg">
              <g id="g" opacity="0.5" visibility="hidden" fill-opacity="0.25">
                <rect id="r"/>
                <rect id="shown" visibility="visible"/>
              </g>
            </svg>
            """);

        var rect = Style(doc, "r");
        Assert.Equal((1.0, false, 0.25), (rect.Opacity, rect.Visible, rect.FillOpacity));
        Assert.Equal(0.5, Style(doc, "g").Opacity);
        Assert.True(Style(doc, "shown").Visible);
    }

    [Fact]
    public void Current_color_refers_to_the_color_property()
    {
        var doc = Parse("""
            <svg xmlns="http://www.w3.org/2000/svg" color="red">
              <g fill="currentColor"><rect id="r" color="#00f"/></g>
              <stop id="s" stop-color="currentColor"/>
            </svg>
            """);

        var rect = Style(doc, "r");
        Assert.Equal(SvgPaintKind.CurrentColor, rect.Fill.Kind); // Inherited as the keyword ...
        Assert.Equal("#0000ff", Color(rect.Color));              // ... so it uses the rect's own color.
        Assert.Equal("#ff0000", Color(Style(doc, "s").StopColor));
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("2px", 2)]
    [InlineData("3pt", 4)]
    [InlineData("1in", 96)]
    [InlineData("25.4mm", 96)]
    [InlineData("2.54cm", 96)]
    [InlineData("1pc", 16)]
    [InlineData("0.5em", 10)]  // font-size 20
    [InlineData("1ex", 10)]
    [InlineData("10%", 10)]    // of the normalized diagonal of a 100 x 100 viewport
    public void Lengths_resolve_to_user_units(string strokeWidth, double expected)
    {
        var doc = Parse($"""<svg xmlns="http://www.w3.org/2000/svg"><rect id="r" font-size="20" stroke-width="{strokeWidth}"/></svg>""");

        Assert.Equal(expected, Style(doc, "r").StrokeWidth, 6);
    }

    [Theory]
    [InlineData("red", "#ff0000")]
    [InlineData("RebeccaPurple", "#663399")]
    [InlineData("#abc", "#aabbcc")]
    [InlineData("#a1b2c3", "#a1b2c3")]
    [InlineData("rgb(255, 128, 0)", "#ff8000")]
    [InlineData("rgb(100%,0%,50%)", "#ff0080")]
    [InlineData("hsl(120, 100%, 25%)", "#008000")]
    [InlineData("rgba(255,0,0,0.5)", "#ff0000 0.5")]  // Translucent: the alpha is kept.
    [InlineData("#ff000080", "#ff0000 0.502")]
    [InlineData("hsla(240, 100%, 50%, 25%)", "#0000ff 0.25")]
    [InlineData("transparent", "#000000 0")]
    public void Colors_are_parsed(string text, string expected)
    {
        var color = SvgColor.Parse(text)!.Value;
        Assert.Equal(expected, color.Alpha < 1 ? FormattableString.Invariant($"{Color(color)} {Math.Round(color.Alpha, 3)}") : Color(color));
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("#12")]
    [InlineData("rgb(1,2)")]
    public void Invalid_colors_are_rejected(string text) => Assert.Null(SvgColor.Parse(text));

    [Fact]
    public void Invalid_values_keep_the_inherited_value()
    {
        var doc = Parse("""<svg xmlns="http://www.w3.org/2000/svg"><g fill="red" stroke-width="3"><rect id="r" fill="bogus" stroke-width="-1"/></g></svg>""");

        Assert.Equal(("#ff0000", 3.0), (Color(Style(doc, "r").Fill), Style(doc, "r").StrokeWidth));
    }

    [Fact]
    public void Dash_arrays_are_doubled_when_odd_and_dropped_when_zero()
    {
        var doc = Parse("""
            <svg xmlns="http://www.w3.org/2000/svg">
              <rect id="odd" stroke-dasharray="5, 10 15"/><rect id="zero" stroke-dasharray="0 0"/><rect id="bad" stroke-dasharray="5 -1"/>
            </svg>
            """);

        Assert.Equal([5.0, 10, 15, 5, 10, 15], Style(doc, "odd").DashArray);
        Assert.Empty(Style(doc, "zero").DashArray);
        Assert.Empty(Style(doc, "bad").DashArray);
    }

    // ---- documents and sizes ----

    [Theory]
    [InlineData("width='120' height='40'", 90, 30)]
    [InlineData("width='120px' height='1in'", 90, 72)]
    [InlineData("width='10mm' height='5mm'", 28.3465, 14.1732)]
    [InlineData("viewBox='0 0 24 24'", 18, 18)]                            // The view box size, in pixels.
    [InlineData("width='48' viewBox='0 0 24 12'", 36, 18)]                 // The missing height follows the view box.
    [InlineData("height='48' viewBox='0 0 24 12'", 72, 36)]
    [InlineData("width='100%' height='100%' viewBox='0 0 50 20'", 37.5, 15)] // Percentages count as missing.
    [InlineData("", 225, 112.5)]                                            // 300 x 150, the CSS default.
    [InlineData("width='40'", 30, 112.5)]
    public void The_natural_size_comes_from_width_height_or_the_view_box(string attributes, double width, double height)
    {
        var image = PdfImage.LoadSvg($"<svg xmlns='http://www.w3.org/2000/svg' {attributes}/>");

        Assert.Equal(ImageFormat.Svg, image.Format);
        Assert.Equal(R((width, height)), R((image.NaturalWidth, image.NaturalHeight)));
        Assert.Equal((int)Math.Round(width / 0.75), image.PixelWidth);
    }

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'/>")]
    [InlineData("\uFEFF  \n<svg width='10' height='10'></svg>")]
    [InlineData("<?xml version='1.0' encoding='UTF-8'?>\n<!-- logo -->\n<svg width='10' height='10'/>")]
    [InlineData("<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' 'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'><svg width='10' height='10'/>")]
    public void Svg_is_detected_by_content(string markup)
    {
        var image = PdfImage.Load(Encoding.UTF8.GetBytes(markup));

        Assert.Equal((ImageFormat.Svg, 10, 10), (image.Format, image.PixelWidth, image.PixelHeight));
        Assert.True(image.HasTransparency);
    }

    [Fact]
    public void Utf16_svg_is_detected() =>
        Assert.Equal(ImageFormat.Svg, PdfImage.Load([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("<svg width='4' height='4'/>")]).Format);

    [Theory]
    [InlineData("<svg><rect></svg>")]                         // Not well-formed.
    [InlineData("<?xml version='1.0'?><html><svg/></html>")]  // The root is not svg.
    [InlineData("<svg>&nbsp;</svg>")]                         // Undeclared entity.
    public void Malformed_svg_fails_with_an_image_format_error(string markup)
    {
        Assert.Throws<ImageFormatException>(() => PdfImage.LoadSvg(markup));
        Assert.Throws<ImageFormatException>(() => PdfImage.Load(Encoding.UTF8.GetBytes(markup)));
    }

    [Fact]
    public void External_entities_are_never_resolved()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"raicore-secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "SECRET");
        try
        {
            var markup = $"""
                <?xml version="1.0"?>
                <!DOCTYPE svg [ <!ENTITY xxe SYSTEM "file://{secret}"> ]>
                <svg xmlns="http://www.w3.org/2000/svg"><text>&xxe;</text></svg>
                """;

            Assert.Throws<ImageFormatException>(() => PdfImage.LoadSvg(markup));
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void Deep_nesting_is_rejected_instead_of_overflowing_the_stack()
    {
        var markup = "<svg>" + string.Concat(Enumerable.Repeat("<g>", 5000)) + string.Concat(Enumerable.Repeat("</g>", 5000)) + "</svg>";

        Assert.Throws<ImageFormatException>(() => PdfImage.LoadSvg(markup));
    }

    [Fact]
    public void Use_elements_that_multiply_content_are_bounded()
    {
        // Each level references the previous one ten times: 10^9 rectangles if expanded fully.
        var markup = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg'><defs><rect id='l0' width='1' height='1'/>");
        for (var level = 1; level <= 9; level++)
        {
            markup.Append($"<g id='l{level}'>").Append(string.Concat(Enumerable.Repeat($"<use href='#l{level - 1}'/>", 10))).Append("</g>");
        }

        markup.Append("</defs><use href='#l9'/><use href='#self'/><g id='self'><use href='#self'/></g></svg>");
        var doc = new PdfDocument();
        doc.AddPage().Canvas.DrawImage(PdfImage.LoadSvg(markup.ToString()), 0, 0, 100, 100);

        Assert.True(doc.ToArray().Length > 0);
    }

    private static (double, double) R((double A, double B) t) => (Math.Round(t.A, 4), Math.Round(t.B, 4));

    private static (double, double, double, double) R((double A, double B, double C, double D) t) =>
        (Math.Round(t.A, 4), Math.Round(t.B, 4), Math.Round(t.C, 4), Math.Round(t.D, 4));

    private static SvgDocument Parse(string markup) => SvgDocument.Parse(new MemoryStream(Encoding.UTF8.GetBytes(markup)));

    private static SvgStyle Style(SvgDocument doc, string id)
    {
        // Computes the style down the ancestor chain, as the renderer does.
        var chain = new List<SvgElement>();
        for (var e = doc.GetElementById(id); e is not null; e = e.Parent)
        {
            chain.Insert(0, e);
        }

        return chain.Aggregate(SvgStyle.Initial, (style, element) => SvgStyle.Compute(style, element, (100, 100)));
    }

    private static string Color(SvgPaint paint) => Color(paint.Color);

    private static string Color(SvgColor color) => Color(new PdfColor(color.R, color.G, color.B));

    private static string Color(PdfColor color) =>
        $"#{(int)Math.Round(color.Red * 255):x2}{(int)Math.Round(color.Green * 255):x2}{(int)Math.Round(color.Blue * 255):x2}";

    private static List<(PathOp Op, double X, double Y)> Points(SvgPath path) => [.. path.Segments.Select(s => (s.Op, s.X, s.Y))];

    /// <summary>Points along every cubic segment.</summary>
    private static IEnumerable<(double X, double Y)> SampleCurves(SvgPath path)
    {
        var (px, py) = (0.0, 0.0);
        foreach (var s in path.Segments)
        {
            if (s.Op == PathOp.CubicTo)
            {
                for (var t = 0.0; t <= 1; t += 0.125)
                {
                    var u = 1 - t;
                    yield return (
                        (u * u * u * px) + (3 * u * u * t * s.X1) + (3 * u * t * t * s.X2) + (t * t * t * s.X),
                        (u * u * u * py) + (3 * u * u * t * s.Y1) + (3 * u * t * t * s.Y2) + (t * t * t * s.Y));
                }
            }

            (px, py) = (s.X, s.Y);
        }
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}

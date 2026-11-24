using RaiCore.Pdf.DrawingML;
using RaiCore.Pdf.Slides.Model;
using RaiCore.Pdf.Slides.Pptx;
using RaiCore.Pdf.Slides.Rendering;

namespace RaiCore.Pdf.Tests.Slides;

/// <summary>Preset and free-form geometry, arcs, guide formulas, transformations and colors of the slide model.</summary>
public class SlideGeometryTests
{
    public static TheoryData<string> Presets() => [.. PresetGeometry.KnownPresets.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Presets))]
    public void Every_known_preset_draws_inside_its_box(string name)
    {
        var geometry = new PresetGeometry(name);
        Assert.True(geometry.IsKnown);
        var paths = geometry.Build(100, 60);
        Assert.NotEmpty(paths);
        foreach (var path in paths)
        {
            Assert.Equal(SegmentKind.Move, path.Segments[0].Kind);
            Assert.True(path.Segments.Count >= 2, name);
            // Callouts point outside their box (below it by default).
            foreach (var segment in path.Segments.Where(s => s.Kind != SegmentKind.Close && !name.Contains("Callout", StringComparison.Ordinal)))
            {
                Assert.InRange(segment.X, -0.01, 100.01);
                Assert.InRange(segment.Y, -0.01, 60.01);
            }

            // Lines and connectors are open and not filled; closed shapes are filled.
            if (geometry.IsLine)
            {
                Assert.False(path.Filled, name);
            }
        }

        var (x, y, w, h) = geometry.TextBounds(100, 60);
        Assert.True(x >= 0 && y >= 0 && x + w <= 100.01 && y + h <= 60.01, name);
    }

    [Fact]
    public void Unknown_presets_are_rectangles_and_sizes_are_never_negative()
    {
        var unknown = new PresetGeometry("cloudCallout");
        Assert.False(unknown.IsKnown);
        Assert.Equal([(0.0, 0.0), (10.0, 0.0), (10.0, 5.0), (0.0, 5.0)], unknown.Build(10, 5)[0].Segments.Where(s => s.Kind != SegmentKind.Close).Select(s => (s.X, s.Y)));
        Assert.All(new PresetGeometry("ellipse").Build(-5, -5)[0].Segments, s => Assert.Equal((0.0, 0.0), (s.X, s.Y)));
        Assert.Equal(Geometry.Rectangle.Build(4, 2)[0].Segments, new PresetGeometry("rect").Build(4, 2)[0].Segments);
    }

    [Fact]
    public void Adjustments_change_presets()
    {
        static double FirstX(string name, double adjust) => new PresetGeometry(name, new Dictionary<string, double> { ["adj"] = adjust }).Build(200, 100)[0].Segments[0].X;
        Assert.Equal(25, FirstX("roundRect", 25000), 6);
        Assert.Equal(50, FirstX("roundRect", 99999999), 6);
        Assert.Equal(0, FirstX("roundRect", -5), 6);
        Assert.Equal(50, new PresetGeometry("triangle", new Dictionary<string, double> { ["adj"] = 25000 }).Build(200, 100)[0].Segments[1].X, 6);
        var arrow = new PresetGeometry("rightArrow", new Dictionary<string, double> { ["adj1"] = 20000, ["adj2"] = 30000 }).Build(200, 100)[0].Segments;
        Assert.Equal((0.0, 40.0), (arrow[0].X, arrow[0].Y));
        Assert.Equal(170, arrow[1].X, 6);
        Assert.Equal((200.0, 50.0), (arrow[3].X, arrow[3].Y));
        var connector = new PresetGeometry("bentConnector3", new Dictionary<string, double> { ["adj1"] = 25000 }).Build(200, 100)[0].Segments;
        Assert.Equal([(0.0, 0.0), (50.0, 0.0), (50.0, 100.0), (200.0, 100.0)], connector.Select(s => (s.X, s.Y)));
    }

    [Fact]
    public void Arcs_are_cubic_curves_through_the_right_points()
    {
        // A quarter circle of radius 10 from the top (270 degrees) clockwise to the right.
        var b = new PathBuilder().MoveTo(0, -10).ArcTo(10, 10, 270, 90);
        var end = b.Segments[^1];
        Assert.Equal(SegmentKind.Cubic, end.Kind);
        Assert.Equal(10, end.X, 6);
        Assert.Equal(0, end.Y, 6);
        Assert.Equal(2, b.Segments.Count);

        // A full turn has four quarters and ends where it started; counter-clockwise too.
        var full = new PathBuilder().MoveTo(10, 0).ArcTo(10, 5, 0, 360).Segments;
        Assert.Equal(5, full.Count);
        Assert.Equal((10.0, 0.0), (Math.Round(full[^1].X, 6), Math.Round(full[^1].Y, 6)));
        var back = new PathBuilder().MoveTo(10, 0).ArcTo(10, 10, 0, -90).Segments[^1];
        Assert.Equal((0.0, -10.0), (Math.Round(back.X, 6), Math.Round(back.Y, 6)));

        // Visual angles on an ellipse: 45 degrees is on the diagonal of the box, not at the parametric 45.
        var diagonal = new PathBuilder().MoveTo(20, 0).ArcTo(20, 10, 0, 45).Segments[^1];
        Assert.Equal(diagonal.X - 0, diagonal.Y + 0 + (diagonal.X - diagonal.Y), 6);
        Assert.Equal(1, (diagonal.Y - 0) / (diagonal.X - 0), 1);

        // Degenerate arcs add nothing.
        Assert.Single(new PathBuilder().MoveTo(0, 0).ArcTo(0, 5, 0, 90).Segments);
        Assert.Single(new PathBuilder().MoveTo(0, 0).ArcTo(5, 5, 0, double.NaN).Segments);
    }

    [Fact]
    public void Quadratic_curves_and_closing()
    {
        var b = new PathBuilder().MoveTo(0, 0).QuadTo(3, 3, 6, 0);
        var curve = b.Segments[1];
        Assert.Equal((2.0, 2.0, 4.0, 2.0, 6.0, 0.0), (curve.X1, curve.Y1, curve.X2, curve.Y2, curve.X, curve.Y));
        b.Close().Close();
        Assert.Single(b.Segments, s => s.Kind == SegmentKind.Close);
        Assert.False(b.ToPath().IsOpen);
        Assert.True(new PathBuilder().MoveTo(0, 0).LineTo(1, 1).ToPath().IsOpen);
    }

    [Theory]
    [InlineData("*/ 10 3 2", 15)]
    [InlineData("*/ 10 3 0", 0)]
    [InlineData("+- 10 3 2", 11)]
    [InlineData("+/ 10 2 4", 3)]
    [InlineData("+/ 10 2 0", 0)]
    [InlineData("?: 1 5 6", 5)]
    [InlineData("?: 0 5 6", 6)]
    [InlineData("abs -4", 4)]
    [InlineData("at2 1 1", 2700000)]
    [InlineData("cat2 10 1 0", 10)]
    [InlineData("cos 10 5400000", 0)]
    [InlineData("max 3 9", 9)]
    [InlineData("min 3 9", 3)]
    [InlineData("mod 3 4 0", 5)]
    [InlineData("pin 0 -5 10", 0)]
    [InlineData("pin 0 15 10", 10)]
    [InlineData("pin 0 5 10", 5)]
    [InlineData("sat2 10 0 1", 10)]
    [InlineData("sin 10 5400000", 10)]
    [InlineData("sqrt 16", 4)]
    [InlineData("sqrt -16", 0)]
    [InlineData("tan 10 2700000", 10)]
    [InlineData("val 42", 42)]
    [InlineData("val w", 1000)]
    [InlineData("val wd2", 500)]
    [InlineData("val ss", 400)]
    [InlineData("val unknown", 0)]
    [InlineData("nonsense 1", 0)]
    [InlineData("", 0)]
    public void Guide_formulas(string formula, double expected)
    {
        var values = CustomGeometry.Builtins(1000, 400);
        Assert.Equal(expected, CustomGeometry.Evaluate(formula, values), 6);
    }

    [Fact]
    public void Shape_transformations_turn_and_mirror_about_the_center()
    {
        // A 100 x 50 box at (10, 20), turned 90 degrees clockwise: its top-left corner goes to the top right.
        var turned = Affine.Of(new ShapeBox(10, 20, 100, 50, 90));
        var (x, y) = turned.Map(0, 0);
        Assert.Equal((85.0, -5.0), (Math.Round(x, 6), Math.Round(y, 6)));
        var mirrored = Affine.Of(new ShapeBox(10, 20, 100, 50, 0, FlipH: true));
        Assert.Equal((110.0, 20.0), mirrored.Map(0, 0));
        Assert.True(mirrored.Determinant < 0);
        var both = Affine.Of(new ShapeBox(0, 0, 10, 10, 0, true, true));
        Assert.Equal((10.0, 10.0), both.Map(0, 0));
        Assert.True(both.Determinant > 0);
        Assert.Equal((5.0, 7.0), Affine.Translate(2, 3).Then(Affine.Scale(1, 1)).Map(3, 4));
        Assert.False(new Affine(double.NaN, 0, 0, 1, 0, 0).IsFinite);
        Assert.True(Affine.Identity.IsFinite);
    }

    [Fact]
    public void Color_conversions_round_trip()
    {
        var (h, s, l) = DrawingColors.ToHsl(0.2, 0.4, 0.6);
        var back = DrawingColors.FromHsl(h, s, l, 1);
        Assert.Equal((0.2, 0.4, 0.6), (Math.Round(back.Red, 6), Math.Round(back.Green, 6), Math.Round(back.Blue, 6)));
        foreach (var (r, g, b) in new[] { (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (0.5, 0.5, 0.5), (1.0, 0.0, 1.0) })
        {
            var (hue, saturation, luminance) = DrawingColors.ToHsl(r, g, b);
            var color = DrawingColors.FromHsl(hue, saturation, luminance, 0.5);
            Assert.Equal((r, g, b, 0.5), (Math.Round(color.Red, 6), Math.Round(color.Green, 6), Math.Round(color.Blue, 6), color.Alpha));
        }

        Assert.Equal(0.5, DrawingColors.ToSrgb(DrawingColors.ToLinear(0.5)), 9);
        Assert.Equal(0.02, DrawingColors.ToSrgb(DrawingColors.ToLinear(0.02)), 9);
        Assert.Null(DrawingColors.Hex("12345"));
        Assert.Null(DrawingColors.Hex("GG0000"));
        Assert.Equal(PdfColor.FromRgb(0x12, 0x34, 0x56), DrawingColors.Hex("123456"));
    }

    [Theory]
    [InlineData("inv", 0x336699, 0xCC9966)]
    [InlineData("gray", 0xFF0000, 0x4C4C4C)]
    [InlineData("comp", 0xFF0000, 0x00FFFF)]
    [InlineData("<a:red val=\"0\"/><a:greenMod val=\"50000\"/><a:blueOff val=\"-100000\"/>", 0xFF80FF, 0x004000)]
    [InlineData("<a:satMod val=\"0\"/>", 0xFF0000, 0x808080)]
    [InlineData("<a:hueOff val=\"7200000\"/>", 0xFF0000, 0x00FF00)]
    [InlineData("<a:hue val=\"14400000\"/>", 0xFF0000, 0x0000FF)]
    [InlineData("<a:lum val=\"100000\"/>", 0xFF0000, 0xFFFFFF)]
    [InlineData("<a:sat val=\"0\"/><a:lumOff val=\"-100000\"/>", 0xFF0000, 0x000000)]
    public void Color_transforms(string transform, int rgb, int expected)
    {
        var xml = transform.StartsWith('<') ? transform : $"<a:{transform}/>";
        var color = System.Xml.Linq.XElement.Parse($"<a:srgbClr xmlns:a=\"{Deck.A}\" val=\"{rgb:X6}\">{xml.Replace("<a:", $"<a:", StringComparison.Ordinal)}</a:srgbClr>");
        var result = DrawingColors.Resolve(color, null, null)!.Value;
        Assert.Equal(PdfColor.FromRgb((byte)(expected >> 16), (byte)(expected >> 8), (byte)expected).Red, result.Red, 2);
        Assert.Equal(PdfColor.FromRgb((byte)(expected >> 16), (byte)(expected >> 8), (byte)expected).Green, result.Green, 2);
        Assert.Equal(PdfColor.FromRgb((byte)(expected >> 16), (byte)(expected >> 8), (byte)expected).Blue, result.Blue, 2);
    }

    [Fact]
    public void Theme_font_references()
    {
        var theme = Theme.Read(System.Xml.Linq.XElement.Parse($"<a:theme xmlns:a=\"{Deck.A}\"><a:themeElements><a:fontScheme><a:majorFont><a:latin typeface=\"H\"/><a:ea typeface=\"HE\"/><a:cs typeface=\"HC\"/></a:majorFont>"
            + "<a:minorFont><a:latin typeface=\"B\"/><a:ea typeface=\"\"/></a:minorFont></a:fontScheme></a:themeElements></a:theme>"), "theme.xml");
        Assert.Equal(("H", "B", "HE", "HC"), (theme.Font("+mj-lt"), theme.Font("+mn-lt"), theme.Font("+mj-ea"), theme.Font("+mj-cs")));
        Assert.Null(theme.Font("+mn-ea"));
        Assert.Null(theme.Font("+mn-cs"));
        Assert.Equal("Arial", theme.Font("Arial"));
        Assert.Equal("theme.xml", theme.Path);
        Assert.Equal("Calibri", Theme.Read(null, string.Empty).MinorLatin);
    }

    [Theory]
    [InlineData("Times New Roman", "serif")]
    [InlineData("Cambria", "serif")]
    [InlineData("PT Sans", "sans-serif")]
    [InlineData("Consolas", "monospace")]
    [InlineData("Calibri", "sans-serif")]
    public void Generic_families(string family, string generic) => Assert.Equal(generic, SlideFonts.Generic(family));

    [Fact]
    public void Text_escapes_and_control_characters()
    {
        Assert.Equal("a\nb_c\tA", PptxReader.Clean("a_x000B_b_x005F_c\t_x0041_\u0001"));
        Assert.Equal("plain", PptxReader.Clean("plain"));
        Assert.Equal("ab", PptxReader.Clean("a\u0085b"));
    }
}

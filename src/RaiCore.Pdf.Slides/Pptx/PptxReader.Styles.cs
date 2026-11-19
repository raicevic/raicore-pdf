using System.Xml.Linq;
using RaiCore.Pdf.Slides.Model;

namespace RaiCore.Pdf.Slides.Pptx;

/// <summary>Geometry, fills, lines and backgrounds.</summary>
internal sealed partial class PptxReader
{
    /// <summary>The dash patterns of the preset dashes, in multiples of the line width.</summary>
    private static readonly Dictionary<string, double[]> Dashes = new(StringComparer.Ordinal)
    {
        ["dash"] = [4, 3],
        ["dashDot"] = [4, 3, 1, 3],
        ["dot"] = [1, 3],
        ["lgDash"] = [8, 3],
        ["lgDashDot"] = [8, 3, 1, 3],
        ["lgDashDotDot"] = [8, 3, 1, 3, 1, 3],
        ["sysDash"] = [3, 1],
        ["sysDot"] = [1, 1],
        ["sysDashDot"] = [3, 1, 1, 1],
        ["sysDashDotDot"] = [3, 1, 1, 1, 1, 1],
    };

    /// <summary>The geometry of a shape's properties (<c>a:prstGeom</c> or <c>a:custGeom</c>), or <c>null</c> if it has none.</summary>
    internal static Model.Geometry? Geometry(XElement? spPr)
    {
        if (spPr.El("prstGeom") is { } preset)
        {
            return new PresetGeometry(preset.Attr("prst") ?? "rect", Adjustments(preset.El("avLst")));
        }

        if (spPr.El("custGeom") is { } custom)
        {
            var guides = new List<(string, string)>();
            foreach (var list in new[] { custom.El("avLst"), custom.El("gdLst") })
            {
                foreach (var gd in list.Els("gd"))
                {
                    if (gd.Attr("name") is { Length: > 0 } name)
                    {
                        guides.Add((name, gd.Attr("fmla") ?? string.Empty));
                    }
                }
            }

            var paths = new List<CustomPath>();
            foreach (var path in custom.El("pathLst").Els("path"))
            {
                var commands = new List<CustomCommand>();
                foreach (var command in path.Elements())
                {
                    var points = command.Els("pt").SelectMany(p => new[] { p.Attr("x") ?? "0", p.Attr("y") ?? "0" }).ToList();
                    switch (command.Name.LocalName)
                    {
                        case "moveTo":
                            commands.Add(new CustomCommand(CustomCommandKind.MoveTo, points));
                            break;
                        case "lnTo":
                            commands.Add(new CustomCommand(CustomCommandKind.LineTo, points));
                            break;
                        case "quadBezTo":
                            commands.Add(new CustomCommand(CustomCommandKind.QuadTo, points));
                            break;
                        case "cubicBezTo":
                            commands.Add(new CustomCommand(CustomCommandKind.CubicTo, points));
                            break;
                        case "arcTo":
                            commands.Add(new CustomCommand(CustomCommandKind.ArcTo,
                                [command.Attr("wR") ?? "0", command.Attr("hR") ?? "0", command.Attr("stAng") ?? "0", command.Attr("swAng") ?? "0"]));
                            break;
                        case "close":
                            commands.Add(new CustomCommand(CustomCommandKind.Close, []));
                            break;
                    }
                }

                paths.Add(new CustomPath(
                    Math.Max(path.Number("w") ?? 0, 0),
                    Math.Max(path.Number("h") ?? 0, 0),
                    path.Attr("fill") != "none",
                    path.Bool("stroke") != false,
                    commands));
            }

            return new CustomGeometry(guides, paths);
        }

        return null;
    }

    /// <summary>The adjust values of a preset (<c>&lt;a:gd name="adj" fmla="val 25000"/&gt;</c>).</summary>
    private static Dictionary<string, double> Adjustments(XElement? avLst)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var gd in avLst.Els("gd"))
        {
            if (gd.Attr("name") is { } name && gd.Attr("fmla") is { } formula)
            {
                values[name] = CustomGeometry.Evaluate(formula, []);
            }
        }

        return values;
    }

    /// <summary>
    /// The fill of an element's fill child (<c>a:noFill</c>, <c>a:solidFill</c>, <c>a:gradFill</c>, <c>a:blipFill</c>,
    /// <c>a:pattFill</c>, <c>a:grpFill</c>): whether it has one, and the fill (<c>null</c> for none).
    /// </summary>
    private (bool Specified, Fill? Fill) FillOf(XElement? element, Sheet sheet, PdfColor? placeholder, Fill? groupFill)
    {
        if (element is null)
        {
            return (false, null);
        }

        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "noFill":
                    return (true, null);
                case "solidFill":
                    return (true, DrawingColors.Child(child, sheet.Colors, placeholder) is { } color ? new SolidFill(color) : null);
                case "gradFill":
                    return (true, Gradient(child, sheet, placeholder));
                case "blipFill":
                {
                    // A picture recolored in two tones (theme textures) is drawn as the average of the tones.
                    if (child.Path("blip", "duotone") is { } duotone)
                    {
                        var tones = duotone.Elements().Select(e => DrawingColors.Resolve(e, sheet.Colors, placeholder)).OfType<PdfColor>().ToList();
                        if (tones.Count > 0)
                        {
                            return (true, new SolidFill(new PdfColor(tones.Average(c => c.Red), tones.Average(c => c.Green), tones.Average(c => c.Blue))));
                        }
                    }

                    var (data, source) = Media(sheet.Path, child.El("blip").RelId("embed"));
                    return (true, data is null ? null : new PictureFill(data, source ?? string.Empty));
                }

                case "pattFill":
                {
                    // A pattern is drawn as the average of its colors.
                    var fg = DrawingColors.Child(child.El("fgClr"), sheet.Colors, placeholder) ?? PdfColor.Black;
                    var bg = DrawingColors.Child(child.El("bgClr"), sheet.Colors, placeholder) ?? PdfColor.White;
                    return (true, new SolidFill(new PdfColor((fg.Red + bg.Red) / 2, (fg.Green + bg.Green) / 2, (fg.Blue + bg.Blue) / 2, (fg.Alpha + bg.Alpha) / 2)));
                }

                case "grpFill":
                    return (true, groupFill);
            }
        }

        return (false, null);
    }

    /// <summary>A gradient fill (<c>a:gradFill</c>): its stops, and its direction (linear) or kind (path).</summary>
    private static GradientFill? Gradient(XElement gradFill, Sheet sheet, PdfColor? placeholder)
    {
        var stops = new List<GradientStop>();
        foreach (var gs in gradFill.El("gsLst").Els("gs"))
        {
            if (DrawingColors.Child(gs, sheet.Colors, placeholder) is { } color)
            {
                stops.Add(new GradientStop(Math.Clamp((gs.Number("pos") ?? 0) / 100000, 0, 1), color));
            }
        }

        if (stops.Count == 0)
        {
            return null;
        }

        stops.Sort((a, b) => a.Position.CompareTo(b.Position));
        var radial = gradFill.El("path") is not null;
        var angle = (gradFill.El("lin")?.Number("ang") ?? 5400000) / 60000;
        return new GradientFill(stops, radial ? 0 : (double.IsFinite(angle) ? angle % 360 : 90), radial);
    }

    /// <summary>The fill a style's fill reference (<c>a:fillRef</c>) points to in the theme, with its color as <c>phClr</c>.</summary>
    private Fill? StyleFill(XElement? fillRef, Sheet sheet)
    {
        if (fillRef?.Number("idx") is not { } idx || idx <= 0)
        {
            return null;
        }

        var color = DrawingColors.Child(fillRef, sheet.Colors, null);
        var theme = sheet.Colors.Theme;
        var styles = idx >= 1001 ? theme.BackgroundStyles : theme.FillStyles;
        var index = (int)(idx >= 1001 ? idx - 1001 : idx - 1);
        if (index < 0 || index >= styles.Count)
        {
            // Without a format scheme, the reference's color fills the shape.
            return color is { } solid ? new SolidFill(solid) : null;
        }

        // The style's pictures belong to the theme part.
        var holder = new XElement("fill", new XElement(styles[index]));
        return FillOf(holder, theme.Path.Length > 0 ? sheet with { Path = theme.Path } : sheet, color, null).Fill;
    }

    /// <summary>The line style a style's line reference (<c>a:lnRef</c>) points to in the theme, or <c>null</c>.</summary>
    private static XElement? StyleLine(XElement? lnRef, Sheet sheet)
    {
        if (lnRef?.Number("idx") is not { } idx || idx <= 0)
        {
            return null;
        }

        var styles = sheet.Colors.Theme.LineStyles;
        var index = (int)idx - 1;
        if (index >= 0 && index < styles.Count)
        {
            return styles[index];
        }

        // Without a format scheme, a thin line in the reference's color.
        return new XElement("ln", new XAttribute("w", "9525"), new XElement("solidFill", new XElement("schemeClr", new XAttribute("val", "phClr"))));
    }

    /// <summary>
    /// A line from its layers (<c>a:ln</c> elements, lowest priority first: the style's, inherited ones, the shape's
    /// own): each property from the last layer that sets it; <c>null</c> when it has no fill.
    /// </summary>
    private static Outline? Line(IReadOnlyList<XElement?> layers, Sheet sheet, PdfColor? placeholder)
    {
        PdfColor? color = null;
        var filled = false;
        double? width = null;
        string? dash = null;
        XElement? customDash = null;
        string? cap = null;
        string? join = null;
        XElement? head = null;
        XElement? tail = null;
        foreach (var layer in layers)
        {
            if (layer is null)
            {
                continue;
            }

            foreach (var child in layer.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "noFill":
                        (filled, color) = (false, null);
                        break;
                    case "solidFill":
                        (filled, color) = (true, DrawingColors.Child(child, sheet.Colors, placeholder));
                        break;
                    case "gradFill":
                        (filled, color) = (true, Gradient(child, sheet, placeholder)?.Stops[0].Color);
                        break;
                    case "pattFill":
                        (filled, color) = (true, DrawingColors.Child(child.El("fgClr"), sheet.Colors, placeholder));
                        break;
                    case "prstDash":
                        (dash, customDash) = (child.Attr("val"), null);
                        break;
                    case "custDash":
                        (dash, customDash) = (null, child);
                        break;
                    case "round" or "bevel" or "miter":
                        join = child.Name.LocalName;
                        break;
                    case "headEnd":
                        head = child;
                        break;
                    case "tailEnd":
                        tail = child;
                        break;
                }
            }

            width = layer.Number("w") is { } w ? Math.Clamp(w, 0, 20116800) / XmlExtensions.EmuPerPoint : width;
            cap = layer.Attr("cap") ?? cap;
        }

        if (!filled || color is not { } stroke)
        {
            return null;
        }

        IReadOnlyList<double> pattern = dash is not null && Dashes.TryGetValue(dash, out var preset) ? preset : [];
        if (customDash is not null)
        {
            // Custom dashes are in 1000ths of percent of the line width.
            pattern = [.. customDash.Els("ds").SelectMany(ds => new[] { (ds.Number("d") ?? 100000) / 100000, (ds.Number("sp") ?? 100000) / 100000 })
                .Select(v => Math.Clamp(v, 0.01, 100))];
        }

        return new Outline
        {
            Width = width ?? 0.75,
            Color = stroke,
            Dash = pattern,
            Cap = cap switch { "rnd" => LineCap.Round, "sq" => LineCap.Square, _ => LineCap.Butt },
            Join = join switch { "bevel" => LineJoin.Bevel, "miter" => LineJoin.Miter, _ => LineJoin.Round },
            Head = LineEnd(head),
            Tail = LineEnd(tail),
        };
    }

    /// <summary>A line end (<c>a:headEnd</c>, <c>a:tailEnd</c>), or <c>null</c> for none.</summary>
    private static Model.LineEnd? LineEnd(XElement? end)
    {
        LineEndKind? kind = end.Attr("type") switch
        {
            "triangle" => LineEndKind.Triangle,
            "arrow" => LineEndKind.Arrow,
            "stealth" => LineEndKind.Stealth,
            "diamond" => LineEndKind.Diamond,
            "oval" => LineEndKind.Oval,
            _ => null,
        };
        static double Size(string? value) => value switch { "sm" => 2, "lg" => 5, _ => 3 };
        return kind is { } k ? new Model.LineEnd(k, Size(end.Attr("w")), Size(end.Attr("len"))) : null;
    }

    /// <summary>A background (<c>p:bg</c>): its own fill (<c>p:bgPr</c>) or a theme background style (<c>p:bgRef</c>).</summary>
    private Fill? Background(XElement? bg, string path, ColorScheme colors)
    {
        if (bg is null)
        {
            return null;
        }

        var sheet = new Sheet(SheetKind.Slide, path, colors, FallbackMaster(), null);
        if (bg.El("bgPr") is { } bgPr)
        {
            return FillOf(bgPr, sheet, null, null).Fill;
        }

        return bg.El("bgRef") is { } bgRef ? StyleFill(bgRef, sheet) : null;
    }

    /// <summary>
    /// A list style that gives every level the font and color of a style's font reference (<c>a:fontRef</c>): the text
    /// of a shape inserted with a style (white on an accent fill, in the theme's minor font).
    /// </summary>
    private static XElement FontReferenceStyle(XElement fontRef, Sheet sheet)
    {
        var defRPr = new XElement("defRPr");
        if (fontRef.Attr("idx") is "major" or "minor")
        {
            defRPr.Add(new XElement("latin", new XAttribute("typeface", fontRef.Attr("idx") == "major" ? "+mj-lt" : "+mn-lt")));
        }

        if (DrawingColors.Child(fontRef, sheet.Colors, null) is { } color)
        {
            defRPr.Add(SolidFill(color));
        }

        return new XElement("lstStyle", Enumerable.Range(1, 9).Select(i => new XElement($"lvl{i}pPr", new XElement(defRPr))));
    }

    /// <summary>A <c>solidFill</c> element of a resolved color, for synthetic style layers.</summary>
    private static XElement SolidFill(PdfColor color)
    {
        static string Hex(double v) => ((int)Math.Round(Math.Clamp(v, 0, 1) * 255)).ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
        var rgb = new XElement("srgbClr", new XAttribute("val", Hex(color.Red) + Hex(color.Green) + Hex(color.Blue)));
        if (!color.IsOpaque)
        {
            rgb.Add(new XElement("alpha", new XAttribute("val", ((int)Math.Round(color.Alpha * 100000)).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        return new XElement("solidFill", rgb);
    }
}

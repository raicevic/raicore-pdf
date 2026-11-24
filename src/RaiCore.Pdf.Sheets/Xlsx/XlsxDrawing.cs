using System.Globalization;
using System.Xml.Linq;
using RaiCore.Pdf.Charts;
using RaiCore.Pdf.DrawingML;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets.Drawings;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// Reads the drawing part of a worksheet (<c>xdr:wsDr</c>): its anchors (two-cell, one-cell, absolute) with their
/// pictures (PNG, JPEG, GIF, BMP, TIFF, WMF, EMF; cropped), shapes and text boxes (preset and free-form geometry, fill,
/// outline, text), charts (for the chart engine) and groups. Anchors are in the source's rows and columns. What cannot
/// be drawn is counted in the worksheet's <see cref="UnsupportedContent"/>: pictures that cannot be read (or have more
/// pixels than <see cref="ResourceLimits.MaxImagePixels"/>), charts the engine cannot draw (such as the newer
/// <c>chartex</c> types), SmartArt, ink and other graphic frames. Objects that do not print or are hidden are skipped.
/// </summary>
internal sealed class XlsxDrawing
{
    /// <summary>EMUs per point.</summary>
    private const double Emu = 12700;

    private readonly XlsxPackage _package;
    private readonly ResourceLimits _limits;
    private readonly UnsupportedContent _unsupported;
    private readonly Dictionary<string, PdfImage?> _images;
    private readonly Func<string, PdfColor?> _scheme;
    private readonly string? _majorFont;
    private readonly string? _minorFont;
    private string _folder = string.Empty;
    private List<(string Id, string Type, string Target)> _relationships = [];
    private long _objects;

    /// <summary>Creates a reader for the drawings of a workbook's worksheets.</summary>
    /// <param name="package">The package.</param>
    /// <param name="workbook">The workbook (its theme's colors and fonts).</param>
    /// <param name="limits">The limits: objects count against <see cref="ResourceLimits.MaxElements"/>, groups nest at most <see cref="ResourceLimits.MaxNestingDepth"/> deep.</param>
    /// <param name="unsupported">Counts what cannot be drawn.</param>
    /// <param name="images">The pictures loaded so far by part (shared by the workbook's sheets).</param>
    public XlsxDrawing(XlsxPackage package, XlsxWorkbook workbook, ResourceLimits limits, UnsupportedContent unsupported, Dictionary<string, PdfImage?> images)
    {
        _package = package;
        _limits = limits;
        _unsupported = unsupported;
        _images = images;
        _scheme = Scheme(workbook.ThemeColors);
        (_majorFont, _minorFont) = (workbook.MajorFont, workbook.MinorFont);
    }

    /// <summary>
    /// The drawings of a worksheet part (its drawing relationship), in z-order; none when it has no drawing or the
    /// drawing part is damaged (then what it holds is not known).
    /// </summary>
    public List<SheetDrawing> Read(string sheetPath)
    {
        var drawings = new List<SheetDrawing>();
        List<(string Id, string Type, string Target)> relationships;
        try
        {
            relationships = _package.Relationships(sheetPath);
        }
        catch (SpreadsheetFormatException)
        {
            return drawings;
        }

        foreach (var (_, type, target) in relationships)
        {
            if (type != "drawing")
            {
                continue;
            }

            XElement? root;
            try
            {
                root = _package.Parse(target, reader => XElement.Load(reader), null);
                _relationships = _package.Relationships(target);
            }
            catch (SpreadsheetFormatException)
            {
                continue;
            }

            if (root is null)
            {
                continue;
            }

            _folder = XlsxPackage.FolderOf(target);
            foreach (var anchor in Choices(root))
            {
                if (ReadAnchor(anchor) is { } drawing)
                {
                    drawings.Add(drawing);
                }
            }
        }

        return drawings;
    }

    /// <summary>The scheme colors of a theme by name (<c>accent1</c>, <c>tx1</c>, ...), from its colors in the order theme indexes give.</summary>
    internal static Func<string, PdfColor?> Scheme(IReadOnlyList<uint>? theme)
    {
        string[] names = ["lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];
        return name =>
        {
            name = name switch
            {
                "tx1" => "dk1",
                "bg1" => "lt1",
                "tx2" => "dk2",
                "bg2" => "lt2",
                _ => name,
            };
            var index = Array.IndexOf(names, name);
            if (theme is not null && index >= 0 && index < theme.Count)
            {
                var rgb = theme[index];
                return PdfColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            }

            return DrawingMLColors.OfficeTheme.TryGetValue(name, out var color) ? color : null;
        };
    }

    /// <summary>
    /// The children of an element with markup compatibility resolved: an <c>mc:AlternateContent</c> stands for the
    /// children of its first choice (its fallback repeats the choice for older readers).
    /// </summary>
    private static IEnumerable<XElement> Choices(XElement element)
    {
        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName == "AlternateContent" && child.Name.NamespaceName == XlsxContent.MarkupCompatibility)
            {
                if (child.Elements().FirstOrDefault(e => e.Name.LocalName == "Choice") is { } choice)
                {
                    foreach (var inner in Choices(choice))
                    {
                        yield return inner;
                    }
                }
            }
            else
            {
                yield return child;
            }
        }
    }

    private SheetDrawing? ReadAnchor(XElement anchor)
    {
        var kind = anchor.Name.LocalName switch
        {
            "twoCellAnchor" => AnchorKind.TwoCell,
            "oneCellAnchor" => AnchorKind.OneCell,
            "absoluteAnchor" => AnchorKind.Absolute,
            _ => (AnchorKind?)null,
        };
        if (kind is null || Child(anchor, "clientData") is { } data && Attr(data, "fPrintsWithSheet") is "0" or "false")
        {
            return null;
        }

        var item = Choices(anchor).Where(e => e.Name.LocalName is "pic" or "sp" or "cxnSp" or "grpSp" or "graphicFrame" or "contentPart")
            .Select(e => ReadObject(e, 0)).FirstOrDefault(i => i is not null);
        if (item is null)
        {
            return null;
        }

        var ext = Child(anchor, "ext");
        var (width, height) = (Emus(ext, "cx"), Emus(ext, "cy"));
        return kind.Value switch
        {
            AnchorKind.TwoCell => new SheetDrawing(AnchorKind.TwoCell, Edge(Child(anchor, "from"), "col"), Edge(Child(anchor, "from"), "row"), item)
            {
                Right = Edge(Child(anchor, "to"), "col"),
                Bottom = Edge(Child(anchor, "to"), "row"),
            },
            AnchorKind.OneCell => new SheetDrawing(AnchorKind.OneCell, Edge(Child(anchor, "from"), "col"), Edge(Child(anchor, "from"), "row"), item)
            {
                Width = width,
                Height = height,
            },
            _ => new SheetDrawing(AnchorKind.Absolute, new AnchorEdge(0, Emus(Child(anchor, "pos"), "x")), new AnchorEdge(0, Emus(Child(anchor, "pos"), "y")), item)
            {
                Width = width,
                Height = height,
            },
        };
    }

    /// <summary>The column or row edge of an anchor's <c>from</c> or <c>to</c>: the cell and the offset in it (EMUs).</summary>
    private static AnchorEdge Edge(XElement? marker, string axis)
    {
        var max = axis == "col" ? XlsxWorkbook.MaxColumns : XlsxWorkbook.MaxRows;
        var index = int.TryParse(Child(marker, axis)?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? Math.Clamp(i, 0, max - 1) : 0;
        var offset = double.TryParse(Child(marker, axis + "Off")?.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var o) && double.IsFinite(o)
            ? Math.Clamp(o / Emu, 0, 1e7) : 0;
        return new AnchorEdge(index, offset);
    }

    /// <summary>
    /// Reads a drawing object; <c>null</c> (and counted, unless it is hidden) when it cannot be drawn. Groups nest at
    /// most <see cref="ResourceLimits.MaxNestingDepth"/> deep and objects count against <see cref="ResourceLimits.MaxElements"/>.
    /// </summary>
    private DrawingItem? ReadObject(XElement element, int depth)
    {
        if (++_objects > _limits.MaxElements)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxElements), _limits.MaxElements,
                $"The drawings have more objects than ResourceLimits.MaxElements allows ({_limits.MaxElements}).");
        }

        if (depth > _limits.MaxNestingDepth)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), _limits.MaxNestingDepth,
                $"The drawings nest groups deeper than ResourceLimits.MaxNestingDepth allows ({_limits.MaxNestingDepth}).");
        }

        // The object's own properties: cNvPr in nvPicPr, nvSpPr, nvGrpSpPr, nvGraphicFramePr, nvCxnSpPr.
        var properties = element.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("nv", StringComparison.Ordinal));
        var info = Child(properties, "cNvPr");
        if (Attr(info, "hidden") is "1" or "true")
        {
            return null; // As Excel hides the stand-ins of controls and embedded objects.
        }

        var name = Attr(info, "name");
        switch (element.Name.LocalName)
        {
            case "pic":
                return ReadPicture(element, name);
            case "sp" or "cxnSp":
                return ReadShape(element, name);
            case "grpSp":
                return ReadGroup(element, name, depth);
            case "graphicFrame":
                return ReadFrame(element, name);
            default:
                _unsupported.Add(SheetDiagnosticKind.Shape); // Ink (contentPart).
                return null;
        }
    }

    private PictureItem? ReadPicture(XElement picture, string? name)
    {
        var blipFill = Child(picture, "blipFill");
        var blip = Child(blipFill, "blip");
        var target = Target(RelId(blip, "embed"));
        var image = target is null ? null : Image(target);
        if (image is null)
        {
            _unsupported.Add(SheetDiagnosticKind.Picture);
            return null;
        }

        var spPr = Child(picture, "spPr");
        var crop = Child(blipFill, "srcRect");
        var geometry = GeometryReader.Read(spPr);
        var line = Line(Child(spPr, "ln"), null);
        var (rotation, flipH, flipV) = Transform(Child(spPr, "xfrm"));
        return new PictureItem(image)
        {
            Name = name,
            Crop = (Percent(crop, "l"), Percent(crop, "t"), Percent(crop, "r"), Percent(crop, "b")),
            Line = line,
            Geometry = geometry is PresetGeometry { Name: "rect" } ? null : geometry,
            Rotation = rotation,
            FlipH = flipH,
            FlipV = flipV,
        };
    }

    /// <summary>A picture part loaded once; <c>null</c> when it cannot be read or is larger than the limits allow.</summary>
    private PdfImage? Image(string target)
    {
        if (_images.TryGetValue(target, out var image))
        {
            return image;
        }

        try
        {
            var data = _package.Read(target);
            image = data is null ? null : PdfImage.IsMetafile(data) ? PdfImage.LoadMetafile(data, new Metafiles.MetafileOptions()) : PdfImage.Load(data);
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException or ArgumentException or SpreadsheetFormatException
            or ResourceLimitExceededException { LimitName: nameof(ResourceLimits.MaxImagePixels) })
        {
            image = null;
        }

        _images[target] = image;
        return image;
    }

    private ShapeItem ReadShape(XElement shape, string? name)
    {
        var spPr = Child(shape, "spPr");
        var style = Child(shape, "style");
        var geometry = GeometryReader.Read(spPr) ?? Geometry.Rectangle;
        var (fillSet, fill) = Fill(spPr);
        if (!fillSet && Child(style, "fillRef") is { } fillRef && Number(fillRef, "idx") is > 0)
        {
            fill = Color(fillRef);
        }

        // Without an outline of its own, the style's: the theme's thin, medium or thick line.
        var lnRef = Child(style, "lnRef");
        var line = Child(spPr, "ln") is { } ln ? Line(ln, lnRef)
            : Number(lnRef, "idx") is { } idx and > 0 && Color(lnRef) is { } lineColor ? new ShapeLine(idx switch { 1 => 0.75, 2 => 2, _ => 3 }, lineColor, [])
            : null;

        var textColor = Color(Child(style, "fontRef")) ?? PdfColor.Black;
        var (rotation, flipH, flipV) = Transform(Child(spPr, "xfrm"));
        return new ShapeItem(geometry)
        {
            Name = name,
            Fill = fill,
            Line = line,
            Text = Text(Child(shape, "txBody"), textColor),
            Rotation = rotation,
            FlipH = flipH,
            FlipV = flipV,
        };
    }

    private GroupItem? ReadGroup(XElement group, string? name, int depth)
    {
        var xfrm = Child(Child(group, "grpSpPr"), "xfrm");
        var (rotation, flipH, flipV) = Transform(xfrm);
        var children = new List<(DrawingItem, PdfRectangle)>();
        foreach (var child in Choices(group))
        {
            if (child.Name.LocalName is not ("pic" or "sp" or "cxnSp" or "grpSp" or "graphicFrame" or "contentPart"))
            {
                continue;
            }

            if (ReadObject(child, depth + 1) is { } item)
            {
                var childXfrm = Child(child.Name.LocalName == "grpSp" ? Child(child, "grpSpPr") : child.Name.LocalName == "graphicFrame" ? child : Child(child, "spPr"), "xfrm");
                children.Add((item, Box(Child(childXfrm, "off"), "x", "y", Child(childXfrm, "ext"), "cx", "cy")));
            }
        }

        if (children.Count == 0)
        {
            return null;
        }

        // Without a child extent the children's coordinates are the group's own.
        var bounds = Child(xfrm, "chExt") is not null
            ? Box(Child(xfrm, "chOff"), "x", "y", Child(xfrm, "chExt"), "cx", "cy")
            : Box(Child(xfrm, "off"), "x", "y", Child(xfrm, "ext"), "cx", "cy");
        return new GroupItem(children, bounds) { Name = name, Rotation = rotation, FlipH = flipH, FlipV = flipV };
    }

    /// <summary>A graphic frame: a chart for the chart engine; others (SmartArt, newer chart types) are counted.</summary>
    private ChartItem? ReadFrame(XElement frame, string? name)
    {
        var data = Child(Child(frame, "graphic"), "graphicData");
        var uri = Attr(data, "uri") ?? string.Empty;
        if (!uri.EndsWith("/chart", StringComparison.OrdinalIgnoreCase))
        {
            _unsupported.Add(uri.EndsWith("/chartex", StringComparison.OrdinalIgnoreCase) ? SheetDiagnosticKind.Chart : SheetDiagnosticKind.Shape);
            return null;
        }

        Chart? chart = null;
        if (Target(RelId(Child(data, "chart"), "id")) is { } target)
        {
            try
            {
                var root = _package.Parse(target, reader => XElement.Load(reader), null);
                chart = root is null ? null : ChartReader.Read(root, _scheme, _limits);
            }
            catch (SpreadsheetFormatException)
            {
                chart = null;
            }
        }

        if (chart is null || chart.PlotArea.Groups.Count == 0)
        {
            _unsupported.Add(SheetDiagnosticKind.Chart);
            return null;
        }

        var (rotation, flipH, flipV) = Transform(Child(frame, "xfrm"));
        return new ChartItem(chart) { Name = name, Rotation = rotation, FlipH = flipH, FlipV = flipV };
    }

    /// <summary>The fill of shape properties: whether they give one, and its color (<c>null</c> for none).</summary>
    private (bool Set, PdfColor? Color) Fill(XElement? spPr)
    {
        foreach (var child in spPr?.Elements() ?? [])
        {
            switch (child.Name.LocalName)
            {
                case "noFill":
                    return (true, null);
                case "solidFill":
                    return (true, Color(child));
                case "gradFill":
                {
                    // Drawn in the average of its stops' colors.
                    var stops = Child(child, "gsLst")?.Elements().Select(Color).OfType<PdfColor>().ToList() ?? [];
                    return (true, stops.Count == 0 ? null : new PdfColor(stops.Average(c => c.Red), stops.Average(c => c.Green), stops.Average(c => c.Blue), stops.Average(c => c.Alpha)));
                }

                case "pattFill":
                    return (true, Color(Child(child, "fgClr")));
                case "blipFill" or "grpFill":
                    return (true, null);
            }
        }

        return (false, null);
    }

    /// <summary>An outline (<c>a:ln</c>), in the style's color (<paramref name="reference"/>) when it gives none; <c>null</c> for none.</summary>
    private ShapeLine? Line(XElement? ln, XElement? reference)
    {
        if (ln is null)
        {
            return null;
        }

        var width = Number(ln, "w") is { } w ? Math.Clamp(w / Emu, 0, 1000) : 0.75;
        if (Child(ln, "noFill") is not null)
        {
            return null;
        }

        var color = Child(ln, "solidFill") is { } solid ? Color(solid)
            : Child(ln, "gradFill") is { } gradient ? Child(gradient, "gsLst")?.Elements().Select(Color).OfType<PdfColor>().FirstOrDefault()
            : null;
        color ??= Color(reference);
        if (color is null)
        {
            return null;
        }

        var dash = Attr(Child(ln, "prstDash"), "val") switch
        {
            "dash" => [4, 3],
            "dashDot" => [4, 3, 1, 3],
            "dot" => [1, 3],
            "lgDash" => [8, 3],
            "lgDashDot" => [8, 3, 1, 3],
            "lgDashDotDot" => [8, 3, 1, 3, 1, 3],
            "sysDash" => [3, 1],
            "sysDot" => [1, 1],
            "sysDashDot" => [3, 1, 1, 1],
            "sysDashDotDot" => [3, 1, 1, 1, 1, 1],
            _ => Array.Empty<double>(),
        };
        return new ShapeLine(width, color.Value, dash);
    }

    /// <summary>The text of a shape (<c>xdr:txBody</c>), or <c>null</c> when it has none.</summary>
    private ShapeText? Text(XElement? body, PdfColor defaultColor)
    {
        if (body is null)
        {
            return null;
        }

        var paragraphs = new List<ShapeParagraph>();
        var hasText = false;
        foreach (var p in Children(body, "p"))
        {
            var pPr = Child(p, "pPr");
            var defaults = Child(pPr, "defRPr");
            var runs = new List<ShapeRun>();
            foreach (var part in p.Elements())
            {
                var text = part.Name.LocalName switch
                {
                    "r" or "fld" => Child(part, "t")?.Value,
                    "br" => "\n",
                    _ => null,
                };
                if (text is null)
                {
                    continue;
                }

                hasText |= text.Trim().Length > 0;
                runs.Add(Run(text, Child(part, "rPr"), defaults, defaultColor));
            }

            if (runs.Count == 0)
            {
                // An empty paragraph keeps its line, at the size its end gives.
                runs.Add(Run(string.Empty, Child(p, "endParaRPr"), defaults, defaultColor));
            }

            var alignment = Attr(pPr, "algn") switch
            {
                "ctr" => TextAlignment.Center,
                "r" => TextAlignment.Right,
                "just" or "dist" => TextAlignment.Justify,
                _ => TextAlignment.Left,
            };
            paragraphs.Add(new ShapeParagraph(runs, alignment));
        }

        if (!hasText)
        {
            return null;
        }

        var bodyPr = Child(body, "bodyPr");
        return new ShapeText(paragraphs)
        {
            Insets = (Inset(bodyPr, "lIns", 7.2), Inset(bodyPr, "tIns", 3.6), Inset(bodyPr, "rIns", 7.2), Inset(bodyPr, "bIns", 3.6)),
            Anchor = Attr(bodyPr, "anchor") switch
            {
                "ctr" => VerticalAlignment.Middle,
                "b" => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Top,
            },
            Wrap = Attr(bodyPr, "wrap") != "none",
        };
    }

    private ShapeRun Run(string text, XElement? rPr, XElement? defaults, PdfColor defaultColor)
    {
        string? Get(string attribute) => Attr(rPr, attribute) ?? Attr(defaults, attribute);
        var size = Number(rPr, "sz") ?? Number(defaults, "sz") ?? 1100;
        var font = Attr(Child(rPr, "latin"), "typeface") ?? Attr(Child(defaults, "latin"), "typeface");
        font = font switch
        {
            null or "" or "+mn-lt" or "+mn-ea" or "+mn-cs" => _minorFont,
            "+mj-lt" or "+mj-ea" or "+mj-cs" => _majorFont,
            _ => font,
        };
        var color = Color(Child(rPr, "solidFill")) ?? Color(Child(defaults, "solidFill")) ?? defaultColor;
        return new ShapeRun(text, Math.Clamp(size / 100, 1, 400), Get("b") is "1" or "true", Get("i") is "1" or "true",
            Get("u") is { } underline && underline != "none", color, font);
    }

    /// <summary>The color of an element with a color child (<c>a:solidFill</c>, a style reference), or <c>null</c>.</summary>
    private PdfColor? Color(XElement? element) => DrawingMLColors.Child(element, _scheme, null);

    /// <summary>The rotation (degrees) and flips of a transform (<c>a:xfrm</c>).</summary>
    private static (double Rotation, bool FlipH, bool FlipV) Transform(XElement? xfrm)
    {
        var rotation = Number(xfrm, "rot") is { } rot ? rot / 60000 % 360 : 0;
        return (rotation, Attr(xfrm, "flipH") is "1" or "true", Attr(xfrm, "flipV") is "1" or "true");
    }

    /// <summary>A rectangle in points from an offset and an extent in EMUs.</summary>
    private static PdfRectangle Box(XElement? offset, string x, string y, XElement? extent, string cx, string cy) =>
        new(Emus(offset, x, signed: true), Emus(offset, y, signed: true), Emus(extent, cx), Emus(extent, cy));

    /// <summary>A length attribute in EMUs as points (0 when missing; lengths are not negative unless signed).</summary>
    private static double Emus(XElement? element, string attribute, bool signed = false) =>
        Number(element, attribute) is { } emus ? Math.Clamp(emus / Emu, signed ? -1e7 : 0, 1e7) : 0;

    private static double Inset(XElement? bodyPr, string attribute, double fallback) =>
        Number(bodyPr, attribute) is { } emus ? Math.Clamp(emus / Emu, 0, 1000) : fallback;

    /// <summary>A fraction in thousandths of a percent (crop), between -1 and 1.</summary>
    private static double Percent(XElement? element, string attribute) =>
        Number(element, attribute) is { } value ? Math.Clamp(value / 100000, -1, 1) : 0;

    /// <summary>The part a relationship of the drawing part points to, or <c>null</c>.</summary>
    private string? Target(string? id) => id is null ? null : _relationships.FirstOrDefault(r => r.Id == id).Target;

    private static XElement? Child(XElement? element, string localName) =>
        element?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XElement? element, string localName) =>
        element is null ? [] : element.Elements().Where(e => e.Name.LocalName == localName);

    private static string? Attr(XElement? element, string localName) => (string?)element?.Attribute(localName);

    private static string? RelId(XElement? element, string localName) =>
        element?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName && a.Name.NamespaceName.EndsWith("relationships", StringComparison.Ordinal))?.Value;

    private static double? Number(XElement? element, string attribute) =>
        Attr(element, attribute) is { } text && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value : null;
}

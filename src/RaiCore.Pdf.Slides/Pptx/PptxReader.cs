using RaiCore.Pdf.DrawingML;
using System.Xml.Linq;
using RaiCore.Pdf.Slides.Model;

namespace RaiCore.Pdf.Slides.Pptx;

/// <summary>
/// Reads a PPTX package into a <see cref="Presentation"/>: the presentation part (slide size and order), each slide
/// with its layout, master and theme, placeholders inheriting position and text styles from the layout and master
/// (matched by index and type), the master's and layout's own shapes, and the shapes of the slide.
/// </summary>
/// <remarks>Partial: text (<c>PptxReader.Text.cs</c>), fills and lines (<c>.Styles.cs</c>), tables (<c>.Tables.cs</c>).</remarks>
internal sealed partial class PptxReader
{
    private const string TableUri = "http://schemas.openxmlformats.org/drawingml/2006/table";
    private const string ChartUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const string DiagramUri = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    private const string OleUri = "http://schemas.openxmlformats.org/presentationml/2006/ole";

    private readonly PptxPackage _package;
    private readonly Action<int, string, string>? _report;
    private readonly Action? _checkpoint;
    private readonly ResourceLimits _limits;
    private readonly Dictionary<string, MasterPart> _masters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LayoutPart> _layouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]?> _media = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(int, string)> _reported = [];
    private XElement? _defaultTextStyle;
    private XElement? _tableStyles;
    private int _slideNumber;

    private PptxReader(PptxPackage package, ResourceLimits limits, Action<int, string, string>? report, Action? checkpoint)
    {
        _package = package;
        _limits = limits;
        _report = report;
        _checkpoint = checkpoint;
    }

    /// <summary>The kind of part a shape tree belongs to.</summary>
    private enum SheetKind
    {
        Master,
        Layout,
        Slide,
    }

    /// <summary>
    /// Reads a presentation: every slide <paramref name="include"/> accepts (by number and hidden flag) is read in
    /// full; the others are left out.
    /// </summary>
    /// <param name="package">The package.</param>
    /// <param name="include">Whether a slide (number from 1, hidden) is converted.</param>
    /// <param name="limits">The limits: <see cref="ResourceLimits.MaxPages"/> bounds the slides converted, and
    /// <see cref="ResourceLimits.MaxTableCells"/> the cells of each table.</param>
    /// <param name="report">Receives what cannot be drawn: slide number (0 for the presentation), subject, message.</param>
    /// <param name="checkpoint">Called regularly; throws to stop.</param>
    /// <exception cref="PresentationFormatException">The package has no presentation part.</exception>
    public static Presentation Read(PptxPackage package, Func<int, bool, bool> include, ResourceLimits limits, Action<int, string, string>? report, Action? checkpoint) =>
        new PptxReader(package, limits, report, checkpoint).Read(include);

    private Presentation Read(Func<int, bool, bool> include)
    {
        var maxSlides = _limits.MaxPages;
        var presentationPath = _package.Target(string.Empty, "officeDocument")
            ?? (_package.Contains("ppt/presentation.xml") ? "ppt/presentation.xml" : null);
        var root = presentationPath is null ? null : _package.Xml(presentationPath);
        if (presentationPath is null || root is null || root.Name.LocalName != "presentation")
        {
            throw new PresentationFormatException("The package has no presentation part (ppt/presentation.xml).");
        }

        _defaultTextStyle = root.El("defaultTextStyle");
        if (_package.Target(presentationPath, "tableStyles") is { } tableStylesPath)
        {
            _tableStyles = _package.Xml(tableStylesPath);
        }

        // The slide size; PowerPoint's default (10 x 7.5 in) when it is missing or unusable.
        var size = root.El("sldSz");
        var width = size.Points("cx") is { } cx and > 0 and < 1e6 ? cx : 720;
        var height = size.Points("cy") is { } cy and > 0 and < 1e6 ? cy : 540;
        var (title, author) = CoreProperties();
        var presentation = new Presentation { Width = width, Height = height, Title = title, Author = author };

        var number = 0;
        var converted = 0;
        foreach (var id in root.El("sldIdLst").Els("sldId"))
        {
            if (_package.Relationship(presentationPath, id.RelId("id")) is not { External: false, Type: "slide" } relationship)
            {
                continue;
            }

            number++;
            _checkpoint?.Invoke();
            var slideRoot = _package.Xml(relationship.Target);
            if (slideRoot is null || slideRoot.Name.LocalName != "sld")
            {
                Report(number, "slide", $"The slide part '{relationship.Target}' is missing or not a slide; the slide is left out.");
                continue;
            }

            var hidden = slideRoot.Bool("show") == false;
            if (!include(number, hidden))
            {
                continue;
            }

            if (++converted > maxSlides)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxPages), maxSlides,
                    $"The presentation has more slides to convert than ResourceLimits.MaxPages allows ({maxSlides}).");
            }

            presentation.Slides.Add(ReadSlide(relationship.Target, slideRoot, number, hidden));
        }

        return presentation;
    }

    /// <summary>The title and author of the core document properties.</summary>
    private (string? Title, string? Author) CoreProperties()
    {
        var path = _package.Target(string.Empty, "core-properties") ?? "docProps/core.xml";
        var core = _package.Contains(path) ? _package.Xml(path) : null;
        static string? Text(XElement? element) => element?.Value.Trim() is { Length: > 0 } text ? text : null;
        return (Text(core.El("title")), Text(core.El("creator")));
    }

    /// <summary>Reports an issue once per slide and subject.</summary>
    private void Report(int slide, string subject, string message)
    {
        if (_reported.Add((slide, subject + "\n" + message)))
        {
            _report?.Invoke(slide, subject, message);
        }
    }

    /// <summary>Reads one slide, with its layout's and master's shapes behind its own.</summary>
    private Slide ReadSlide(string path, XElement root, int number, bool hidden)
    {
        _slideNumber = number;
        var layout = _package.Target(path, "slideLayout") is { } layoutPath ? Layout(layoutPath) : null;
        var master = layout?.Master ?? FallbackMaster();
        var map = new Dictionary<string, string>(master.ColorMap, StringComparer.Ordinal);
        foreach (var overrides in new[] { layout?.Root.Path("clrMapOvr", "overrideClrMapping"), root.Path("clrMapOvr", "overrideClrMapping") })
        {
            foreach (var (key, value) in ColorScheme.ReadMap(overrides))
            {
                map[key] = value;
            }
        }

        var colors = new ColorScheme(master.Theme, map);
        var slide = new Slide { Number = number, Hidden = hidden };
        var cSld = root.El("cSld");
        slide.Background = Background(cSld.El("bg"), path, colors)
            ?? (layout is null ? null : Background(layout.Root.Path("cSld", "bg"), layout.Path, colors))
            ?? Background(master.Root.Path("cSld", "bg"), master.Path, colors);

        // The master's own shapes (not its placeholders), unless the layout hides them; then the layout's, unless the
        // slide hides them; then the slide's.
        var showLayout = root.Bool("showMasterSp") != false;
        if (showLayout && layout is not null && layout.ShowMasterShapes)
        {
            ReadTree(master.Root.Path("cSld", "spTree"), new Sheet(SheetKind.Master, master.Path, colors, master, null), slide.Shapes, null, 0);
        }

        if (showLayout && layout is not null)
        {
            ReadTree(layout.Root.Path("cSld", "spTree"), new Sheet(SheetKind.Layout, layout.Path, colors, master, layout), slide.Shapes, null, 0);
        }

        foreach (var shape in slide.Shapes)
        {
            shape.Background = true;
        }

        ReadTree(cSld.El("spTree"), new Sheet(SheetKind.Slide, path, colors, master, layout), slide.Shapes, null, 0);
        return slide;
    }

    /// <summary>A layout (read once): its placeholders, color map override and master.</summary>
    private LayoutPart Layout(string path)
    {
        if (_layouts.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var root = _package.Xml(path);
        var master = _package.Target(path, "slideMaster") is { } masterPath ? Master(masterPath) : FallbackMaster();
        var layout = new LayoutPart(path, root ?? new XElement("sldLayout"), master);
        _layouts[path] = layout;
        return layout;
    }

    /// <summary>A master (read once): its theme, color map, text styles and placeholders.</summary>
    private MasterPart Master(string path)
    {
        if (_masters.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var root = _package.Xml(path) ?? new XElement("sldMaster");
        var theme = _package.Target(path, "theme") is { } themePath ? Theme.Read(_package.Xml(themePath), themePath) : Theme.Default;
        var master = new MasterPart(path, root, theme, ColorScheme.ReadMap(root.El("clrMap")));
        _masters[path] = master;
        return master;
    }

    /// <summary>The master of slides without one: the Office theme and no text styles.</summary>
    private MasterPart FallbackMaster() => _masters.TryGetValue(string.Empty, out var master)
        ? master
        : _masters[string.Empty] = new MasterPart(string.Empty, new XElement("sldMaster"), Theme.Default, []);

    /// <summary>
    /// Reads the shapes of a shape tree (or group) into <paramref name="shapes"/>, in order. On masters and layouts,
    /// placeholders are left out: they are templates for the slides' placeholders, not drawn themselves.
    /// </summary>
    private void ReadTree(XElement? tree, Sheet sheet, List<Shape> shapes, Fill? groupFill, int depth)
    {
        if (tree is null)
        {
            return;
        }

        foreach (var element in tree.Elements())
        {
            _checkpoint?.Invoke();
            var chosen = element;
            if (element.Name.LocalName == "AlternateContent")
            {
                // The fallback is what an application that knows none of the choices' extensions shows; a newer
                // chart (chartex) the chart engine draws is the exception.
                chosen = DrawableChartEx(element, sheet) ?? element.El("Fallback") ?? element.El("Choice");
                if (chosen is null)
                {
                    continue;
                }

                ReportChartEx(element, chosen);

                ReadTree(chosen, sheet, shapes, groupFill, depth);
                continue;
            }

            switch (chosen.Name.LocalName)
            {
                case "sp":
                    if (Sp(chosen, sheet, groupFill) is { } sp)
                    {
                        shapes.Add(sp);

                        // A SmartArt drawing's shape may lay its text out in a box of its own.
                        if (sp.Text is { } text && Box(chosen.El("txXfrm")) is { } textBox)
                        {
                            sp.Text = null;
                            shapes.Add(new AutoShape { Box = textBox, Name = sp.Name, Text = text });
                        }
                    }

                    break;
                case "cxnSp":
                    if (Sp(chosen, sheet, groupFill) is { } connector)
                    {
                        shapes.Add(connector);
                    }

                    break;
                case "pic":
                    if (Picture(chosen, sheet) is { } picture)
                    {
                        shapes.Add(picture);
                    }

                    break;
                case "grpSp":
                    if (Group(chosen, sheet, groupFill, depth) is { } group)
                    {
                        shapes.Add(group);
                    }

                    break;
                case "graphicFrame":
                    GraphicFrame(chosen, sheet, shapes, depth);
                    break;
                case "contentPart":
                    Report(_slideNumber, "ink", "Ink (content parts) is not drawn.");
                    break;
            }
        }
    }

    /// <summary>The non-visual properties of a shape: <c>cNvPr</c> and <c>nvPr</c>.</summary>
    private static (XElement? CNvPr, XElement? NvPr) NonVisual(XElement shape)
    {
        var nv = shape.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("nv", StringComparison.Ordinal));
        return (nv.El("cNvPr"), nv.El("nvPr"));
    }

    /// <summary>The placeholder element of a shape (<c>p:ph</c>), or <c>null</c>.</summary>
    private static XElement? PlaceholderOf(XElement shape) => NonVisual(shape).NvPr.El("ph");

    /// <summary>A placeholder's type; one with only an index is a content (<c>obj</c>) placeholder.</summary>
    private static string TypeOf(XElement ph) => ph.Attr("type") ?? "obj";

    /// <summary>The type of master placeholder a placeholder type inherits from.</summary>
    private static string MasterType(string type) => type switch
    {
        "title" or "ctrTitle" => "title",
        "dt" or "ftr" or "sldNum" or "hdr" => type,
        _ => "body",
    };

    /// <summary>
    /// The placeholders a placeholder inherits from: the layout's with the same index (or else type), and the master's
    /// of the matching type.
    /// </summary>
    private static (XElement? Layout, XElement? Master) Inherited(XElement ph, Sheet sheet)
    {
        var type = TypeOf(ph);
        XElement? layout = null;
        if (sheet.Kind == SheetKind.Slide && sheet.Layout is { } layoutPart)
        {
            var idx = ph.Attr("idx");
            if (idx is not null)
            {
                layout = layoutPart.Placeholders.FirstOrDefault(p => PlaceholderOf(p)?.Attr("idx") == idx);
            }

            layout ??= layoutPart.Placeholders.FirstOrDefault(p => PlaceholderOf(p) is { } other && SameType(TypeOf(other), type));
        }

        XElement? master = null;
        if (sheet.Kind != SheetKind.Master)
        {
            var masterType = MasterType(layout is not null && PlaceholderOf(layout) is { } layoutPh ? TypeOf(layoutPh) : type);
            master = sheet.Master.Placeholders.FirstOrDefault(p => PlaceholderOf(p) is { } other && TypeOf(other) == masterType)
                ?? sheet.Master.Placeholders.FirstOrDefault(p => PlaceholderOf(p) is { } other && MasterType(TypeOf(other)) == masterType);
        }

        return (layout, master);
    }

    /// <summary>Whether two placeholder types match (a title matches a centered title, content matches body).</summary>
    private static bool SameType(string a, string b) => a == b
        || (a is "title" or "ctrTitle" && b is "title" or "ctrTitle")
        || (a is "body" or "obj" && b is "body" or "obj");

    /// <summary>Reads a shape or connector (<c>p:sp</c>, <c>p:cxnSp</c>).</summary>
    private AutoShape? Sp(XElement element, Sheet sheet, Fill? groupFill)
    {
        var (cNvPr, _) = NonVisual(element);
        if (cNvPr.Bool("hidden") == true)
        {
            return null;
        }

        var ph = PlaceholderOf(element);
        if (ph is not null && sheet.Kind != SheetKind.Slide)
        {
            return null;
        }

        var (layoutPh, masterPh) = ph is null ? (null, null) : Inherited(ph, sheet);
        var spPr = element.El("spPr");
        var xfrm = spPr.El("xfrm") ?? layoutPh.Path("spPr", "xfrm") ?? masterPh.Path("spPr", "xfrm");
        if (Box(xfrm) is not { } box)
        {
            return null;
        }

        var style = element.El("style");
        var geometry = Geometry(spPr) ?? Geometry(layoutPh.El("spPr")) ?? Geometry(masterPh.El("spPr")) ?? DrawingML.Geometry.Rectangle;
        var fill = FillOf(spPr, sheet, null, groupFill);
        if (!fill.Specified && ph is not null)
        {
            fill = FillOf(layoutPh.El("spPr"), sheet, null, groupFill);
            if (!fill.Specified)
            {
                fill = FillOf(masterPh.El("spPr"), sheet, null, groupFill);
            }
        }

        if (!fill.Specified && style is not null)
        {
            fill = (true, StyleFill(style.El("fillRef"), sheet));
        }

        var line = Line([StyleLine(style.El("lnRef"), sheet), masterPh.Path("spPr", "ln"), layoutPh.Path("spPr", "ln"), spPr.El("ln")], sheet,
            DrawingColors.Child(style.El("lnRef"), sheet.Colors, null));
        var type = ph is null ? null : TypeOf(ph);
        var shape = new AutoShape
        {
            Box = box,
            Name = cNvPr.Attr("name"),
            AltText = cNvPr.Attr("descr"),
            Placeholder = type is null ? PlaceholderKind.None : type is "title" or "ctrTitle" ? PlaceholderKind.Title : PlaceholderKind.Other,
            Link = ShapeLink(cNvPr, sheet),
            Geometry = geometry,
            Fill = geometry.IsLine ? null : fill.Fill,
            Line = line,
        };

        if (geometry is PresetGeometry { IsKnown: false } preset)
        {
            Report(_slideNumber, "shape", $"The preset shape '{preset.Name}' is drawn as a rectangle.");
        }

        if (element.El("txBody") is { } txBody)
        {
            var lists = new List<XElement?> { _defaultTextStyle };
            if (type is not null)
            {
                lists.Add(type switch
                {
                    "title" or "ctrTitle" => sheet.Master.Root.Path("txStyles", "titleStyle"),
                    "dt" or "ftr" or "sldNum" or "hdr" => sheet.Master.Root.Path("txStyles", "otherStyle"),
                    _ => sheet.Master.Root.Path("txStyles", "bodyStyle"),
                });
                lists.Add(masterPh.Path("txBody", "lstStyle"));
                lists.Add(layoutPh.Path("txBody", "lstStyle"));
            }

            if (style.El("fontRef") is { } fontRef)
            {
                lists.Add(FontReferenceStyle(fontRef, sheet));
            }

            lists.Add(txBody.El("lstStyle"));
            shape.Text = TextFrame(txBody, lists, [masterPh.Path("txBody", "bodyPr"), layoutPh.Path("txBody", "bodyPr"), txBody.El("bodyPr")], sheet);
        }

        return shape;
    }

    /// <summary>The link a click on a shape opens: an external hyperlink of <c>cNvPr</c>, or <c>null</c>.</summary>
    private Uri? ShapeLink(XElement? cNvPr, Sheet sheet) => Hyperlink(cNvPr.El("hlinkClick"), sheet);

    /// <summary>An external hyperlink (<c>a:hlinkClick</c>) as an absolute URI, or <c>null</c>.</summary>
    private Uri? Hyperlink(XElement? hlinkClick, Sheet sheet)
    {
        if (_package.Relationship(sheet.Path, hlinkClick.RelId("id")) is { External: true } relationship
            && Uri.TryCreate(relationship.Target, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto" or "ftp")
        {
            return uri;
        }

        return null;
    }

    /// <summary>Reads a picture (<c>p:pic</c>).</summary>
    private PictureShape? Picture(XElement element, Sheet sheet)
    {
        var (cNvPr, _) = NonVisual(element);
        if (cNvPr.Bool("hidden") == true)
        {
            return null;
        }

        var ph = PlaceholderOf(element);
        if (ph is not null && sheet.Kind != SheetKind.Slide)
        {
            return null;
        }

        var (layoutPh, masterPh) = ph is null ? (null, null) : Inherited(ph, sheet);
        var spPr = element.El("spPr");
        if (Box(spPr.El("xfrm") ?? layoutPh.Path("spPr", "xfrm") ?? masterPh.Path("spPr", "xfrm")) is not { } box)
        {
            return null;
        }

        var blipFill = element.El("blipFill");
        var blip = blipFill.El("blip");
        var (data, source) = Media(sheet.Path, blip.RelId("embed"));
        if (data is null)
        {
            Report(_slideNumber, "picture", blip.RelId("link") is not null
                ? "A linked (external) picture is not loaded."
                : $"The picture '{cNvPr.Attr("name")}' has no image data.");
        }

        var crop = blipFill.El("srcRect") is { } rect
            ? new Crop((rect.Number("l") ?? 0) / 100000, (rect.Number("t") ?? 0) / 100000, (rect.Number("r") ?? 0) / 100000, (rect.Number("b") ?? 0) / 100000)
            : default;
        var style = element.El("style");
        return new PictureShape
        {
            Box = box,
            Name = cNvPr.Attr("name"),
            AltText = cNvPr.Attr("descr"),
            Placeholder = ph is null ? PlaceholderKind.None : PlaceholderKind.Other,
            Link = ShapeLink(cNvPr, sheet),
            Data = data,
            Source = source,
            Crop = crop,
            Geometry = Geometry(spPr) ?? DrawingML.Geometry.Rectangle,
            Fill = FillOf(spPr, sheet, null, null).Fill,
            Line = Line([StyleLine(style.El("lnRef"), sheet), spPr.El("ln")], sheet, DrawingColors.Child(style.El("lnRef"), sheet.Colors, null)),
            Opacity = blip.El("alphaModFix")?.Number("amt") is { } amount ? Math.Clamp(amount / 100000, 0, 1) : 1,
        };
    }

    /// <summary>The bytes of a media part a relationship of a part points to (read once), and its name.</summary>
    private (byte[]? Data, string? Source) Media(string partPath, string? id)
    {
        if (_package.Relationship(partPath, id) is not { External: false } relationship)
        {
            return (null, null);
        }

        if (!_media.TryGetValue(relationship.Target, out var data))
        {
            data = _package.Read(relationship.Target);
            _media[relationship.Target] = data;
        }

        return (data, relationship.Target);
    }

    /// <summary>Reads a group (<c>p:grpSp</c>) and its children.</summary>
    private GroupShape? Group(XElement element, Sheet sheet, Fill? groupFill, int depth)
    {
        var (cNvPr, _) = NonVisual(element);
        if (cNvPr.Bool("hidden") == true)
        {
            return null;
        }

        var grpSpPr = element.El("grpSpPr");
        var xfrm = grpSpPr.El("xfrm");
        var box = Box(xfrm) ?? default;
        var chOff = xfrm.El("chOff");
        var chExt = xfrm.El("chExt");
        var group = new GroupShape
        {
            Box = box,
            Name = cNvPr.Attr("name"),
            AltText = cNvPr.Attr("descr"),
            ChildX = chOff.Points("x") ?? box.X,
            ChildY = chOff.Points("y") ?? box.Y,
            ChildWidth = chExt.Points("cx") ?? box.Width,
            ChildHeight = chExt.Points("cy") ?? box.Height,
        };

        var fill = FillOf(grpSpPr, sheet, null, groupFill);
        ReadTree(element, sheet, group.Children, fill.Specified ? fill.Fill : groupFill, depth + 1);
        return group.Children.Count > 0 ? group : null;
    }

    /// <summary>Reads a graphic frame: a table, a chart, a SmartArt diagram (its drawing), an OLE object (its picture).</summary>
    private void GraphicFrame(XElement element, Sheet sheet, List<Shape> shapes, int depth)
    {
        var (cNvPr, _) = NonVisual(element);
        if (cNvPr.Bool("hidden") == true || (PlaceholderOf(element) is not null && sheet.Kind != SheetKind.Slide))
        {
            return;
        }

        var data = element.Path("graphic", "graphicData");
        var uri = data.Attr("uri");
        if (Box(element.El("xfrm")) is not { } box)
        {
            return;
        }

        switch (uri)
        {
            case TableUri when data.El("tbl") is { } tbl:
                shapes.Add(Table(tbl, box, cNvPr, sheet));
                break;
            case ChartUri or ChartExUri:
                if (ChartOf(data, box, cNvPr, sheet) is { } chart)
                {
                    shapes.Add(chart);
                }

                break;
            case DiagramUri:
                if (DiagramDrawing(data.El("relIds"), sheet) is { } drawing)
                {
                    var group = new GroupShape
                    {
                        Box = box,
                        Name = cNvPr.Attr("name"),
                        AltText = cNvPr.Attr("descr"),
                        ChildX = 0,
                        ChildY = 0,
                        ChildWidth = box.Width,
                        ChildHeight = box.Height,
                    };
                    ReadTree(drawing.Root.Path("spTree"), sheet with { Path = drawing.Path }, group.Children, null, depth + 1);
                    shapes.Add(group);
                }
                else
                {
                    Report(_slideNumber, "SmartArt", "A SmartArt diagram without a stored drawing is not drawn.");
                }

                break;
            case OleUri:
                var picture = data?.Descendants().FirstOrDefault(e => e.Name.LocalName == "pic");
                if (picture is not null && Picture(picture, sheet) is { } shape)
                {
                    shape.Box = box;
                    shapes.Add(shape);
                }
                else
                {
                    Report(_slideNumber, "OLE object", "An embedded object without a picture is not drawn.");
                }

                break;
            default:
                Report(_slideNumber, "graphic", $"A graphic frame of type '{uri}' is not drawn.");
                break;
        }
    }

    /// <summary>
    /// The drawing PowerPoint stores for a SmartArt diagram (the shapes it was last drawn as), found through the data
    /// part's <c>dataModelExt</c>; <c>null</c> when there is none.
    /// </summary>
    private (string Path, XElement Root)? DiagramDrawing(XElement? relIds, Sheet sheet)
    {
        string? drawingPath = null;
        if (_package.Relationship(sheet.Path, relIds.RelId("dm")) is { External: false } dataRelationship
            && _package.Xml(dataRelationship.Target) is { } dataModel)
        {
            var ext = dataModel.Descendants().FirstOrDefault(e => e.Name.LocalName == "dataModelExt");
            if (_package.Relationship(sheet.Path, ext.Attr("relId")) is { External: false } drawingRelationship)
            {
                drawingPath = drawingRelationship.Target;
            }
        }

        if (drawingPath is null)
        {
            var drawings = _package.Relationships(sheet.Path).Where(r => !r.External && r.Type == "diagramDrawing").ToList();
            if (drawings.Count == 1)
            {
                drawingPath = drawings[0].Target;
            }
        }

        return drawingPath is not null && _package.Xml(drawingPath) is { } root ? (drawingPath, root) : null;
    }

    /// <summary>
    /// The box of a transform (<c>a:xfrm</c> or <c>p:xfrm</c>): offset, extent, rotation and flips; <c>null</c> when
    /// it is missing or has no usable offset and extent.
    /// </summary>
    internal static ShapeBox? Box(XElement? xfrm)
    {
        if (xfrm is null)
        {
            return null;
        }

        var off = xfrm.El("off");
        var ext = xfrm.El("ext");
        if (off is null || ext is null)
        {
            return null;
        }

        var rotation = (xfrm.Number("rot") ?? 0) / 60000;
        return new ShapeBox(
            off.Points("x") ?? 0,
            off.Points("y") ?? 0,
            Math.Max(ext.Points("cx") ?? 0, 0),
            Math.Max(ext.Points("cy") ?? 0, 0),
            double.IsFinite(rotation) ? rotation % 360 : 0,
            xfrm.Bool("flipH") == true,
            xfrm.Bool("flipV") == true);
    }

    /// <summary>The part a shape tree belongs to: its kind, path (for relationships), colors, master and layout.</summary>
    private sealed record Sheet(SheetKind Kind, string Path, ColorScheme Colors, MasterPart Master, LayoutPart? Layout);

    /// <summary>A slide master: its root, theme, color map and placeholders.</summary>
    private sealed class MasterPart(string path, XElement root, Theme theme, Dictionary<string, string> colorMap)
    {
        public string Path => path;

        public XElement Root => root;

        public Theme Theme => theme;

        public Dictionary<string, string> ColorMap => colorMap;

        public List<XElement> Placeholders { get; } = [.. Shapes(root)];

        /// <summary>The placeholder shapes of a master or layout.</summary>
        public static IEnumerable<XElement> Shapes(XElement root) =>
            root.Path("cSld", "spTree")?.Elements().Where(e => e.Name.LocalName is "sp" or "pic" or "graphicFrame" && PlaceholderOf(e) is not null) ?? [];
    }

    /// <summary>A slide layout: its root, master and placeholders.</summary>
    private sealed class LayoutPart(string path, XElement root, MasterPart master)
    {
        public string Path => path;

        public XElement Root => root;

        public MasterPart Master => master;

        public bool ShowMasterShapes => root.Bool("showMasterSp") != false;

        public List<XElement> Placeholders { get; } = [.. MasterPart.Shapes(root)];
    }
}

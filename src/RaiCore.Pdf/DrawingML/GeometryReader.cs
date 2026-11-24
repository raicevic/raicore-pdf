using System.Globalization;
using System.Xml.Linq;

namespace RaiCore.Pdf.DrawingML;

/// <summary>
/// Reads the geometry of DrawingML shape properties (<c>spPr</c>): a preset (<c>a:prstGeom</c> with its adjust values)
/// or a free form (<c>a:custGeom</c>), for the converters that draw shapes (presentations, spreadsheets).
/// </summary>
internal static class GeometryReader
{
    /// <summary>The geometry of a shape's properties (<c>a:prstGeom</c> or <c>a:custGeom</c>), or <c>null</c> if it has none.</summary>
    public static Geometry? Read(XElement? spPr)
    {
        if (Child(spPr, "prstGeom") is { } preset)
        {
            return new PresetGeometry(Attr(preset, "prst") ?? "rect", Adjustments(Child(preset, "avLst")));
        }

        if (Child(spPr, "custGeom") is not { } custom)
        {
            return null;
        }

        var guides = new List<(string, string)>();
        foreach (var list in new[] { Child(custom, "avLst"), Child(custom, "gdLst") })
        {
            foreach (var gd in Children(list, "gd"))
            {
                if (Attr(gd, "name") is { Length: > 0 } name)
                {
                    guides.Add((name, Attr(gd, "fmla") ?? string.Empty));
                }
            }
        }

        var paths = new List<CustomPath>();
        foreach (var path in Children(Child(custom, "pathLst"), "path"))
        {
            var commands = new List<CustomCommand>();
            foreach (var command in path.Elements())
            {
                var points = Children(command, "pt").SelectMany(p => new[] { Attr(p, "x") ?? "0", Attr(p, "y") ?? "0" }).ToList();
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
                            [Attr(command, "wR") ?? "0", Attr(command, "hR") ?? "0", Attr(command, "stAng") ?? "0", Attr(command, "swAng") ?? "0"]));
                        break;
                    case "close":
                        commands.Add(new CustomCommand(CustomCommandKind.Close, []));
                        break;
                }
            }

            paths.Add(new CustomPath(
                Math.Max(Number(path, "w") ?? 0, 0),
                Math.Max(Number(path, "h") ?? 0, 0),
                Attr(path, "fill") != "none",
                Attr(path, "stroke") is not ("0" or "false" or "off"),
                commands));
        }

        return new CustomGeometry(guides, paths);
    }

    /// <summary>The adjust values of a preset (<c>&lt;a:gd name="adj" fmla="val 25000"/&gt;</c>).</summary>
    private static Dictionary<string, double> Adjustments(XElement? avLst)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var gd in Children(avLst, "gd"))
        {
            if (Attr(gd, "name") is { } name && Attr(gd, "fmla") is { } formula)
            {
                values[name] = CustomGeometry.Evaluate(formula, []);
            }
        }

        return values;
    }

    private static XElement? Child(XElement? element, string localName) =>
        element?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> Children(XElement? element, string localName) =>
        element is null ? [] : element.Elements().Where(e => e.Name.LocalName == localName);

    private static string? Attr(XElement element, string localName) => (string?)element.Attribute(localName);

    /// <summary>A number attribute (a percentage with a % sign in thousandths, as strict files write it), or <c>null</c>.</summary>
    private static double? Number(XElement element, string localName)
    {
        if (Attr(element, localName) is not { } text)
        {
            return null;
        }

        text = text.Trim();
        var percent = text.EndsWith('%');
        if (percent)
        {
            text = text[..^1];
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            return null;
        }

        return percent ? value * 1000 : value;
    }
}

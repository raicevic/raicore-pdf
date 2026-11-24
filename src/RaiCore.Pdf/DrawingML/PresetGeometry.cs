namespace RaiCore.Pdf.DrawingML;

/// <summary>
/// A preset shape of DrawingML (<c>prstGeom</c>) by name, with its adjust values (in the format's units: 100000ths of
/// a length, 60000ths of a degree). The common presets are drawn as their definitions describe; the others as a
/// rectangle (<see cref="IsKnown"/> tells which).
/// </summary>
internal sealed class PresetGeometry : Geometry
{
    private static readonly HashSet<string> Lines = new(StringComparer.Ordinal)
    {
        "line", "straightConnector1", "bentConnector2", "bentConnector3", "bentConnector4", "bentConnector5",
        "curvedConnector2", "curvedConnector3", "curvedConnector4", "curvedConnector5", "arc",
        "leftBracket", "rightBracket", "leftBrace", "rightBrace", "bracketPair", "bracePair",
    };

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "rect", "roundRect", "ellipse", "triangle", "rtTriangle", "diamond", "parallelogram", "trapezoid", "pentagon",
        "hexagon", "heptagon", "octagon", "decagon", "dodecagon", "star4", "star5", "star6", "star7", "star8", "star10",
        "star12", "star16", "star24", "star32", "rightArrow", "leftArrow", "upArrow", "downArrow", "leftRightArrow",
        "upDownArrow", "chevron", "homePlate", "plus", "mathPlus", "line", "straightConnector1", "bentConnector2",
        "bentConnector3", "bentConnector4", "bentConnector5", "curvedConnector2", "curvedConnector3", "curvedConnector4",
        "curvedConnector5", "flowChartProcess", "flowChartAlternateProcess", "flowChartDecision", "flowChartTerminator",
        "flowChartConnector", "flowChartInputOutput", "flowChartPredefinedProcess", "flowChartPreparation",
        "flowChartManualInput", "flowChartDocument", "flowChartOffpageConnector", "flowChartManualOperation",
        "flowChartMerge", "flowChartExtract", "snip1Rect", "snip2SameRect", "round1Rect", "round2SameRect", "can",
        "donut", "frame", "arc", "pie", "blockArc", "heart", "wedgeRectCallout", "wedgeRoundRectCallout",
        "wedgeEllipseCallout", "leftBracket", "rightBracket", "leftBrace", "rightBrace", "bracketPair", "bracePair",
        "cube", "plaque", "halfFrame", "corner", "notchedRightArrow", "stripedRightArrow", "teardrop", "moon",
    };

    private readonly IReadOnlyDictionary<string, double> _adjust;

    /// <summary>Creates a preset geometry.</summary>
    /// <param name="name">The preset's name (<c>rect</c>, <c>roundRect</c>, ...).</param>
    /// <param name="adjust">Its adjust values by name (<c>adj</c>, <c>adj1</c>, ...), or <c>null</c> for the defaults.</param>
    public PresetGeometry(string name, IReadOnlyDictionary<string, double>? adjust = null)
    {
        Name = name;
        _adjust = adjust ?? new Dictionary<string, double>();
    }

    /// <summary>The names of the presets drawn as themselves.</summary>
    internal static IReadOnlyCollection<string> KnownPresets => Known;

    /// <summary>The preset's name.</summary>
    public string Name { get; }

    /// <summary>Whether the preset is drawn as itself (not as a rectangle).</summary>
    public bool IsKnown => Known.Contains(Name);

    /// <inheritdoc/>
    public override bool IsLine => Lines.Contains(Name);

    /// <summary>An adjust value, or its default.</summary>
    private double Adj(string name, double fallback) => _adjust.TryGetValue(name, out var value) && double.IsFinite(value) ? value : fallback;

    /// <inheritdoc/>
    public override (double X, double Y, double Width, double Height) TextBounds(double width, double height)
    {
        switch (Name)
        {
            case "ellipse" or "flowChartConnector" or "wedgeEllipseCallout":
                // The rectangle inscribed in the ellipse at 45 degrees.
                var (dx, dy) = (width * 0.14645, height * 0.14645);
                return (dx, dy, width - (2 * dx), height - (2 * dy));
            case "triangle":
                return (width / 4, height / 2, width / 2, height / 2);
            case "diamond" or "flowChartDecision":
                return (width / 4, height / 4, width / 2, height / 2);
            default:
                return (0, 0, width, height);
        }
    }

    /// <inheritdoc/>
    public override IReadOnlyList<GeometryPath> Build(double w, double h)
    {
        w = Math.Max(w, 0);
        h = Math.Max(h, 0);
        var ss = Math.Min(w, h);
        var b = new PathBuilder();
        switch (Name)
        {
            case "roundRect" or "flowChartAlternateProcess":
            {
                var r = ss * Math.Clamp(Adj("adj", 16667), 0, 50000) / 100000;
                return [b.RoundedRectangle(0, 0, w, h, r, r, r, r).ToPath()];
            }

            case "ellipse" or "flowChartConnector":
                return [b.Ellipse(0, 0, w, h).ToPath()];
            case "triangle":
            {
                var x = w * Math.Clamp(Adj("adj", 50000), 0, 100000) / 100000;
                return [b.Polygon(0, h, x, 0, w, h).ToPath()];
            }

            case "rtTriangle":
                return [b.Polygon(0, h, 0, 0, w, h).ToPath()];
            case "diamond" or "flowChartDecision":
                return [b.Polygon(w / 2, 0, w, h / 2, w / 2, h, 0, h / 2).ToPath()];
            case "parallelogram" or "flowChartInputOutput":
            {
                var x = Name == "flowChartInputOutput" ? w / 5 : Math.Min(w, ss * Math.Max(Adj("adj", 25000), 0) / 100000);
                return [b.Polygon(x, 0, w, 0, w - x, h, 0, h).ToPath()];
            }

            case "trapezoid":
            {
                var x = Math.Min(w / 2, ss * Math.Max(Adj("adj", 25000), 0) / 100000);
                return [b.Polygon(0, h, x, 0, w - x, 0, w, h).ToPath()];
            }

            case "flowChartManualOperation":
                return [b.Polygon(0, 0, w, 0, w * 0.8, h, w * 0.2, h).ToPath()];
            case "flowChartMerge":
                return [b.Polygon(0, 0, w, 0, w / 2, h).ToPath()];
            case "flowChartExtract":
                return [b.Polygon(w / 2, 0, w, h, 0, h).ToPath()];
            case "pentagon":
                return [b.Polygon(w / 2, 0, w, h * 0.38197, w * 0.80902, h, w * 0.19098, h, 0, h * 0.38197).ToPath()];
            case "hexagon":
            {
                var x = Math.Min(w / 2, ss * Math.Max(Adj("adj", 25000), 0) / 100000);
                return [b.Polygon(x, 0, w - x, 0, w, h / 2, w - x, h, x, h, 0, h / 2).ToPath()];
            }

            case "flowChartPreparation":
                return [b.Polygon(w / 5, 0, w * 0.8, 0, w, h / 2, w * 0.8, h, w / 5, h, 0, h / 2).ToPath()];
            case "heptagon":
                return [RegularPolygon(b, w, h, 7).ToPath()];
            case "decagon":
                return [RegularPolygon(b, w, h, 10).ToPath()];
            case "dodecagon":
                return [RegularPolygon(b, w, h, 12).ToPath()];
            case "octagon":
            {
                var x = Math.Min(ss / 2, ss * Math.Max(Adj("adj", 29289), 0) / 100000);
                return [b.Polygon(x, 0, w - x, 0, w, x, w, h - x, w - x, h, x, h, 0, h - x, 0, x).ToPath()];
            }

            case "plaque":
            {
                var r = Math.Min(ss / 2, ss * Math.Max(Adj("adj", 16667), 0) / 100000);
                b.MoveTo(r, 0).LineTo(w - r, 0).ArcTo(r, r, 180, -90).LineTo(w, h - r).ArcTo(r, r, 270, -90)
                    .LineTo(r, h).ArcTo(r, r, 0, -90).LineTo(0, r).ArcTo(r, r, 90, -90).Close();
                return [b.ToPath()];
            }

            case "star4":
                return [Star(b, w, h, 4, Adj("adj", 12500)).ToPath()];
            case "star5":
                return [Star(b, w, h, 5, Adj("adj", 19098)).ToPath()];
            case "star6":
                return [Star(b, w, h, 6, Adj("adj", 28868)).ToPath()];
            case "star7":
                return [Star(b, w, h, 7, Adj("adj", 34601)).ToPath()];
            case "star8":
                return [Star(b, w, h, 8, Adj("adj", 38268)).ToPath()];
            case "star10":
                return [Star(b, w, h, 10, Adj("adj", 42533)).ToPath()];
            case "star12":
                return [Star(b, w, h, 12, Adj("adj", 37500)).ToPath()];
            case "star16":
                return [Star(b, w, h, 16, Adj("adj", 37500)).ToPath()];
            case "star24":
                return [Star(b, w, h, 24, Adj("adj", 37500)).ToPath()];
            case "star32":
                return [Star(b, w, h, 32, Adj("adj", 37500)).ToPath()];
            case "rightArrow" or "notchedRightArrow" or "stripedRightArrow":
            {
                var half = h * Math.Clamp(Adj("adj1", 50000), 0, 100000) / 200000;
                var x = w - Math.Min(w, ss * Math.Max(Adj("adj2", 50000), 0) / 100000);
                var notch = Name == "notchedRightArrow" ? (h / 2 - half) * (w - x) / (h / 2) : 0;
                return [b.Polygon(0, (h / 2) - half, x, (h / 2) - half, x, 0, w, h / 2, x, h, x, (h / 2) + half, 0, (h / 2) + half, notch, h / 2).ToPath()];
            }

            case "leftArrow":
            {
                var half = h * Math.Clamp(Adj("adj1", 50000), 0, 100000) / 200000;
                var x = Math.Min(w, ss * Math.Max(Adj("adj2", 50000), 0) / 100000);
                return [b.Polygon(w, (h / 2) - half, x, (h / 2) - half, x, 0, 0, h / 2, x, h, x, (h / 2) + half, w, (h / 2) + half).ToPath()];
            }

            case "upArrow":
            {
                var half = w * Math.Clamp(Adj("adj1", 50000), 0, 100000) / 200000;
                var y = Math.Min(h, ss * Math.Max(Adj("adj2", 50000), 0) / 100000);
                return [b.Polygon((w / 2) - half, h, (w / 2) - half, y, 0, y, w / 2, 0, w, y, (w / 2) + half, y, (w / 2) + half, h).ToPath()];
            }

            case "downArrow":
            {
                var half = w * Math.Clamp(Adj("adj1", 50000), 0, 100000) / 200000;
                var y = h - Math.Min(h, ss * Math.Max(Adj("adj2", 50000), 0) / 100000);
                return [b.Polygon((w / 2) - half, 0, (w / 2) - half, y, 0, y, w / 2, h, w, y, (w / 2) + half, y, (w / 2) + half, 0).ToPath()];
            }

            case "leftRightArrow":
            {
                var half = h * Math.Clamp(Adj("adj1", 50000), 0, 100000) / 200000;
                var x = Math.Min(w / 2, ss * Math.Max(Adj("adj2", 50000), 0) / 100000);
                return [b.Polygon(0, h / 2, x, 0, x, (h / 2) - half, w - x, (h / 2) - half, w - x, 0, w, h / 2, w - x, h, w - x, (h / 2) + half, x, (h / 2) + half, x, h).ToPath()];
            }

            case "upDownArrow":
            {
                var half = w * Math.Clamp(Adj("adj1", 50000), 0, 100000) / 200000;
                var y = Math.Min(h / 2, ss * Math.Max(Adj("adj2", 50000), 0) / 100000);
                return [b.Polygon(w / 2, 0, w, y, (w / 2) + half, y, (w / 2) + half, h - y, w, h - y, w / 2, h, 0, h - y, (w / 2) - half, h - y, (w / 2) - half, y, 0, y).ToPath()];
            }

            case "chevron":
            {
                var x = Math.Min(w, ss * Math.Max(Adj("adj", 50000), 0) / 100000);
                return [b.Polygon(0, 0, w - x, 0, w, h / 2, w - x, h, 0, h, x, h / 2).ToPath()];
            }

            case "homePlate":
            {
                var x = Math.Min(w, ss * Math.Max(Adj("adj", 50000), 0) / 100000);
                return [b.Polygon(0, 0, w - x, 0, w, h / 2, w - x, h, 0, h).ToPath()];
            }

            case "flowChartOffpageConnector":
                return [b.Polygon(0, 0, w, 0, w, h * 0.8, w / 2, h, 0, h * 0.8).ToPath()];
            case "plus" or "mathPlus":
            {
                var x = Name == "mathPlus" ? ss * 0.1176 : Math.Min(ss / 2, ss * Math.Max(Adj("adj", 25000), 0) / 100000);
                if (Name == "mathPlus")
                {
                    var (cx, cy) = (w / 2, h / 2);
                    var arm = ss * 0.36;
                    return [b.Polygon(cx - x, cy - arm, cx + x, cy - arm, cx + x, cy - x, cx + arm, cy - x, cx + arm, cy + x, cx + x, cy + x, cx + x, cy + arm, cx - x, cy + arm, cx - x, cy + x, cx - arm, cy + x, cx - arm, cy - x, cx - x, cy - x).ToPath()];
                }

                return [b.Polygon(x, 0, w - x, 0, w - x, x, w, x, w, h - x, w - x, h - x, w - x, h, x, h, x, h - x, 0, h - x, 0, x, x, x).ToPath()];
            }

            case "line" or "straightConnector1":
                return [b.MoveTo(0, 0).LineTo(w, h).ToPath(filled: false)];
            case "bentConnector2":
                return [b.MoveTo(0, 0).LineTo(w, 0).LineTo(w, h).ToPath(filled: false)];
            case "bentConnector3" or "bentConnector4" or "bentConnector5":
            {
                var x = w * Adj("adj1", 50000) / 100000;
                return [b.MoveTo(0, 0).LineTo(x, 0).LineTo(x, h).LineTo(w, h).ToPath(filled: false)];
            }

            case "curvedConnector2":
                return [b.MoveTo(0, 0).CubicTo(w / 2, 0, w, h / 2, w, h).ToPath(filled: false)];
            case "curvedConnector3" or "curvedConnector4" or "curvedConnector5":
            {
                var x = w * Adj("adj1", 50000) / 100000;
                return [b.MoveTo(0, 0).CubicTo(x, 0, x, h, w, h).ToPath(filled: false)];
            }

            case "flowChartTerminator":
            {
                var r = Math.Min(w / 2, h / 2);
                return [b.RoundedRectangle(0, 0, w, h, r, r, r, r).ToPath()];
            }

            case "flowChartPredefinedProcess":
                return
                [
                    b.Polygon(0, 0, w, 0, w, h, 0, h).ToPath(),
                    new PathBuilder().MoveTo(w / 8, 0).LineTo(w / 8, h).MoveTo(w * 7 / 8, 0).LineTo(w * 7 / 8, h).ToPath(filled: false),
                ];
            case "flowChartManualInput":
                return [b.Polygon(0, h / 5, w, 0, w, h, 0, h).ToPath()];
            case "flowChartDocument":
            {
                var y = h * 17322 / 21600;
                b.MoveTo(0, 0).LineTo(w, 0).LineTo(w, y)
                    .CubicTo(w / 2, y, w / 2, h * 23922 / 21600, 0, h * 20172 / 21600).Close();
                return [b.ToPath()];
            }

            case "snip1Rect":
            {
                var x = Math.Min(ss / 2, ss * Math.Max(Adj("adj", 16667), 0) / 100000);
                return [b.Polygon(0, 0, w - x, 0, w, x, w, h, 0, h).ToPath()];
            }

            case "snip2SameRect":
            {
                var x = Math.Min(ss / 2, ss * Math.Max(Adj("adj1", 16667), 0) / 100000);
                var y = Math.Min(ss / 2, ss * Math.Max(Adj("adj2", 0), 0) / 100000);
                return [b.Polygon(x, 0, w - x, 0, w, x, w, h - y, w - y, h, y, h, 0, h - y, 0, x).ToPath()];
            }

            case "round1Rect":
            {
                var r = Math.Min(ss / 2, ss * Math.Max(Adj("adj", 16667), 0) / 100000);
                return [b.RoundedRectangle(0, 0, w, h, 0, r, 0, 0).ToPath()];
            }

            case "round2SameRect":
            {
                var r = Math.Min(ss / 2, ss * Math.Max(Adj("adj1", 16667), 0) / 100000);
                var r2 = Math.Min(ss / 2, ss * Math.Max(Adj("adj2", 0), 0) / 100000);
                return [b.RoundedRectangle(0, 0, w, h, r, r, r2, r2).ToPath()];
            }

            case "can":
            {
                var ry = Math.Min(h / 2, ss * Math.Max(Adj("adj", 25000), 0) / 200000);
                b.MoveTo(0, ry).ArcTo(w / 2, ry, 180, -180).LineTo(w, h - ry).ArcTo(w / 2, ry, 0, 180).Close();
                var top = new PathBuilder().Ellipse(0, 0, w, 2 * ry);
                return [b.ToPath(), top.ToPath()];
            }

            case "cube":
            {
                var d = Math.Min(ss, ss * Math.Max(Adj("adj", 25000), 0) / 100000);
                return
                [
                    b.Polygon(0, d, w - d, d, w - d, h, 0, h).ToPath(),
                    new PathBuilder().Polygon(0, d, d, 0, w, 0, w - d, d).ToPath(),
                    new PathBuilder().Polygon(w - d, d, w, 0, w, h - d, w - d, h).ToPath(),
                ];
            }

            case "donut":
            {
                var t = Math.Min(ss / 2, ss * Math.Max(Adj("adj", 25000), 0) / 100000);
                b.Ellipse(0, 0, w, h);
                // The hole runs the other way round, so that the nonzero rule leaves it empty.
                var (rx, ry) = ((w / 2) - t, (h / 2) - t);
                if (rx > 0 && ry > 0)
                {
                    b.MoveTo(t, h / 2).ArcTo(rx, ry, 180, -360).Close();
                }

                return [b.ToPath()];
            }

            case "frame":
            {
                var t = Math.Min(ss / 2, ss * Math.Max(Adj("adj1", 12500), 0) / 100000);
                b.Polygon(0, 0, w, 0, w, h, 0, h);
                b.Polygon(t, t, t, h - t, w - t, h - t, w - t, t);
                return [b.ToPath()];
            }

            case "halfFrame":
            {
                var y = Math.Min(h, ss * Math.Max(Adj("adj1", 33333), 0) / 100000);
                var x = Math.Min(w, ss * Math.Max(Adj("adj2", 33333), 0) / 100000);
                return [b.Polygon(0, 0, w, 0, w - (w * y / Math.Max(h, 1e-9)), y, x, y, x, h - (h * x / Math.Max(w, 1e-9)), 0, h).ToPath()];
            }

            case "corner":
            {
                var y = Math.Min(h, ss * Math.Max(Adj("adj1", 50000), 0) / 100000);
                var x = Math.Min(w, ss * Math.Max(Adj("adj2", 50000), 0) / 100000);
                return [b.Polygon(0, 0, x, 0, x, h - y, w, h - y, w, h, 0, h).ToPath()];
            }

            case "arc" or "pie" or "blockArc":
            {
                var start = Adj("adj1", Name == "pie" ? 0 : 16200000) / 60000;
                var end = Adj("adj2", Name == "pie" ? 16200000 : 0) / 60000;
                var sweep = ((end - start) % 360 + 360) % 360;
                if (sweep == 0)
                {
                    sweep = 360;
                }

                var (rx, ry) = (w / 2, h / 2);
                var (sx, sy) = PointAt(rx, ry, start);
                b.MoveTo(rx + sx, ry + sy).ArcTo(rx, ry, start, sweep);
                if (Name == "arc")
                {
                    return [b.ToPath(filled: false)];
                }

                if (Name == "pie")
                {
                    return [b.LineTo(rx, ry).Close().ToPath()];
                }

                var t = Math.Min(ss / 2, ss * Math.Max(Adj("adj3", 25000), 0) / 100000);
                var (ix, iy) = PointAt(rx - t, ry - t, end);
                b.LineTo(rx + ix, ry + iy).ArcTo(rx - t, ry - t, end, -sweep).Close();
                return [b.ToPath()];
            }

            case "heart":
                b.MoveTo(w / 2, h / 4)
                    .CubicTo(w * 0.8, -h / 6, w * 1.35, h / 3, w / 2, h)
                    .CubicTo(-w * 0.35, h / 3, w * 0.2, -h / 6, w / 2, h / 4).Close();
                return [b.ToPath()];
            case "teardrop":
                b.MoveTo(0, h / 2).ArcTo(w / 2, h / 2, 180, 90).LineTo(w, 0).LineTo(w, h / 2).ArcTo(w / 2, h / 2, 0, 180).Close();
                return [b.ToPath()];

            case "moon":
            {
                var x = w * Math.Clamp(Adj("adj", 50000), 0, 87500) / 100000;
                b.MoveTo(w, 0).ArcTo(w, h / 2, 270, -180);
                if (w - x > 0)
                {
                    b.ArcTo(w - x, h / 2, 90, 180);
                }

                b.Close();
                return [b.ToPath()];
            }

            case "wedgeRectCallout" or "wedgeRoundRectCallout" or "wedgeEllipseCallout":
            {
                var px = (w / 2) + (w * Adj("adj1", -20833) / 100000);
                var py = (h / 2) + (h * Adj("adj2", 62500) / 100000);
                if (Name == "wedgeEllipseCallout")
                {
                    b.Ellipse(0, 0, w, h);
                }
                else
                {
                    var r = Name == "wedgeRoundRectCallout" ? ss * Math.Clamp(Adj("adj3", 16667), 0, 50000) / 100000 : 0;
                    b.RoundedRectangle(0, 0, w, h, r, r, r, r);
                }

                // The pointer: a triangle from the side facing the point, merged by the nonzero fill rule.
                var (bx, by, ex, ey) = Math.Abs((px - (w / 2)) / Math.Max(w, 1e-9)) > Math.Abs((py - (h / 2)) / Math.Max(h, 1e-9))
                    ? (px < w / 2 ? (w * 0.15, h * 0.4, w * 0.15, h * 0.6) : (w * 0.85, h * 0.6, w * 0.85, h * 0.4))
                    : (py < h / 2 ? (w * 0.6, h * 0.15, w * 0.4, h * 0.15) : (w * 0.4, h * 0.85, w * 0.6, h * 0.85));
                b.Polygon(bx, by, px, py, ex, ey);
                return [b.ToPath()];
            }

            case "leftBracket":
            {
                var r = Math.Min(h / 2, ss * Math.Max(Adj("adj", 8333), 0) / 100000);
                return [b.MoveTo(w, h).ArcTo(w, r, 90, 90).LineTo(0, r).ArcTo(w, r, 180, 90).ToPath(filled: false)];
            }

            case "rightBracket":
            {
                var r = Math.Min(h / 2, ss * Math.Max(Adj("adj", 8333), 0) / 100000);
                return [b.MoveTo(0, 0).ArcTo(w, r, 270, 90).LineTo(w, h - r).ArcTo(w, r, 0, 90).ToPath(filled: false)];
            }

            case "leftBrace":
            {
                var r = Math.Min(h / 4, ss * Math.Max(Adj("adj1", 8333), 0) / 100000);
                return [b.MoveTo(w, h).ArcTo(w / 2, r, 90, 90).LineTo(w / 2, (h / 2) + r).ArcTo(w / 2, r, 0, -90).ArcTo(w / 2, r, 90, -90)
                    .LineTo(w / 2, r).ArcTo(w / 2, r, 180, 90).ToPath(filled: false)];
            }

            case "rightBrace":
            {
                var r = Math.Min(h / 4, ss * Math.Max(Adj("adj1", 8333), 0) / 100000);
                return [b.MoveTo(0, 0).ArcTo(w / 2, r, 270, 90).LineTo(w / 2, (h / 2) - r).ArcTo(w / 2, r, 180, -90).ArcTo(w / 2, r, 270, -90)
                    .LineTo(w / 2, h - r).ArcTo(w / 2, r, 0, 90).ToPath(filled: false)];
            }

            case "bracketPair" or "bracePair":
            {
                var r = Math.Min(ss / 2, ss * Math.Max(Adj("adj", 16667), 0) / 100000);
                var left = new PathBuilder().MoveTo(r, h).ArcTo(r, r, 90, 90).LineTo(0, r).ArcTo(r, r, 180, 90);
                var right = new PathBuilder().MoveTo(w - r, 0).ArcTo(r, r, 270, 90).LineTo(w, h - r).ArcTo(r, r, 0, 90);
                return [left.ToPath(filled: false), right.ToPath(filled: false)];
            }

            default:
                return [b.Polygon(0, 0, w, 0, w, h, 0, h).ToPath()];
        }
    }

    /// <summary>The point at a visual angle on an ellipse, relative to its center.</summary>
    private static (double X, double Y) PointAt(double rx, double ry, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var t = Math.Atan2(rx * Math.Sin(radians), ry * Math.Cos(radians));
        return (rx * Math.Cos(t), ry * Math.Sin(t));
    }

    /// <summary>A regular polygon with a corner at the top, stretched over the box.</summary>
    private static PathBuilder RegularPolygon(PathBuilder b, double w, double h, int corners)
    {
        for (var i = 0; i < corners; i++)
        {
            var angle = (-Math.PI / 2) + (2 * Math.PI * i / corners);
            var (x, y) = ((w / 2) + (w / 2 * Math.Cos(angle)), (h / 2) + (h / 2 * Math.Sin(angle)));
            if (i == 0)
            {
                b.MoveTo(x, y);
            }
            else
            {
                b.LineTo(x, y);
            }
        }

        return b.Close();
    }

    /// <summary>A star with a point at the top; <paramref name="adjust"/> is the inner radius in 50000ths of the outer.</summary>
    private static PathBuilder Star(PathBuilder b, double w, double h, int points, double adjust)
    {
        var inner = Math.Clamp(adjust, 0, 50000) / 50000;
        for (var i = 0; i < 2 * points; i++)
        {
            var angle = (-Math.PI / 2) + (Math.PI * i / points);
            var r = i % 2 == 0 ? 1 : inner;
            var (x, y) = ((w / 2) + (w / 2 * r * Math.Cos(angle)), (h / 2) + (h / 2 * r * Math.Sin(angle)));
            if (i == 0)
            {
                b.MoveTo(x, y);
            }
            else
            {
                b.LineTo(x, y);
            }
        }

        return b.Close();
    }
}

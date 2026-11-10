namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>
/// Draws border sides in their styles, the way browsers do. Each side is a band from its outer edge inward by its
/// width; where two sides meet, the corner is split on the diagonal from the outer to the inner corner, so sides of
/// different colors or styles meet in trapezoids. Solid, double and the 3D styles are filled shapes (one path per color,
/// so sides of the same color join without seams); dashed and dotted sides are dashed strokes along the side's center
/// line, clipped to the side's band.
/// </summary>
/// <remarks>
/// Geometry that browsers snap to device pixels is reckoned in CSS pixels (0.75 pt): the thirds of a double line, and
/// the widths below which double draws solid and dots are square.
/// </remarks>
internal static class BorderPainter
{
    /// <summary>One CSS pixel in points.</summary>
    private const double Pixel = 0.75;

    /// <summary>Tolerance for comparing lengths.</summary>
    private const double Epsilon = 1e-6;

    /// <summary>Which side of a box a band is, for the shading of the 3D styles.</summary>
    internal enum Side
    {
        Top,
        Right,
        Bottom,
        Left,
    }

    /// <summary>
    /// A border side's band. Its outer edge runs from (<paramref name="X"/>, <paramref name="Y"/>) in the unit direction
    /// (<paramref name="DirectionX"/>, <paramref name="DirectionY"/>) for <paramref name="Length"/>, clockwise around the
    /// box, so the inside is to the right of the direction. The inner edge is shortened at each end by the adjacent side's
    /// width (<paramref name="StartJoin"/>, <paramref name="EndJoin"/>; 0 for none), which makes the diagonal corner joins.
    /// <paramref name="StartSquare"/>/<paramref name="EndSquare"/> mark corners shared with an identical side, where
    /// dashes and dots may cover the whole corner square instead of stopping at the diagonal.
    /// </summary>
    internal readonly record struct Edge(
        Side Side, BorderSide Border, double X, double Y, double DirectionX, double DirectionY, double Length,
        double StartJoin, double EndJoin, bool StartSquare, bool EndSquare)
    {
        /// <summary>The point <paramref name="along"/> the outer edge and <paramref name="depth"/> inward from it.</summary>
        public (double X, double Y) Point(double along, double depth) =>
            (X + (along * DirectionX) + (depth * DirectionY), Y + (along * DirectionY) - (depth * DirectionX));
    }

    /// <summary>A dash pattern for a stroke: the lengths of dashes and gaps, the phase and the line cap.</summary>
    internal readonly record struct DashStroke(double From, double To, double[] Pattern, double Phase, LineCap Cap);

    /// <summary>
    /// Draws a box's borders around the rectangle with its bottom-left corner at (<paramref name="x"/>, <paramref name="y"/>)
    /// (PDF coordinates). <paramref name="top"/>/<paramref name="bottom"/> false leave that side out, for the fragments of a
    /// box split across pages; the sides next to it then run straight to the edge.
    /// </summary>
    public static void PaintBox(PdfCanvas canvas, Borders borders, double x, double y, double width, double height, bool top = true, bool bottom = true)
    {
        var t = top && Visible(borders.Top) ? borders.Top : BorderSide.None;
        var r = Visible(borders.Right) ? borders.Right : BorderSide.None;
        var b = bottom && Visible(borders.Bottom) ? borders.Bottom : BorderSide.None;
        var l = Visible(borders.Left) ? borders.Left : BorderSide.None;
        var (x1, y1) = (x + width, y + height);

        var edges = new List<Edge>(4);
        void Add(Side side, BorderSide border, double ex, double ey, double dx, double dy, double length, BorderSide before, BorderSide after)
        {
            if (border.Width > 0)
            {
                edges.Add(new Edge(side, border, ex, ey, dx, dy, length, before.Width, after.Width,
                    before.Width == 0 || before == border, after.Width == 0 || after == border));
            }
        }

        Add(Side.Top, t, x, y1, 1, 0, width, l, r);
        Add(Side.Right, r, x1, y1, 0, -1, height, t, b);
        Add(Side.Bottom, b, x1, y, -1, 0, width, r, l);
        Add(Side.Left, l, x, y, 0, 1, height, b, t);
        Paint(canvas, edges);
    }

    /// <summary>
    /// A horizontal grid line of the collapsing border model, centered on <paramref name="y"/> from <paramref name="x0"/> to
    /// <paramref name="x1"/>. Shaded as a top side; inset and outset draw as ridge and groove, as CSS specifies for collapsed borders.
    /// </summary>
    public static Edge HorizontalLine(GridLine line, double x0, double x1, double y) =>
        new(Side.Top, Collapsed(line), x0, y + (line.Width / 2), 1, 0, x1 - x0, 0, 0, true, true);

    /// <summary>A vertical grid line centered on <paramref name="x"/> from <paramref name="y0"/> up to <paramref name="y1"/>, shaded as a left side.</summary>
    public static Edge VerticalLine(GridLine line, double x, double y0, double y1) =>
        new(Side.Left, Collapsed(line), x - (line.Width / 2), y0, 0, 1, y1 - y0, 0, 0, true, true);

    /// <summary>Draws border bands: the filled styles first, grouped by color, then the dashed and dotted strokes.</summary>
    public static void Paint(PdfCanvas canvas, IReadOnlyList<Edge> edges)
    {
        var fills = new List<(PdfColor Color, Edge Edge, double From, double To)>();
        var strokes = new List<Edge>();
        foreach (var edge in edges)
        {
            var (width, color) = (edge.Border.Width, edge.Border.Color);
            if (width <= 0 || edge.Length <= 0)
            {
                continue;
            }

            switch (edge.Border.Style)
            {
                case BorderStyle.Dashed or BorderStyle.Dotted:
                    strokes.Add(edge);
                    break;
                case BorderStyle.Double when DoubleLine(width) is { } line:
                    fills.Add((color, edge, 0, line));
                    fills.Add((color, edge, width - line, width));
                    break;
                case BorderStyle.Inset or BorderStyle.Outset:
                    fills.Add((Shade(color, edge.Border.Style, edge.Side, outer: true), edge, 0, width));
                    break;
                case BorderStyle.Groove or BorderStyle.Ridge when width < (2 * Pixel) - Epsilon:
                    fills.Add((Shade(color, edge.Border.Style, edge.Side, outer: true), edge, 0, width)); // Too thin for two halves.
                    break;
                case BorderStyle.Groove or BorderStyle.Ridge:
                    fills.Add((Shade(color, edge.Border.Style, edge.Side, outer: true), edge, 0, width / 2));
                    fills.Add((Shade(color, edge.Border.Style, edge.Side, outer: false), edge, width / 2, width));
                    break;
                case BorderStyle.Hidden:
                    break;
                default: // Solid, and double lines too thin to split.
                    fills.Add((color, edge, 0, width));
                    break;
            }
        }

        foreach (var group in fills.GroupBy(f => f.Color))
        {
            canvas.SetFillColor(group.Key);
            foreach (var (_, edge, from, to) in group)
            {
                AddBand(canvas, edge, from, to);
            }

            canvas.Fill();
        }

        foreach (var edge in strokes)
        {
            StrokeDashes(canvas, edge);
        }
    }

    /// <summary>The width of each of a double line's two lines (a third of the width in whole pixels), or <c>null</c> to draw it solid.</summary>
    internal static double? DoubleLine(double width)
    {
        var pixels = width / Pixel;
        if (pixels < 3 - Epsilon)
        {
            return null;
        }

        return Math.Max(1, Math.Round(pixels / 3, MidpointRounding.AwayFromZero)) * Pixel;
    }

    /// <summary>
    /// The color of a 3D-style band. Inset darkens the top and left sides and outset the bottom and right; groove is
    /// inset outside and outset inside, ridge the reverse. As in Chrome, the darker shade takes a third of full
    /// brightness off the color (each channel scaled by (max - 1/3) / max, max being the brightest channel) and the
    /// lighter shade is the color itself; colors too dark to darken (brightest channel below 1/3, such as black) keep
    /// the color as the darker shade and move a third of the way to white for the lighter one.
    /// </summary>
    internal static PdfColor Shade(PdfColor color, BorderStyle style, Side side, bool outer)
    {
        var effective = style switch
        {
            BorderStyle.Groove => outer ? BorderStyle.Inset : BorderStyle.Outset,
            BorderStyle.Ridge => outer ? BorderStyle.Outset : BorderStyle.Inset,
            _ => style,
        };
        var darker = (side is Side.Top or Side.Left) == (effective == BorderStyle.Inset);
        var max = Math.Max(color.Red, Math.Max(color.Green, color.Blue));
        if (max < 1.0 / 3)
        {
            return darker ? color : new PdfColor(Lighten(color.Red), Lighten(color.Green), Lighten(color.Blue));
        }

        var scale = (max - (1.0 / 3)) / max;
        return darker ? new PdfColor(color.Red * scale, color.Green * scale, color.Blue * scale) : color;

        static double Lighten(double channel) => channel + ((1 - channel) / 3);
    }

    /// <summary>
    /// How to stroke a dashed or dotted band along its center line. Dashes are three times the width long and spaced
    /// about as far apart, the gap adjusted so the side starts and ends with a whole dash that covers the corner. Dots
    /// are as wide as the line, about a dot apart: round (zero-length dashes with round caps) from the center of one
    /// corner to the other, or square below 3 px, laid out like dashes. A side too short for two whole dashes gets
    /// three equal parts (dash, gap, dash); <c>null</c> when even those would be shorter than wide: it is drawn solid.
    /// </summary>
    internal static DashStroke? Dashes(Edge edge)
    {
        var width = edge.Border.Width;
        if (edge.Border.Style == BorderStyle.Dotted && width >= (3 * Pixel) - Epsilon)
        {
            // Round dots centered from corner to corner: the corner's center when a side meets there, else a dot's radius in.
            var from = (edge.StartJoin > 0 ? edge.StartJoin : width) / 2;
            var to = edge.Length - ((edge.EndJoin > 0 ? edge.EndJoin : width) / 2);
            var span = to - from;
            if (span < width)
            {
                var middle = (from + to) / 2;
                return new DashStroke(middle - width, middle + width, [0, 2 * width], width, LineCap.Round);
            }

            // The path runs half a spacing past the end dots, and the phase puts the first dot half a spacing in, so
            // rounding cannot drop the last dot.
            var spacing = span / Math.Max(1, Math.Round(span / (2 * width)));
            return new DashStroke(from - (spacing / 2), to + (spacing / 2), [0, spacing], spacing / 2, LineCap.Round);
        }

        var dash = edge.Border.Style == BorderStyle.Dotted ? width : 3 * width;
        if (Gap(edge.Length, dash) is { } gap)
        {
            return new DashStroke(0, edge.Length, [dash, gap], 0, LineCap.Butt);
        }

        // Too short for two whole dashes: shorter dashes (dash, gap, dash) while they are still at least square.
        var third = edge.Length / 3;
        return third >= width - Epsilon ? new DashStroke(0, edge.Length, [third, third], 0, LineCap.Butt) : null;
    }

    /// <summary>
    /// The gap that fits whole dashes of length <paramref name="dash"/> into <paramref name="length"/>, starting and
    /// ending with a dash, closest to the dash length (as Chrome does); <c>null</c> when not even two dashes fit.
    /// </summary>
    internal static double? Gap(double length, double dash)
    {
        var ideal = (length + dash) / (2 * dash); // The number of dashes for gap = dash.
        double? best = null;
        foreach (var count in new[] { Math.Floor(ideal), Math.Ceiling(ideal) })
        {
            if (count < 2)
            {
                continue;
            }

            var gap = (length - (count * dash)) / (count - 1);
            if (gap > Epsilon && (best is null || Math.Abs(gap - dash) < Math.Abs(best.Value - dash)))
            {
                best = gap;
            }
        }

        return best;
    }

    private static bool Visible(BorderSide side) => side.Width > 0 && side.Style != BorderStyle.Hidden;

    /// <summary>Collapsed borders have no inside or outside: inset draws as ridge and outset as groove.</summary>
    private static BorderSide Collapsed(GridLine line) => new(line.Width, line.Color, line.Style switch
    {
        BorderStyle.Inset => BorderStyle.Ridge,
        BorderStyle.Outset => BorderStyle.Groove,
        _ => line.Style,
    });

    /// <summary>Adds the part of a band between two depths as a closed quadrilateral, cut on the corner diagonals.</summary>
    private static void AddBand(PdfCanvas canvas, Edge edge, double from, double to, bool squareStart = false, bool squareEnd = false)
    {
        // Joins wider than the side is long (a tiny box with thick borders) would cross; scale them down.
        var startJoin = squareStart ? 0 : edge.StartJoin;
        var endJoin = squareEnd ? 0 : edge.EndJoin;
        var scale = startJoin + endJoin > edge.Length ? edge.Length / (startJoin + endJoin) : 1;
        var width = edge.Border.Width;
        double Start(double depth) => startJoin * scale * depth / width;
        double End(double depth) => edge.Length - (endJoin * scale * depth / width);

        var (ax, ay) = edge.Point(Start(from), from);
        var (bx, by) = edge.Point(End(from), from);
        var (cx, cy) = edge.Point(End(to), to);
        var (dx, dy) = edge.Point(Start(to), to);
        canvas.MoveTo(ax, ay).LineTo(bx, by).LineTo(cx, cy).LineTo(dx, dy).ClosePath();
    }

    /// <summary>Strokes a dashed or dotted band, clipped to its share of the corners.</summary>
    private static void StrokeDashes(PdfCanvas canvas, Edge edge)
    {
        var width = edge.Border.Width;
        canvas.SaveState();
        AddBand(canvas, edge, 0, width, edge.StartSquare, edge.EndSquare);
        canvas.Clip();
        canvas.SetStrokeColor(edge.Border.Color).SetLineWidth(width);
        var stroke = Dashes(edge) ?? new DashStroke(0, edge.Length, [], 0, LineCap.Butt);
        if (stroke.Pattern.Length > 0)
        {
            canvas.SetDash(stroke.Pattern, stroke.Phase);
        }

        if (stroke.Cap != LineCap.Butt)
        {
            canvas.SetLineCap(stroke.Cap);
        }

        var (x0, y0) = edge.Point(stroke.From, width / 2);
        var (x1, y1) = edge.Point(stroke.To, width / 2);
        canvas.MoveTo(x0, y0).LineTo(x1, y1).Stroke();
        canvas.RestoreState();
    }
}

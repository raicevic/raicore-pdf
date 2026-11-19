namespace RaiCore.Pdf.Slides.Model;

/// <summary>The kinds of path segment.</summary>
internal enum SegmentKind
{
    /// <summary>Starts a subpath at (X, Y).</summary>
    Move,

    /// <summary>A straight line to (X, Y).</summary>
    Line,

    /// <summary>A cubic Bezier curve to (X, Y) through the control points (X1, Y1) and (X2, Y2).</summary>
    Cubic,

    /// <summary>Closes the subpath.</summary>
    Close,
}

/// <summary>A path segment in the shape's own coordinates (points, y downwards from its box's top-left corner).</summary>
internal readonly record struct Segment(SegmentKind Kind, double X, double Y, double X1 = 0, double Y1 = 0, double X2 = 0, double Y2 = 0);

/// <summary>A path of a geometry, and whether it is filled and stroked.</summary>
/// <param name="Segments">The segments.</param>
/// <param name="Filled">Whether the path is filled.</param>
/// <param name="Stroked">Whether the path is stroked.</param>
internal sealed record GeometryPath(IReadOnlyList<Segment> Segments, bool Filled, bool Stroked)
{
    /// <summary>Whether the path is open (its last subpath is not closed): line ends are drawn on open paths.</summary>
    public bool IsOpen => Segments.Count > 0 && Segments[^1].Kind != SegmentKind.Close;
}

/// <summary>
/// The outline of a shape, built for a size: a preset shape (<see cref="PresetGeometry"/>) or a free form
/// (<see cref="CustomGeometry"/>).
/// </summary>
internal abstract class Geometry
{
    /// <summary>A rectangle.</summary>
    public static Geometry Rectangle { get; } = new PresetGeometry("rect");

    /// <summary>Builds the paths for a box of a size.</summary>
    public abstract IReadOnlyList<GeometryPath> Build(double width, double height);

    /// <summary>The rectangle text is laid out in, for a box of a size (the whole box unless the shape says otherwise).</summary>
    public virtual (double X, double Y, double Width, double Height) TextBounds(double width, double height) => (0, 0, width, height);

    /// <summary>Whether the geometry is a line (a connector): its paths are not filled.</summary>
    public virtual bool IsLine => false;
}

/// <summary>Builds path segments, with arcs as Bezier curves.</summary>
internal sealed class PathBuilder
{
    /// <summary>The control point distance of a quarter circle as a fraction of the radius.</summary>
    private const double Kappa = 0.5522847498307936;

    private readonly List<Segment> _segments = [];
    private double _x;
    private double _y;
    private double _startX;
    private double _startY;

    /// <summary>The segments so far.</summary>
    public List<Segment> Segments => _segments;

    /// <summary>Starts a subpath.</summary>
    public PathBuilder MoveTo(double x, double y)
    {
        _segments.Add(new Segment(SegmentKind.Move, x, y));
        (_x, _y, _startX, _startY) = (x, y, x, y);
        return this;
    }

    /// <summary>A line to a point.</summary>
    public PathBuilder LineTo(double x, double y)
    {
        _segments.Add(new Segment(SegmentKind.Line, x, y));
        (_x, _y) = (x, y);
        return this;
    }

    /// <summary>A cubic curve.</summary>
    public PathBuilder CubicTo(double x1, double y1, double x2, double y2, double x, double y)
    {
        _segments.Add(new Segment(SegmentKind.Cubic, x, y, x1, y1, x2, y2));
        (_x, _y) = (x, y);
        return this;
    }

    /// <summary>A quadratic curve, as a cubic one.</summary>
    public PathBuilder QuadTo(double x1, double y1, double x, double y) =>
        CubicTo(_x + (2.0 / 3 * (x1 - _x)), _y + (2.0 / 3 * (y1 - _y)), x + (2.0 / 3 * (x1 - x)), y + (2.0 / 3 * (y1 - y)), x, y);

    /// <summary>
    /// An arc of an ellipse with radii <paramref name="rx"/> and <paramref name="ry"/>, from the current point, which is
    /// at angle <paramref name="start"/> on it, sweeping <paramref name="sweep"/> degrees (clockwise when positive, y
    /// pointing down), as DrawingML's <c>arcTo</c>. The angles are visual angles, as DrawingML measures them.
    /// </summary>
    public PathBuilder ArcTo(double rx, double ry, double start, double sweep)
    {
        if (rx <= 0 || ry <= 0 || !double.IsFinite(rx + ry + start + sweep))
        {
            return this;
        }

        var t0 = Parametric(start, rx, ry);
        var t1 = Parametric(start + sweep, rx, ry);
        if (sweep > 0)
        {
            while (t1 < t0)
            {
                t1 += 2 * Math.PI;
            }

            // A full turn (or more) of visual angle is a full turn of the parametric one too.
            if (sweep >= 360)
            {
                t1 = t0 + (2 * Math.PI);
            }
        }
        else
        {
            while (t1 > t0)
            {
                t1 -= 2 * Math.PI;
            }

            if (sweep <= -360)
            {
                t1 = t0 - (2 * Math.PI);
            }
        }

        var cx = _x - (rx * Math.Cos(t0));
        var cy = _y - (ry * Math.Sin(t0));
        var parts = Math.Max(1, (int)Math.Ceiling(Math.Abs(t1 - t0) / (Math.PI / 2) - 1e-9));
        var step = (t1 - t0) / parts;
        var k = 4.0 / 3 * Math.Tan(step / 4);
        for (var i = 0; i < parts; i++)
        {
            var a0 = t0 + (i * step);
            var a1 = a0 + step;
            var (c0, s0, c1, s1) = (Math.Cos(a0), Math.Sin(a0), Math.Cos(a1), Math.Sin(a1));
            CubicTo(
                cx + (rx * (c0 - (k * s0))), cy + (ry * (s0 + (k * c0))),
                cx + (rx * (c1 + (k * s1))), cy + (ry * (s1 - (k * c1))),
                cx + (rx * c1), cy + (ry * s1));
        }

        return this;
    }

    /// <summary>The parametric angle (radians) of the point at a visual angle (degrees) on an ellipse.</summary>
    private static double Parametric(double degrees, double rx, double ry)
    {
        var radians = degrees * Math.PI / 180;
        return Math.Atan2(rx * Math.Sin(radians), ry * Math.Cos(radians));
    }

    /// <summary>Closes the subpath.</summary>
    public PathBuilder Close()
    {
        if (_segments.Count > 0 && _segments[^1].Kind != SegmentKind.Close)
        {
            _segments.Add(new Segment(SegmentKind.Close, _startX, _startY));
            (_x, _y) = (_startX, _startY);
        }

        return this;
    }

    /// <summary>A closed polygon through points (x0, y0, x1, y1, ...).</summary>
    public PathBuilder Polygon(params ReadOnlySpan<double> points)
    {
        MoveTo(points[0], points[1]);
        for (var i = 2; i + 1 < points.Length; i += 2)
        {
            LineTo(points[i], points[i + 1]);
        }

        return Close();
    }

    /// <summary>A closed ellipse in a box, clockwise from its left point.</summary>
    public PathBuilder Ellipse(double x, double y, double width, double height)
    {
        var (rx, ry) = (width / 2, height / 2);
        var (cx, cy) = (x + rx, y + ry);
        var (kx, ky) = (rx * Kappa, ry * Kappa);
        MoveTo(x, cy);
        CubicTo(x, cy - ky, cx - kx, y, cx, y);
        CubicTo(cx + kx, y, x + width, cy - ky, x + width, cy);
        CubicTo(x + width, cy + ky, cx + kx, y + height, cx, y + height);
        CubicTo(cx - kx, y + height, x, cy + ky, x, cy);
        return Close();
    }

    /// <summary>A rectangle with rounded corners (radii per corner: top-left, top-right, bottom-right, bottom-left).</summary>
    public PathBuilder RoundedRectangle(double x, double y, double width, double height, double tl, double tr, double br, double bl)
    {
        MoveTo(x + tl, y);
        LineTo(x + width - tr, y);
        if (tr > 0)
        {
            ArcTo(tr, tr, 270, 90);
        }

        LineTo(x + width, y + height - br);
        if (br > 0)
        {
            ArcTo(br, br, 0, 90);
        }

        LineTo(x + bl, y + height);
        if (bl > 0)
        {
            ArcTo(bl, bl, 90, 90);
        }

        LineTo(x, y + tl);
        if (tl > 0)
        {
            ArcTo(tl, tl, 180, 90);
        }

        return Close();
    }

    /// <summary>The path built so far, filled and stroked as given.</summary>
    public GeometryPath ToPath(bool filled = true, bool stroked = true) => new([.. _segments], filled, stroked);
}

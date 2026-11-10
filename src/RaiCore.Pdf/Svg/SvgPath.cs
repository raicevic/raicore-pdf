namespace RaiCore.Pdf.Svg;

internal enum PathOp : byte
{
    MoveTo,
    LineTo,
    CubicTo,
    Close,
}

/// <summary>A path segment in absolute coordinates; MoveTo and LineTo only use (X, Y).</summary>
internal readonly record struct PathSegment(PathOp Op, double X1, double Y1, double X2, double Y2, double X, double Y);

/// <summary>An axis-aligned rectangle.</summary>
internal readonly record struct SvgBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;

    public double Height => MaxY - MinY;

    public SvgBounds Union(SvgBounds other) =>
        new(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY), Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));

    /// <summary>The bounds of this rectangle after a transformation.</summary>
    public SvgBounds Transform(SvgMatrix matrix)
    {
        var (x1, y1) = matrix.Apply(MinX, MinY);
        var (x2, y2) = matrix.Apply(MaxX, MinY);
        var (x3, y3) = matrix.Apply(MinX, MaxY);
        var (x4, y4) = matrix.Apply(MaxX, MaxY);
        return new(Math.Min(Math.Min(x1, x2), Math.Min(x3, x4)), Math.Min(Math.Min(y1, y2), Math.Min(y3, y4)),
            Math.Max(Math.Max(x1, x2), Math.Max(x3, x4)), Math.Max(Math.Max(y1, y2), Math.Max(y3, y4)));
    }
}

/// <summary>
/// A path made of lines and cubic Béziers in absolute coordinates. Quadratic curves and elliptical arcs are converted
/// to cubics when added, so the path maps directly to PDF path operators.
/// </summary>
internal sealed class SvgPath
{
    /// <summary>The magic number for approximating a quarter circle with a cubic Bézier.</summary>
    private const double Kappa = 0.5522847498307936;

    private readonly List<PathSegment> _segments = [];
    private double _startX;
    private double _startY;
    private bool _open;

    public IReadOnlyList<PathSegment> Segments => _segments;

    /// <summary>Whether the path draws anything (it has a segment other than MoveTo).</summary>
    public bool HasDrawing => _segments.Any(s => s.Op != PathOp.MoveTo);

    public double CurrentX { get; private set; }

    public double CurrentY { get; private set; }

    public void MoveTo(double x, double y)
    {
        _segments.Add(new PathSegment(PathOp.MoveTo, 0, 0, 0, 0, x, y));
        (_startX, _startY, CurrentX, CurrentY) = (x, y, x, y);
        _open = true;
    }

    public void LineTo(double x, double y)
    {
        EnsureOpen();
        _segments.Add(new PathSegment(PathOp.LineTo, 0, 0, 0, 0, x, y));
        (CurrentX, CurrentY) = (x, y);
    }

    public void CubicTo(double x1, double y1, double x2, double y2, double x, double y)
    {
        EnsureOpen();
        _segments.Add(new PathSegment(PathOp.CubicTo, x1, y1, x2, y2, x, y));
        (CurrentX, CurrentY) = (x, y);
    }

    /// <summary>Adds a quadratic Bézier, raised to the equivalent cubic.</summary>
    public void QuadTo(double x1, double y1, double x, double y)
    {
        var (x0, y0) = (CurrentX, CurrentY);
        CubicTo(x0 + (2.0 / 3 * (x1 - x0)), y0 + (2.0 / 3 * (y1 - y0)), x + (2.0 / 3 * (x1 - x)), y + (2.0 / 3 * (y1 - y)), x, y);
    }

    public void Close()
    {
        if (_open)
        {
            _segments.Add(new PathSegment(PathOp.Close, 0, 0, 0, 0, _startX, _startY));
            (CurrentX, CurrentY) = (_startX, _startY);
            _open = false;
        }
    }

    /// <summary>
    /// Adds an SVG elliptical arc from the current point to (<paramref name="x"/>, <paramref name="y"/>) as cubic
    /// Béziers of at most 90° each (SVG 1.1 implementation notes F.6.5 and F.6.6: out-of-range radii are scaled up).
    /// </summary>
    public void ArcTo(double rx, double ry, double rotation, bool largeArc, bool sweep, double x, double y)
    {
        var (x0, y0) = (CurrentX, CurrentY);
        if (x0 == x && y0 == y)
        {
            return; // Identical endpoints: the arc is omitted.
        }

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0 || ry == 0)
        {
            LineTo(x, y);
            return;
        }

        var (sin, cos) = Math.SinCos(rotation * Math.PI / 180);
        var dx2 = (x0 - x) / 2;
        var dy2 = (y0 - y) / 2;
        var x1p = (cos * dx2) + (sin * dy2);
        var y1p = (-sin * dx2) + (cos * dy2);

        var lambda = (x1p * x1p / (rx * rx)) + (y1p * y1p / (ry * ry));
        if (lambda > 1)
        {
            var scale = Math.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        var rx2 = rx * rx;
        var ry2 = ry * ry;
        var numerator = (rx2 * ry2) - (rx2 * y1p * y1p) - (ry2 * x1p * x1p);
        var denominator = (rx2 * y1p * y1p) + (ry2 * x1p * x1p);
        var coefficient = Math.Sqrt(Math.Max(0, numerator / denominator)) * (largeArc == sweep ? -1 : 1);
        var cxp = coefficient * rx * y1p / ry;
        var cyp = coefficient * -ry * x1p / rx;
        var cx = (cos * cxp) - (sin * cyp) + ((x0 + x) / 2);
        var cy = (sin * cxp) + (cos * cyp) + ((y0 + y) / 2);

        var ux = (x1p - cxp) / rx;
        var uy = (y1p - cyp) / ry;
        var vx = (-x1p - cxp) / rx;
        var vy = (-y1p - cyp) / ry;
        var theta = Math.Atan2(uy, ux);
        var delta = Math.Atan2((ux * vy) - (uy * vx), (ux * vx) + (uy * vy));
        if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }
        else if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }

        var count = Math.Max(1, (int)Math.Ceiling((Math.Abs(delta) / (Math.PI / 2)) - 1e-9));
        var step = delta / count;
        var k = 4.0 / 3 * Math.Tan(step / 4);

        // Maps a point of the unit circle to the ellipse.
        (double X, double Y) Map(double px, double py) =>
            (cx + (rx * px * cos) - (ry * py * sin), cy + (rx * px * sin) + (ry * py * cos));

        for (var i = 0; i < count; i++)
        {
            var t1 = theta + (i * step);
            var t2 = t1 + step;
            var (s1, c1) = Math.SinCos(t1);
            var (s2, c2) = Math.SinCos(t2);
            var p1 = Map(c1 - (k * s1), s1 + (k * c1));
            var p2 = Map(c2 + (k * s2), s2 - (k * c2));
            var p3 = i == count - 1 ? (x, y) : Map(c2, s2);
            CubicTo(p1.X, p1.Y, p2.X, p2.Y, p3.Item1, p3.Item2);
        }
    }

    /// <summary>Adds a closed ellipse, starting at its rightmost point.</summary>
    public void Ellipse(double cx, double cy, double rx, double ry)
    {
        var (kx, ky) = (rx * Kappa, ry * Kappa);
        MoveTo(cx + rx, cy);
        CubicTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry);
        CubicTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy);
        CubicTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry);
        CubicTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy);
        Close();
    }

    /// <summary>Adds a closed rectangle, with rounded corners when both radii are positive.</summary>
    public void Rectangle(double x, double y, double width, double height, double rx, double ry)
    {
        if (rx <= 0 || ry <= 0)
        {
            MoveTo(x, y);
            LineTo(x + width, y);
            LineTo(x + width, y + height);
            LineTo(x, y + height);
            Close();
            return;
        }

        var (kx, ky) = (rx * (1 - Kappa), ry * (1 - Kappa));
        var (right, bottom) = (x + width, y + height);
        MoveTo(x + rx, y);
        LineTo(right - rx, y);
        CubicTo(right - kx, y, right, y + ky, right, y + ry);
        LineTo(right, bottom - ry);
        CubicTo(right, bottom - ky, right - kx, bottom, right - rx, bottom);
        LineTo(x + rx, bottom);
        CubicTo(x + kx, bottom, x, bottom - ky, x, bottom - ry);
        LineTo(x, y + ry);
        CubicTo(x, y + ky, x + kx, y, x + rx, y);
        Close();
    }

    /// <summary>Appends another path's segments, transformed.</summary>
    public void Append(SvgPath other, SvgMatrix matrix)
    {
        foreach (var s in other._segments)
        {
            var (x1, y1) = matrix.Apply(s.X1, s.Y1);
            var (x2, y2) = matrix.Apply(s.X2, s.Y2);
            var (x, y) = matrix.Apply(s.X, s.Y);
            switch (s.Op)
            {
                case PathOp.MoveTo:
                    MoveTo(x, y);
                    break;
                case PathOp.LineTo:
                    LineTo(x, y);
                    break;
                case PathOp.CubicTo:
                    CubicTo(x1, y1, x2, y2, x, y);
                    break;
                default:
                    Close();
                    break;
            }
        }
    }

    /// <summary>Whether every coordinate is a finite number (so the path can be written to PDF).</summary>
    public bool IsFinite() => _segments.All(s =>
        double.IsFinite(s.X1) && double.IsFinite(s.Y1) && double.IsFinite(s.X2) && double.IsFinite(s.Y2) && double.IsFinite(s.X) && double.IsFinite(s.Y));

    /// <summary>The exact bounding box of the geometry (curve extremes, not control points), or null when empty.</summary>
    public SvgBounds? Bounds()
    {
        if (_segments.Count == 0)
        {
            return null;
        }

        var (minX, minY, maxX, maxY) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        void Include(double x, double y)
        {
            (minX, maxX) = (Math.Min(minX, x), Math.Max(maxX, x));
            (minY, maxY) = (Math.Min(minY, y), Math.Max(maxY, y));
        }

        double px = 0, py = 0;
        foreach (var s in _segments)
        {
            switch (s.Op)
            {
                case PathOp.CubicTo:
                    Include(s.X, s.Y);
                    foreach (var t in Extremes(px, s.X1, s.X2, s.X).Concat(Extremes(py, s.Y1, s.Y2, s.Y)))
                    {
                        Include(Bezier(px, s.X1, s.X2, s.X, t), Bezier(py, s.Y1, s.Y2, s.Y, t));
                    }

                    break;
                default:
                    Include(s.X, s.Y);
                    break;
            }

            (px, py) = (s.X, s.Y);
        }

        return new SvgBounds(minX, minY, maxX, maxY);
    }

    private static double Bezier(double p0, double p1, double p2, double p3, double t)
    {
        var u = 1 - t;
        return (u * u * u * p0) + (3 * u * u * t * p1) + (3 * u * t * t * p2) + (t * t * t * p3);
    }

    /// <summary>Parameters in (0, 1) where one coordinate of a cubic Bézier has a local extreme.</summary>
    private static IEnumerable<double> Extremes(double p0, double p1, double p2, double p3)
    {
        // The derivative is a·t² + b·t + c.
        var a = 3 * (-p0 + (3 * p1) - (3 * p2) + p3);
        var b = 6 * (p0 - (2 * p1) + p2);
        var c = 3 * (p1 - p0);
        var roots = new List<double>(2);
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) > 1e-12)
            {
                roots.Add(-c / b);
            }
        }
        else
        {
            var discriminant = (b * b) - (4 * a * c);
            if (discriminant >= 0)
            {
                var root = Math.Sqrt(discriminant);
                roots.Add((-b + root) / (2 * a));
                roots.Add((-b - root) / (2 * a));
            }
        }

        return roots.Where(t => t is > 0 and < 1);
    }

    private void EnsureOpen()
    {
        if (!_open)
        {
            // Drawing after a close (or without a MoveTo) starts a new subpath at the current point.
            MoveTo(CurrentX, CurrentY);
        }
    }
}

using RaiCore.Pdf.Svg;

namespace RaiCore.Pdf.Metafiles;

/// <summary>
/// A path in the picture's form space (points, y up): figures of lines and cubic Béziers. Metafile geometry is mapped
/// to form space as it is recorded, so paths, clips and regions all live in one space.
/// </summary>
internal sealed class MetaPath
{
    private readonly List<byte> _kinds = [];
    private readonly List<(double X, double Y)> _points = [];
    private bool _open;

    private const byte Move = 0, Line = 1, Curve = 2, Close = 3;

    /// <summary>Whether the path has no segments.</summary>
    public bool IsEmpty => _kinds.Count == 0;

    /// <summary>The number of points.</summary>
    public int PointCount => _points.Count;

    /// <summary>Whether a figure is open (a point to continue from).</summary>
    public bool HasCurrentPoint => _open;

    /// <summary>The last point, or (0, 0).</summary>
    public (double X, double Y) LastPoint => _points.Count > 0 ? _points[^1] : (0, 0);

    public MetaPath MoveTo((double X, double Y) p)
    {
        if (_kinds.Count > 0 && _kinds[^1] == Move)
        {
            _points[^1] = p;
            return this;
        }

        _kinds.Add(Move);
        _points.Add(p);
        _open = true;
        return this;
    }

    public MetaPath LineTo((double X, double Y) p)
    {
        if (!_open)
        {
            return MoveTo(p);
        }

        _kinds.Add(Line);
        _points.Add(p);
        return this;
    }

    public MetaPath CurveTo((double X, double Y) c1, (double X, double Y) c2, (double X, double Y) p)
    {
        if (!_open)
        {
            MoveTo(c1);
        }

        _kinds.Add(Curve);
        _points.Add(c1);
        _points.Add(c2);
        _points.Add(p);
        return this;
    }

    /// <summary>Closes the current figure (a figure starting after it begins at the same point, as in PDF).</summary>
    public MetaPath ClosePath()
    {
        if (_open && _kinds.Count > 0 && _kinds[^1] != Close)
        {
            _kinds.Add(Close);
        }

        return this;
    }

    /// <summary>Adds the figures of another path.</summary>
    public MetaPath Append(MetaPath other)
    {
        _kinds.AddRange(other._kinds);
        _points.AddRange(other._points);
        _open = _open || other._open;
        return this;
    }

    /// <summary>The first point and the direction away from the line there (towards the outside), or null.</summary>
    public ((double X, double Y) Point, (double X, double Y) Direction)? Start()
    {
        if (_points.Count < 2)
        {
            return null;
        }

        var p = _points[0];
        for (var i = 1; i < _points.Count && _kinds.Count > 0; i++)
        {
            var q = _points[i];
            if (Math.Abs(q.X - p.X) + Math.Abs(q.Y - p.Y) > 1e-9)
            {
                return (p, Unit(q, p));
            }
        }

        return null;
    }

    /// <summary>The last point and the direction away from the line there (towards the outside), or null.</summary>
    public ((double X, double Y) Point, (double X, double Y) Direction)? End()
    {
        if (_points.Count < 2)
        {
            return null;
        }

        var p = _points[^1];
        for (var i = _points.Count - 2; i >= 0; i--)
        {
            var q = _points[i];
            if (Math.Abs(q.X - p.X) + Math.Abs(q.Y - p.Y) > 1e-9)
            {
                return (p, Unit(q, p));
            }
        }

        return null;
    }

    private static (double X, double Y) Unit((double X, double Y) from, (double X, double Y) to)
    {
        var (dx, dy) = (to.X - from.X, to.Y - from.Y);
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        return (dx / length, dy / length);
    }

    /// <summary>A copy mapped by a matrix.</summary>
    public MetaPath Transformed(SvgMatrix matrix)
    {
        var copy = new MetaPath { _open = _open };
        copy._kinds.AddRange(_kinds);
        foreach (var (x, y) in _points)
        {
            copy._points.Add(matrix.Apply(x, y));
        }

        return copy;
    }

    /// <summary>
    /// Adds an elliptical arc (center, radii and parameter angles in a space that <paramref name="matrix"/> maps to
    /// form space) as Béziers of at most a quarter turn each; a line to its start first if a figure is open.
    /// </summary>
    public MetaPath Arc(SvgMatrix matrix, double cx, double cy, double rx, double ry, double t0, double sweep)
    {
        var segments = Math.Clamp((int)Math.Ceiling((Math.Abs(sweep) / (Math.PI / 2)) - 1e-9), 1, 16);
        var step = sweep / segments;
        var k = 4.0 / 3 * Math.Tan(step / 4);
        var t = t0;
        var start = matrix.Apply(cx + (rx * Math.Cos(t)), cy + (ry * Math.Sin(t)));
        if (HasCurrentPoint)
        {
            LineTo(start);
        }
        else
        {
            MoveTo(start);
        }

        for (var i = 0; i < segments; i++)
        {
            var (s0, c0) = Math.SinCos(t);
            var (s1, c1) = Math.SinCos(t + step);
            CurveTo(
                matrix.Apply(cx + (rx * (c0 - (k * s0))), cy + (ry * (s0 + (k * c0)))),
                matrix.Apply(cx + (rx * (c1 + (k * s1))), cy + (ry * (s1 - (k * c1)))),
                matrix.Apply(cx + (rx * c1), cy + (ry * s1)));
            t += step;
        }

        return this;
    }

    /// <summary>A rectangle from two corners.</summary>
    public static MetaPath Rectangle((double X, double Y) a, (double X, double Y) b) =>
        new MetaPath().MoveTo(a).LineTo((b.X, a.Y)).LineTo(b).LineTo((a.X, b.Y)).ClosePath();

    /// <summary>A parallelogram from three corners (the fourth is opposite <paramref name="b"/>).</summary>
    public static MetaPath Parallelogram((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        new MetaPath().MoveTo(a).LineTo(b).LineTo((b.X + c.X - a.X, b.Y + c.Y - a.Y)).LineTo(c).ClosePath();

    /// <summary>The bounding box of the points (control points included), or null for an empty path.</summary>
    public (double Left, double Bottom, double Right, double Top)? Bounds()
    {
        if (_points.Count == 0)
        {
            return null;
        }

        double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        foreach (var (x, y) in _points)
        {
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            bottom = Math.Min(bottom, y);
            top = Math.Max(top, y);
        }

        return (left, bottom, right, top);
    }

    /// <summary>Whether every coordinate is a finite number of a sane size.</summary>
    public bool IsFinite => _points.TrueForAll(p => Math.Abs(p.X) < 1e7 && Math.Abs(p.Y) < 1e7);

    /// <summary>Adds the path to the canvas's current path.</summary>
    public void Emit(PdfCanvas canvas)
    {
        var p = 0;
        foreach (var kind in _kinds)
        {
            switch (kind)
            {
                case Move:
                    canvas.MoveTo(_points[p].X, _points[p].Y);
                    p++;
                    break;
                case Line:
                    canvas.LineTo(_points[p].X, _points[p].Y);
                    p++;
                    break;
                case Curve:
                    canvas.CurveTo(_points[p].X, _points[p].Y, _points[p + 1].X, _points[p + 1].Y, _points[p + 2].X, _points[p + 2].Y);
                    p += 3;
                    break;
                default:
                    canvas.ClosePath();
                    break;
            }
        }
    }
}

/// <summary>
/// A clipping region: the intersection of its parts, each a path with a fill rule. <c>null</c> stands for no clip.
/// Combinations PDF clipping cannot express exactly (a union with a region that is itself an intersection) are
/// approximated.
/// </summary>
internal sealed class ClipRegion
{
    private ClipRegion(IReadOnlyList<(MetaPath Path, bool EvenOdd)> parts) => Parts = parts;

    /// <summary>The paths whose intersection is the region.</summary>
    public IReadOnlyList<(MetaPath Path, bool EvenOdd)> Parts { get; }

    /// <summary>An empty region: nothing is drawn inside it.</summary>
    public static ClipRegion Empty { get; } = new([(new MetaPath(), false)]);

    public static ClipRegion Of(MetaPath path, bool evenOdd = false) => new([(path, evenOdd)]);

    /// <summary>Region operations ([MS-EMF] RegionMode, the same numbers in EMF+ CombineMode + 1).</summary>
    public const int And = 1, Or = 2, Xor = 3, Diff = 4, Copy = 5;

    /// <summary>
    /// Combines a region (<c>null</c>: everything) with a path. <paramref name="far"/> is a rectangle covering far
    /// more than the picture, for complements.
    /// </summary>
    public static ClipRegion? Combine(ClipRegion? current, ClipRegion? with, int mode, MetaPath far)
    {
        switch (mode)
        {
            case Copy:
                return with;
            case And:
                return current is null ? with : with is null ? current : new([.. current.Parts, .. with.Parts]);
            case Diff:
                // current minus with: the intersection with the complement of each part of "with" is exact for one part.
                if (with is null)
                {
                    return Empty;
                }

                return current is null && with.Parts.Count == 1
                    ? Complement(with.Parts[0], far)
                    : new([.. (current ?? Of(far)).Parts, .. with.Parts.Count == 1 ? Complement(with.Parts[0], far).Parts : []]);
            case Or:
                if (current is null || with is null)
                {
                    return null;
                }

                return Merge(current, with, evenOdd: false);
            case Xor:
                if (current is null)
                {
                    return with is null ? Empty : Complement(with.Parts[0], far);
                }

                return with is null ? Complement(current.Parts[0], far) : Merge(current, with, evenOdd: true);
            default:
                return current;
        }
    }

    /// <summary>The region and everything outside a part: the far rectangle with the part as a hole.</summary>
    private static ClipRegion Complement((MetaPath Path, bool EvenOdd) part, MetaPath far) =>
        new([(new MetaPath().Append(far).Append(part.Path), true)]);

    /// <summary>
    /// The union (or exclusive or) of two regions as one path. Exact for single paths with the same fill rule and
    /// winding; a region that is an intersection of several parts is approximated by its first part.
    /// </summary>
    private static ClipRegion Merge(ClipRegion a, ClipRegion b, bool evenOdd)
    {
        var path = new MetaPath().Append(a.Parts[0].Path).Append(b.Parts[0].Path);
        return new([(path, evenOdd || a.Parts[0].EvenOdd || b.Parts[0].EvenOdd)]);
    }

    /// <summary>The region moved by a vector.</summary>
    public ClipRegion Offset(double dx, double dy)
    {
        var move = SvgMatrix.Translate(dx, dy);
        return new([.. Parts.Select(part => (part.Path.Transformed(move), part.EvenOdd))]);
    }

    /// <summary>The bounding box of the region (of its first part's intersection with the others' boxes), or null when empty.</summary>
    public (double Left, double Bottom, double Right, double Top)? Bounds()
    {
        if (!_measured)
        {
            _bounds = Measure();
            _measured = true;
        }

        return _bounds;
    }

    private (double Left, double Bottom, double Right, double Top)? _bounds;
    private bool _measured;

    private (double Left, double Bottom, double Right, double Top)? Measure()
    {
        (double Left, double Bottom, double Right, double Top)? result = null;
        foreach (var (path, _) in Parts)
        {
            if (path.Bounds() is not { } b)
            {
                return null;
            }

            result = result is not { } r ? b : (Math.Max(r.Left, b.Left), Math.Max(r.Bottom, b.Bottom), Math.Min(r.Right, b.Right), Math.Min(r.Top, b.Top));
        }

        return result is { } box && box.Left <= box.Right && box.Bottom <= box.Top ? box : null;
    }
}

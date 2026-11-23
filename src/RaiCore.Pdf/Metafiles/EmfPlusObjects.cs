using System.Text;
using RaiCore.Pdf.Images;
using RaiCore.Pdf.Svg;

namespace RaiCore.Pdf.Metafiles;

/// <summary>A fill that paints only inside a boundary (an EMF+ path gradient), in form space.</summary>
internal sealed record ClippedBrush(MetaBrush Brush, MetaPath Boundary) : MetaBrush;

/// <summary>An EMF+ brush object ([MS-EMFPLUS] 2.2.1.1): solid, hatch, linear gradient or path gradient.</summary>
internal sealed class PlusBrush
{
    private int _type;
    private PdfColor _color = PdfColor.Black;
    private PdfColor _back = PdfColor.White;
    private int _hatch;
    private (double X, double Y, double Width, double Height) _rect;
    private SvgMatrix _transform = SvgMatrix.Identity;
    private List<(double Offset, PdfColor Color)> _stops = [];
    private (double X, double Y) _center;
    private (double X, double Y)[] _boundary = [];
    private PlusPath? _boundaryPath;

    /// <summary>The brush's main color (for pens and text, which use one color).</summary>
    public PdfColor Color => _type is 3 or 4 && _stops.Count > 0 ? _stops[0].Color : _color;

    public static PlusBrush? Read(MetaReader r, EmfPlusPlayer player)
    {
        r.U32();
        var brush = new PlusBrush { _type = (int)r.U32() };
        switch (brush._type)
        {
            case 0:
                brush._color = Argb(r.U32());
                return brush;
            case 1:
                brush._hatch = (int)r.U32();
                brush._color = Argb(r.U32());
                brush._back = Argb(r.U32());
                return brush;
            case 3:
                ReadPathGradient(r, brush, player);
                return brush;
            case 4:
                ReadLinearGradient(r, brush);
                return brush;
            default:
                player.Context.Report("EMF+ texture brushes are left out.");
                return null;
        }
    }

    private static void ReadLinearGradient(MetaReader r, PlusBrush brush)
    {
        var flags = r.U32();
        r.I32(); // Wrap mode: the gradient is padded, not repeated.
        brush._rect = (r.F32(), r.F32(), r.F32(), r.F32());
        var (start, end) = (Argb(r.U32()), Argb(r.U32()));
        r.U32();
        r.U32();
        if ((flags & 0x02) != 0)
        {
            brush._transform = new SvgMatrix(r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32());
        }

        brush._stops = [(0, start), (1, end)];
        if ((flags & 0x04) != 0)
        {
            brush._stops = PresetColors(r) ?? brush._stops;
        }
        else if ((flags & 0x08) != 0 || (flags & 0x10) != 0)
        {
            brush._stops = BlendFactors(r, start, end) ?? brush._stops;
        }
    }

    private static void ReadPathGradient(MetaReader r, PlusBrush brush, EmfPlusPlayer player)
    {
        var flags = r.U32();
        r.I32();
        var center = Argb(r.U32());
        brush._center = (r.F32(), r.F32());
        var count = r.U32();
        if (count > r.Remaining / 4)
        {
            throw new MetafileFormatException("an EMF+ path gradient is damaged");
        }

        var surround = new PdfColor[count];
        for (var i = 0; i < count; i++)
        {
            surround[i] = Argb(r.U32());
        }

        if ((flags & 0x01) != 0)
        {
            var size = r.I32();
            if (size < 0 || size > r.Remaining)
            {
                throw new MetafileFormatException("an EMF+ path gradient is damaged");
            }

            var start = r.Position;
            brush._boundaryPath = PlusPath.Read(new MetaReader(r.At(start, size).ToArray(), 0, size), player);
            r.Position = start + size;
        }
        else
        {
            var points = r.I32();
            if (points < 0 || points > r.Remaining / 8)
            {
                throw new MetafileFormatException("an EMF+ path gradient is damaged");
            }

            player.Context.Count(points);
            brush._boundary = new (double, double)[points];
            for (var i = 0; i < points; i++)
            {
                brush._boundary[i] = (r.F32(), r.F32());
            }
        }

        if ((flags & 0x02) != 0)
        {
            brush._transform = new SvgMatrix(r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32());
        }

        var outer = surround.Length > 0 ? surround[0] : center;
        brush._stops = [(0, center), (1, outer)];
        if ((flags & 0x04) != 0 && PresetColors(r) is { } preset)
        {
            // Positions go from the boundary (0) to the center (1).
            brush._stops = [.. preset.Select(stop => (1 - stop.Offset, stop.Color)).Reverse()];
        }
        else if ((flags & 0x08) != 0 && BlendFactors(r, outer, center) is { } blend)
        {
            brush._stops = [.. blend.Select(stop => (1 - stop.Offset, stop.Color)).Reverse()];
        }
    }

    private static List<(double Offset, PdfColor Color)>? PresetColors(MetaReader r)
    {
        var count = r.U32();
        if (count is 0 or > 256 || count > r.Remaining / 8)
        {
            return null;
        }

        var positions = new double[count];
        for (var i = 0; i < count; i++)
        {
            positions[i] = Math.Clamp(r.F32(), 0, 1);
        }

        var stops = new List<(double, PdfColor)>();
        for (var i = 0; i < count; i++)
        {
            stops.Add((positions[i], Argb(r.U32())));
        }

        return Sorted(stops);
    }

    private static List<(double Offset, PdfColor Color)>? BlendFactors(MetaReader r, PdfColor start, PdfColor end)
    {
        var count = r.U32();
        if (count is 0 or > 256 || count > r.Remaining / 8)
        {
            return null;
        }

        var positions = new double[count];
        for (var i = 0; i < count; i++)
        {
            positions[i] = Math.Clamp(r.F32(), 0, 1);
        }

        var stops = new List<(double, PdfColor)>();
        for (var i = 0; i < count; i++)
        {
            var f = Math.Clamp(r.F32(), 0, 1);
            stops.Add((positions[i], new PdfColor(
                start.Red + ((end.Red - start.Red) * f), start.Green + ((end.Green - start.Green) * f),
                start.Blue + ((end.Blue - start.Blue) * f), start.Alpha + ((end.Alpha - start.Alpha) * f))));
        }

        return Sorted(stops);
    }

    private static List<(double Offset, PdfColor Color)> Sorted(List<(double Offset, PdfColor Color)> stops)
    {
        // Offsets must not decrease.
        var last = 0.0;
        for (var i = 0; i < stops.Count; i++)
        {
            last = Math.Max(last, stops[i].Offset);
            stops[i] = (last, stops[i].Color);
        }

        return [.. stops.Select(s => (s.Offset, s.Color))];
    }

    internal static PdfColor Argb(uint argb) =>
        PdfColor.FromRgba((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (argb >> 24) / 255.0);

    /// <summary>The fill in form space, for world coordinates that <paramref name="worldToForm"/> maps (a device pixel is <paramref name="pointsPerPixel"/>).</summary>
    public MetaBrush? ToMeta(SvgMatrix worldToForm, double pointsPerPixel, MetafileContext context)
    {
        switch (_type)
        {
            case 0:
                return new SolidBrush(_color);
            case 1:
            {
                int? style = _hatch switch
                {
                    0 or 25 or 27 or 29 or 32 => 0,
                    1 or 24 or 26 or 28 or 33 => 1,
                    2 or 18 or 20 or 22 or 30 => 2,
                    3 or 19 or 21 or 23 or 31 => 3,
                    4 => 4,
                    5 => 5,
                    _ => null,
                };
                if (style is not { } s)
                {
                    // Percentage and other patterns: their average color.
                    double[] percents = [0.05, 0.10, 0.20, 0.25, 0.30, 0.40, 0.50, 0.60, 0.70, 0.75, 0.80, 0.90];
                    var p = _hatch is >= 6 and <= 17 ? percents[_hatch - 6] : 0.5;
                    return new SolidBrush(Mix(_back, _color, p));
                }

                // A hatch's cell is 8 device pixels square.
                return new HatchBrush(s, _color, _back.Alpha > 0 ? _back : null, 8 * pointsPerPixel, pointsPerPixel);
            }

            case 4:
            {
                var (x, y, w, _) = _rect;
                if (w == 0)
                {
                    return new SolidBrush(_stops[0].Color);
                }

                return new GradientBrush(new AxialGeometry(x, y, x + w, y), _stops, _transform.Then(worldToForm));
            }

            case 3:
            {
                var boundary = _boundaryPath?.ToForm(_transform.Then(worldToForm), false) ?? Boundary(_transform.Then(worldToForm));
                var points = _boundaryPath?.Points ?? _boundary;
                if (points.Length == 0 || boundary.IsEmpty)
                {
                    return new SolidBrush(_stops[^1].Color);
                }

                double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
                foreach (var (px, py) in points)
                {
                    (left, right, top, bottom) = (Math.Min(left, px), Math.Max(right, px), Math.Min(top, py), Math.Max(bottom, py));
                }

                var (hw, hh) = ((right - left) / 2, (bottom - top) / 2);
                if (hw <= 0 || hh <= 0)
                {
                    return new SolidBrush(_stops[^1].Color);
                }

                // From the center point to the ellipse that fits the boundary's box (a circle of radius 1 in this space).
                var (cx, cy) = ((left + right) / 2, (top + bottom) / 2);
                var space = new SvgMatrix(hw, 0, 0, hh, cx, cy).Then(_transform).Then(worldToForm);
                var geometry = new RadialGeometry((_center.X - cx) / hw, (_center.Y - cy) / hh, 0, 0, 0, 1);
                context.Report("EMF+ path gradients are drawn as radial gradients.");
                return new ClippedBrush(new GradientBrush(geometry, _stops, space), boundary);
            }

            default:
                return null;
        }
    }

    private MetaPath Boundary(SvgMatrix matrix)
    {
        var path = new MetaPath();
        foreach (var (x, y) in _boundary)
        {
            path.LineTo(matrix.Apply(x, y));
        }

        return path.ClosePath();
    }

    private static PdfColor Mix(PdfColor a, PdfColor b, double t) =>
        new(a.Red + ((b.Red - a.Red) * t), a.Green + ((b.Green - a.Green) * t), a.Blue + ((b.Blue - a.Blue) * t), Math.Max(a.Alpha, b.Alpha));
}

/// <summary>An EMF+ pen object ([MS-EMFPLUS] 2.2.1.7).</summary>
internal sealed class PlusPen
{
    private uint _unit;
    private double _width;
    private SvgMatrix _transform = SvgMatrix.Identity;
    private int _startCap;
    private int _endCap;
    private int _join;
    private double _miter = 10;
    private int _lineStyle;
    private double[]? _dashes;
    private PlusCap? _customStart;
    private PlusCap? _customEnd;
    private PlusBrush? _brush;

    public static PlusPen Read(MetaReader r, EmfPlusPlayer player)
    {
        r.U32();
        r.U32();
        var pen = new PlusPen();
        var flags = r.U32();
        pen._unit = r.U32();
        pen._width = r.F32();
        if ((flags & 0x1) != 0)
        {
            pen._transform = new SvgMatrix(r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32());
        }

        if ((flags & 0x2) != 0)
        {
            pen._startCap = r.I32();
        }

        if ((flags & 0x4) != 0)
        {
            pen._endCap = r.I32();
        }

        if ((flags & 0x8) != 0)
        {
            pen._join = r.I32();
        }

        if ((flags & 0x10) != 0)
        {
            pen._miter = r.F32();
        }

        if ((flags & 0x20) != 0)
        {
            pen._lineStyle = r.I32();
        }

        if ((flags & 0x40) != 0)
        {
            r.I32();
        }

        if ((flags & 0x80) != 0)
        {
            r.F32();
        }

        if ((flags & 0x100) != 0)
        {
            var count = r.U32();
            if (count > r.Remaining / 4)
            {
                throw new MetafileFormatException("an EMF+ pen is damaged");
            }

            var dashes = new double[count];
            for (var i = 0; i < count; i++)
            {
                dashes[i] = Math.Max(0, r.F32());
            }

            pen._dashes = count is > 0 and <= 64 ? dashes : null;
        }

        if ((flags & 0x200) != 0)
        {
            r.I32();
        }

        if ((flags & 0x400) != 0)
        {
            var count = r.U32();
            if (count > r.Remaining / 4)
            {
                throw new MetafileFormatException("an EMF+ pen is damaged");
            }

            r.Skip((int)count * 4);
        }

        if ((flags & 0x800) != 0)
        {
            pen._customStart = PlusCap.Read(r);
        }

        if ((flags & 0x1000) != 0)
        {
            pen._customEnd = PlusCap.Read(r);
        }

        pen._brush = r.Remaining >= 8 ? PlusBrush.Read(r, player) : null;
        return pen;
    }

    /// <summary>The stroke in form space: the width in world units (scaled by the world transform) or in a fixed unit.</summary>
    public MetaStroke ToStroke(SvgMatrix worldToForm, double pointsPerPixel, Func<int, (double X, double Y)> unitToPixels)
    {
        var color = _brush?.Color ?? PdfColor.Black;
        double width;
        if (_unit is 0 or 1)
        {
            var m = _transform.Then(worldToForm);
            width = _width * Math.Sqrt(Math.Abs((m.A * m.D) - (m.B * m.C)));
        }
        else
        {
            width = _width * unitToPixels((int)_unit).X * pointsPerPixel;
        }

        if (!(width > 0) || !double.IsFinite(width))
        {
            width = pointsPerPixel;
        }

        double[]? dashes = _lineStyle switch
        {
            1 => [3, 1],
            2 => [1, 1],
            3 => [3, 1, 1, 1],
            4 => [3, 1, 1, 1, 1, 1],
            5 when _dashes is { Length: > 0 } custom => [.. custom],
            _ => null,
        };
        if (dashes is not null)
        {
            for (var i = 0; i < dashes.Length; i++)
            {
                dashes[i] *= width;
            }
        }

        var cap = _startCap switch { 1 => LineCap.Square, 2 => LineCap.Round, _ => LineCap.Butt };
        var join = _join switch { 1 => LineJoin.Bevel, 2 => LineJoin.Round, _ => LineJoin.Miter };
        return new MetaStroke(color, width, dashes, cap, join, _miter);
    }

    /// <summary>Draws the arrowheads of anchor caps and custom (arrow) caps at the ends of an open path.</summary>
    public void DrawCaps(MetaPath path, MetaStroke stroke, MetaPainter painter, ClipRegion? clip)
    {
        Cap(path.Start(), _startCap, _customStart, stroke, painter, clip);
        Cap(path.End(), _endCap, _customEnd, stroke, painter, clip);
    }

    private static void Cap(((double X, double Y) Point, (double X, double Y) Direction)? end, int cap, PlusCap? custom, MetaStroke stroke, MetaPainter painter, ClipRegion? clip)
    {
        if (end is not { } e)
        {
            return;
        }

        var w = Math.Max(stroke.Width, 0.5);
        (double X, double Y)[]? shape = cap switch
        {
            0x14 => [(0, 1), (-1, -1), (1, -1)], // An arrow anchor: twice the width wide.
            0x13 => [(0, 1), (-1, 0), (0, -1), (1, 0)],
            0x11 => [(-1, 1), (1, 1), (1, -1), (-1, -1)],
            0xFF when custom is not null => custom.Shape,
            _ => null,
        };
        if (shape is null)
        {
            if (cap == 0x12)
            {
                var circle = new MetaPath().Arc(SvgMatrix.Identity, e.Point.X, e.Point.Y, w, w, 0, 2 * Math.PI).ClosePath();
                painter.Fill(circle, new SolidBrush(stroke.Color), false, clip);
            }

            return;
        }

        // Cap coordinates: x across the line, y along it (outwards), in pen widths, from the end point.
        var (dx, dy) = e.Direction;
        var path = new MetaPath();
        foreach (var (x, y) in shape)
        {
            path.LineTo((e.Point.X + (((x * -dy) + (y * dx)) * w), e.Point.Y + (((x * dx) + (y * dy)) * w)));
        }

        painter.Fill(path.ClosePath(), new SolidBrush(stroke.Color), false, clip);
    }
}

/// <summary>A custom line cap ([MS-EMFPLUS] 2.2.2.11): an adjustable arrow, or a default cap's fill path.</summary>
internal sealed class PlusCap
{
    /// <summary>The cap's outline in pen widths: x across the line, y along it outwards from the end point.</summary>
    public (double X, double Y)[] Shape { get; private init; } = [];

    public static PlusCap? Read(MetaReader r)
    {
        var size = r.I32();
        if (size < 0 || size > r.Remaining)
        {
            throw new MetafileFormatException("an EMF+ line cap is damaged");
        }

        var start = r.Position;
        r.Position = start + size;
        var cap = new MetaReader(r.At(start, size).ToArray(), 0, size);
        if (size < 8)
        {
            return null;
        }

        cap.U32();
        var type = cap.U32();
        if (type == 1 && cap.Remaining >= 12)
        {
            // An adjustable arrow: width and height in pen widths, its tip at the end point.
            var (width, height) = (cap.F32(), cap.F32());
            if (width <= 0 || height <= 0 || width > 100 || height > 100)
            {
                return null;
            }

            return new PlusCap { Shape = [(0, 0), (-width / 2, -height), (width / 2, -height)] };
        }

        if (type == 0 && cap.Remaining >= 52)
        {
            var flags = cap.U32();
            cap.Skip(4 + 4 + 4 + 4 + 4 + 4 + 4 + 8 + 8);
            if ((flags & 1) != 0 && cap.Remaining >= 4)
            {
                var length = cap.I32();
                if (length > 0 && length <= cap.Remaining)
                {
                    var bytes = cap.Bytes(length).ToArray();
                    var path = PlusPath.ReadPoints(new MetaReader(bytes, 0, bytes.Length));
                    if (path.Length is >= 3 and <= 256)
                    {
                        // The path's y points back along the line.
                        return new PlusCap { Shape = [.. path.Select(p => (p.X, -p.Y))] };
                    }
                }
            }
        }

        return null;
    }
}

/// <summary>An EMF+ path object ([MS-EMFPLUS] 2.2.1.6): points with types (start, line, Bézier; close flags).</summary>
internal sealed class PlusPath
{
    private byte[] _types = [];

    public (double X, double Y)[] Points { get; private set; } = [];

    public static PlusPath Read(MetaReader r, EmfPlusPlayer player)
    {
        var path = new PlusPath();
        r.U32();
        var count = r.U32();
        var flags = r.U32();
        if (count > r.Remaining)
        {
            throw new MetafileFormatException("an EMF+ path has more points than it holds");
        }

        player.Context.Count(count);
        path.Points = ReadPoints(r, (int)count, flags);
        path._types = new byte[count];
        if ((flags & 0x1000) != 0)
        {
            // Run-length encoded types.
            var i = 0;
            while (i < count && r.Remaining >= 2)
            {
                var run = r.U8() & 0x3F;
                var type = r.U8();
                for (var k = 0; k < run && i < count; k++)
                {
                    path._types[i++] = type;
                }
            }
        }
        else
        {
            r.Bytes((int)count).CopyTo(path._types);
        }

        return path;
    }

    /// <summary>The points of a path object, for the fill path of a line cap.</summary>
    public static (double X, double Y)[] ReadPoints(MetaReader r)
    {
        r.U32();
        var count = r.U32();
        var flags = r.U32();
        return count > r.Remaining ? [] : ReadPoints(r, (int)count, flags);
    }

    private static (double X, double Y)[] ReadPoints(MetaReader r, int count, uint flags)
    {
        var points = new (double X, double Y)[count];
        (double X, double Y) last = (0, 0);
        for (var i = 0; i < count; i++)
        {
            if ((flags & 0x0800) != 0)
            {
                last = (last.X + Relative(r), last.Y + Relative(r));
                points[i] = last;
            }
            else if ((flags & 0x4000) != 0)
            {
                points[i] = (r.I16(), r.I16());
            }
            else
            {
                points[i] = (r.F32(), r.F32());
            }
        }

        return points;
    }

    private static int Relative(MetaReader r)
    {
        var first = r.U8();
        if ((first & 0x80) == 0)
        {
            return (first & 0x40) != 0 ? first - 0x80 : first;
        }

        var value = ((first & 0x7F) << 8) | r.U8();
        return (value & 0x4000) != 0 ? value - 0x8000 : value;
    }

    /// <summary>The path in form space; <paramref name="closeAll"/> closes every figure (for regions).</summary>
    public MetaPath ToForm(SvgMatrix matrix, bool closeAll)
    {
        var path = new MetaPath();
        for (var i = 0; i < Points.Length; i++)
        {
            var type = _types[i] & 0x7;
            var point = matrix.Apply(Points[i].X, Points[i].Y);
            if (type == 0)
            {
                if (closeAll)
                {
                    path.ClosePath();
                }

                path.MoveTo(point);
            }
            else if (type == 3 && i + 2 < Points.Length)
            {
                path.CurveTo(point, matrix.Apply(Points[i + 1].X, Points[i + 1].Y), matrix.Apply(Points[i + 2].X, Points[i + 2].Y));
                i += 2;
            }
            else
            {
                path.LineTo(point);
            }

            if ((_types[i] & 0x80) != 0)
            {
                path.ClosePath();
            }
        }

        return closeAll ? path.ClosePath() : path;
    }
}

/// <summary>An EMF+ region object ([MS-EMFPLUS] 2.2.1.8): a tree of rectangles and paths combined.</summary>
internal sealed class PlusRegion
{
    private const int MaxDepth = 32;

    private uint _type;
    private (double X, double Y, double Width, double Height) _rect;
    private PlusPath? _path;
    private PlusRegion? _left;
    private PlusRegion? _right;

    public static PlusRegion Read(MetaReader r, EmfPlusPlayer player, int depth)
    {
        if (depth == 0)
        {
            r.U32();
            r.U32();
        }

        if (depth > MaxDepth)
        {
            throw new MetafileFormatException("an EMF+ region is nested too deeply");
        }

        player.Context.Count(1);
        var region = new PlusRegion { _type = r.U32() };
        switch (region._type)
        {
            case 0x10000000:
                region._rect = (r.F32(), r.F32(), r.F32(), r.F32());
                break;
            case 0x10000001:
            {
                var length = r.I32();
                if (length < 0 || length > r.Remaining)
                {
                    throw new MetafileFormatException("an EMF+ region is damaged");
                }

                var start = r.Position;
                region._path = PlusPath.Read(new MetaReader(r.At(start, length).ToArray(), 0, length), player);
                r.Position = start + length;
                break;
            }

            case 0x10000002 or 0x10000003:
                break;
            case >= 1 and <= 5:
                region._left = Read(r, player, depth + 1);
                region._right = Read(r, player, depth + 1);
                break;
            default:
                throw new MetafileFormatException("an EMF+ region has an unknown node");
        }

        return region;
    }

    /// <summary>The region in form space (null: infinite).</summary>
    public ClipRegion? ToForm(SvgMatrix matrix, MetaPath far)
    {
        switch (_type)
        {
            case 0x10000000:
            {
                var (x, y, w, h) = _rect;
                return ClipRegion.Of(MetaPath.Parallelogram(matrix.Apply(x, y), matrix.Apply(x + w, y), matrix.Apply(x, y + h)));
            }

            case 0x10000001:
                return ClipRegion.Of(_path!.ToForm(matrix, true), evenOdd: true);
            case 0x10000002:
                return ClipRegion.Empty;
            case 0x10000003:
                return null;
            default:
            {
                var left = _left!.ToForm(matrix, far);
                var right = _right!.ToForm(matrix, far);
                return _type switch
                {
                    1 => ClipRegion.Combine(left, right, ClipRegion.And, far),
                    2 => ClipRegion.Combine(left, right, ClipRegion.Or, far),
                    3 => ClipRegion.Combine(left, right, ClipRegion.Xor, far),
                    4 => ClipRegion.Combine(left, right, ClipRegion.Diff, far),
                    _ => ClipRegion.Combine(right, left, ClipRegion.Diff, far),
                };
            }
        }
    }
}

/// <summary>An EMF+ image object ([MS-EMFPLUS] 2.2.1.4): a bitmap or a nested metafile.</summary>
internal sealed record PlusImage(PdfImage Image, bool Metafile)
{
    public static PlusImage? Read(MetaReader r, EmfPlusPlayer player)
    {
        var context = player.Context;
        r.U32();
        var type = r.U32();
        try
        {
            if (type == 1)
            {
                var (width, height, stride) = (r.I32(), r.I32(), r.I32());
                var format = r.U32();
                var compressed = r.U32() == 1;
                if (compressed)
                {
                    return new PlusImage(PdfImage.Load(r.Bytes(r.Remaining).ToArray()), false);
                }

                return Pixels(r, width, height, stride, format, context) is { } image ? new PlusImage(PdfImage.FromDecoded(image), false) : null;
            }

            if (type == 2)
            {
                var metafileType = r.U32();
                var size = r.U32();
                if (size > r.Remaining)
                {
                    throw new MetafileFormatException("an EMF+ metafile image is damaged");
                }

                if (context.Depth >= MetafileContext.MaxDepth)
                {
                    context.Report("Pictures nested more deeply than 4 levels are left out.");
                    return null;
                }

                var data = r.Bytes((int)size).ToArray();
                if (metafileType == 1 && !WmfHeader.IsWmf(data))
                {
                    return null;
                }

                return new PlusImage(PdfImage.FromMetafile(MetafileImage.Load(data, context.Nested())), true);
            }
        }
        catch (ImageFormatException ex)
        {
            context.Report($"An image in the picture cannot be read ({ex.Message}); it is left out.", damaged: true);
        }
        catch (ResourceLimitExceededException ex) when (ex.LimitName == nameof(ResourceLimits.MaxImagePixels))
        {
            context.Report($"An image in the picture is left out: it has more pixels than ResourceLimits.MaxImagePixels allows ({ex.Limit}).", damaged: true);
        }

        return null;
    }

    /// <summary>Uncompressed pixels ([MS-EMFPLUS] PixelFormat): 1, 4 and 8-bit indexed, 24 and 32-bit RGB and ARGB.</summary>
    private static DecodedImage? Pixels(MetaReader r, int width, int height, int stride, uint format, MetafileContext context)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        ResourceLimits.CheckImagePixels(width, height);
        PdfColor[]? palette = null;
        if ((format & 0x00010000) != 0)
        {
            r.U32();
            var count = r.U32();
            if (count > 256 || count > r.Remaining / 4)
            {
                throw new MetafileFormatException("an EMF+ bitmap palette is damaged");
            }

            palette = new PdfColor[count];
            for (var i = 0; i < count; i++)
            {
                palette[i] = PlusBrush.Argb(r.U32());
            }
        }

        var bits = (int)((format >> 8) & 0xFF);
        if (bits is not (1 or 4 or 8 or 24 or 32) || (bits <= 8 && palette is null))
        {
            context.Report("An EMF+ bitmap in an unsupported pixel format is left out.");
            return null;
        }

        var rowBytes = (((long)width * bits) + 7) / 8;
        if (stride < rowBytes || (long)stride * height > r.Remaining)
        {
            throw new MetafileFormatException("an EMF+ bitmap is shorter than its size");
        }

        var pixels = r.Bytes(stride * height);
        var rgb = new byte[(long)width * height * 3];
        var alpha = new byte[(long)width * height];
        var anyAlpha = false;
        var premultiplied = format == 0x000E200B;
        var hasAlpha = format is 0x0026200A or 0x000E200B || palette is not null;
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * stride, (int)rowBytes);
            for (var x = 0; x < width; x++)
            {
                byte red, green, blue, a = 255;
                if (bits <= 8)
                {
                    var bit = x * bits;
                    var index = (row[bit / 8] >> (8 - bits - (bit % 8))) & ((1 << bits) - 1);
                    var color = index < palette!.Length ? palette[index] : PdfColor.Black;
                    (red, green, blue, a) = ((byte)Math.Round(color.Red * 255), (byte)Math.Round(color.Green * 255), (byte)Math.Round(color.Blue * 255), (byte)Math.Round(color.Alpha * 255));
                }
                else
                {
                    var o = x * (bits / 8);
                    (blue, green, red) = (row[o], row[o + 1], row[o + 2]);
                    if (bits == 32 && hasAlpha)
                    {
                        a = row[o + 3];
                        if (premultiplied && a is > 0 and < 255)
                        {
                            (red, green, blue) = ((byte)Math.Min(255, red * 255 / a), (byte)Math.Min(255, green * 255 / a), (byte)Math.Min(255, blue * 255 / a));
                        }
                    }
                }

                var p = (y * width) + x;
                (rgb[p * 3], rgb[(p * 3) + 1], rgb[(p * 3) + 2]) = (red, green, blue);
                alpha[p] = a;
                anyAlpha |= a != 255;
            }
        }

        return new DecodedImage(width, height, ImageColorSpace.Rgb, 8, rgb, ImageEncoding.Raw) { Alpha = anyAlpha ? alpha : null };
    }
}

/// <summary>An EMF+ font object ([MS-EMFPLUS] 2.2.1.5).</summary>
internal sealed class PlusFont
{
    private double _emSize;
    private uint _unit;

    public string Family { get; private set; } = "Arial";

    /// <summary>Bold 1, italic 2, underline 4, strikeout 8.</summary>
    public int Style { get; private set; }

    public static PlusFont Read(MetaReader r)
    {
        r.U32();
        var font = new PlusFont { _emSize = r.F32(), _unit = r.U32(), Style = r.I32() };
        r.U32();
        var length = r.U32();
        if (length > 0 && length <= r.Remaining / 2)
        {
            var family = Encoding.Unicode.GetString(r.Bytes((int)length * 2)).TrimEnd('\0').Trim();
            if (family.Length > 0)
            {
                font.Family = family;
            }
        }

        return font;
    }

    /// <summary>The em size in world units, for the player's current transforms.</summary>
    public double WorldEm(EmfPlusPlayer player)
    {
        if (_unit is 0 or 1)
        {
            return _emSize;
        }

        var dpi = player.Dpi;
        var pixels = _unit switch
        {
            3 => _emSize * dpi.Y / 72,
            4 => _emSize * dpi.Y,
            5 => _emSize * dpi.Y / 300,
            6 => _emSize * dpi.Y / 25.4,
            _ => _emSize,
        };

        // World units per device pixel: the inverse of the world and page scale (form points per pixel are 72 / dpi).
        var m = player.CurrentWorldToForm;
        var formPerWorld = Math.Sqrt(Math.Abs((m.A * m.D) - (m.B * m.C)));
        var formPerPixel = 72 / dpi.Y;
        return formPerWorld > 0 ? pixels * formPerPixel / formPerWorld : 0;
    }
}

/// <summary>An EMF+ string format object ([MS-EMFPLUS] 2.2.1.9): alignment, wrapping and margins.</summary>
internal sealed class PlusStringFormat
{
    public uint Flags { get; private set; }

    /// <summary>0 near, 1 center, 2 far.</summary>
    public uint Alignment { get; private set; }

    public uint LineAlignment { get; private set; }

    /// <summary>The space before a line, in ems.</summary>
    public double LeadingMargin { get; private set; } = 1 / 6.0;

    public static PlusStringFormat Read(MetaReader r)
    {
        r.U32();
        var format = new PlusStringFormat { Flags = r.U32() };
        r.U32();
        format.Alignment = r.U32();
        format.LineAlignment = r.U32();
        r.U32();
        r.U32();
        r.F32();
        r.I32();
        var margin = r.F32();
        format.LeadingMargin = margin is >= 0 and < 10 ? margin : 1 / 6.0;
        return format;
    }
}

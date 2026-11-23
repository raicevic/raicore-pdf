using System.Buffers.Binary;
using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Images;
using RaiCore.Pdf.Svg;

namespace RaiCore.Pdf.Metafiles;

/// <summary>
/// Plays EMF+ records ([MS-EMFPLUS]) from the comment records of an EMF file onto a <see cref="MetaPainter"/>: the
/// common fills and lines, paths, regions, gradients, images (bitmaps and nested metafiles) and strings, with world
/// and page transforms, clipping, and saved states and containers.
/// </summary>
internal sealed partial class EmfPlusPlayer
{
    private const int MaxObjects = 64;
    private const int MaxSaved = 1024;

    private readonly MetafileContext _context;
    private readonly MetaPainter _painter;
    private readonly (double Left, double Top, double Width, double Height) _frame;
    private readonly object?[] _objects = new object?[MaxObjects];
    private readonly List<(uint Index, PlusState State)> _saved = [];
    private (int Id, int Type, uint Total, List<byte> Data)? _pending;
    private PlusState _state = new();
    private (double X, double Y) _dpi = (96, 96);

    public EmfPlusPlayer(MetafileContext context, MetaPainter painter, (double Left, double Top, double Width, double Height) frame)
    {
        _context = context;
        _painter = painter;
        _frame = frame;
    }

    /// <summary>The graphics state that Save and BeginContainer keep.</summary>
    private sealed class PlusState
    {
        public SvgMatrix World { get; set; } = SvgMatrix.Identity;

        public int PageUnit { get; set; } = 2;

        public double PageScale { get; set; } = 1;

        public ClipRegion? Clip { get; set; }

        public PlusState Clone() => (PlusState)MemberwiseClone();
    }

    /// <summary>The record types the player draws or safely ignores.</summary>
    private static readonly HashSet<int> Known =
    [
        0x4001, 0x4002, 0x4003, 0x4004, 0x4008, 0x4009, 0x400A, 0x400B, 0x400C, 0x400D, 0x400E, 0x400F, 0x4010, 0x4011, 0x4012,
        0x4013, 0x4014, 0x4015, 0x4016, 0x4017, 0x4018, 0x4019, 0x401A, 0x401B, 0x401C, 0x401D, 0x401E, 0x401F, 0x4020, 0x4021,
        0x4022, 0x4023, 0x4024, 0x4025, 0x4026, 0x4027, 0x4028, 0x4029, 0x402A, 0x402B, 0x402C, 0x402D, 0x402E, 0x402F, 0x4030,
        0x4031, 0x4032, 0x4033, 0x4034, 0x4035, 0x4036, 0x4039, 0x403A,
    ];

    /// <summary>
    /// Looks at the EMF+ records of one comment: whether they include the EMF+ header (and if it says dual mode), and
    /// whether the player can draw all of them (else a dual-mode file is drawn from its EMF records).
    /// </summary>
    public static (bool Header, bool Dual, bool Supported) Scan(byte[] data, int start, int end)
    {
        var (header, dual, supported) = (false, false, true);
        var offset = start;
        while (offset <= end - 12)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            if (size < 12 || size > end - offset)
            {
                break;
            }

            var body = data.AsSpan(offset + 12, (int)size - 12);
            switch (type)
            {
                case 0x4001:
                    header = true;
                    dual = (flags & 1) != 0;
                    break;
                case 0x4008:
                {
                    var objectType = (flags >> 8) & 0x7F;
                    var continued = (flags & 0x8000) != 0;
                    var payload = continued && body.Length >= 4 ? body[4..] : body;
                    if (objectType is 0 or > 9)
                    {
                        supported = false;
                    }
                    else if (objectType == 1 && payload.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]) == 2)
                    {
                        // Texture brushes.
                        supported = false;
                    }

                    break;
                }

                case 0x4036:
                    if (body.Length >= 8 && (BinaryPrimitives.ReadUInt32LittleEndian(body[4..]) & 1) == 0)
                    {
                        // Glyph numbers instead of characters.
                        supported = false;
                    }

                    break;
                default:
                    if (!Known.Contains(type))
                    {
                        supported = false;
                    }

                    break;
            }

            offset += (int)size;
        }

        return (header, dual, supported);
    }

    /// <summary>
    /// Plays the EMF+ records of one comment record. Returns whether EMF records that follow are drawn: after a GetDC
    /// record, until the next EMF+ record.
    /// </summary>
    public bool Play(byte[] data, int start, int end, bool gdiEnabled)
    {
        var offset = start;
        while (offset <= end - 12)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8));
            if (size < 12 || size > end - offset || dataSize > size - 12)
            {
                throw new MetafileFormatException("an EMF+ record has an invalid size");
            }

            _context.Count(1);
            gdiEnabled = type == 0x4004;
            Record(type, flags, new MetaReader(data, offset + 12, offset + 12 + (int)dataSize));
            offset += (int)size;
        }

        return gdiEnabled;
    }

    private void Record(int type, int flags, MetaReader r)
    {
        switch (type)
        {
            case 0x4001:
                r.U32();
                r.U32();
                var (dpiX, dpiY) = (r.U32(), r.U32());
                _dpi = (dpiX is > 0 and < 10000 ? dpiX : 96, dpiY is > 0 and < 10000 ? dpiY : 96);
                break;
            case 0x4008:
                ObjectRecord(flags, r);
                break;
            case 0x4009:
                _painter.Fill(_painter.Far, new SolidBrush(Argb(r.U32())), false, _state.Clip);
                break;
            case 0x400A:
                FillRects(flags, r);
                break;
            case 0x400B:
                DrawRects(flags, r);
                break;
            case 0x400C:
            {
                var brush = r.U32();
                var points = Points(r, (int)Math.Min(r.U32(), int.MaxValue), flags);
                Fill(Polygon(points, close: true), brush, flags, evenOdd: true);
                break;
            }

            case 0x400D:
            {
                var points = Points(r, (int)Math.Min(r.U32(), int.MaxValue), flags);
                Draw(Polygon(points, close: (flags & 0x2000) != 0), flags & 0xFF);
                break;
            }

            case 0x400E:
            {
                var brush = r.U32();
                Fill(Ellipse(ReadRect(r, flags)), brush, flags, evenOdd: false);
                break;
            }

            case 0x400F:
                Draw(Ellipse(ReadRect(r, flags)), flags & 0xFF);
                break;
            case 0x4010:
            {
                var brush = r.U32();
                var (startAngle, sweep) = (r.F32(), r.F32());
                Fill(Pie(ReadRect(r, flags), startAngle, sweep, pie: true), brush, flags, evenOdd: false);
                break;
            }

            case 0x4011 or 0x4012:
            {
                var (startAngle, sweep) = (r.F32(), r.F32());
                Draw(Pie(ReadRect(r, flags), startAngle, sweep, pie: type == 0x4011), flags & 0xFF);
                break;
            }

            case 0x4013:
            {
                var brush = r.U32();
                if (Get(flags & 0xFF) is PlusRegion region && RegionToForm(region) is { } clip)
                {
                    FillRegion(clip, brush, flags);
                }

                break;
            }

            case 0x4014:
            {
                var brush = r.U32();
                if (Get(flags & 0xFF) is PlusPath path)
                {
                    // EMF+ paths keep no fill mode: even-odd, GDI+'s default.
                    Fill(path.ToForm(WorldToForm, false), brush, flags, evenOdd: true);
                }

                break;
            }

            case 0x4015:
            {
                var pen = r.U32();
                if (Get(flags & 0xFF) is PlusPath path)
                {
                    Draw(path.ToForm(WorldToForm, false), (int)pen);
                }

                break;
            }

            case 0x4016:
            {
                var brush = r.U32();
                var tension = r.F32();
                var points = Points(r, (int)Math.Min(r.U32(), int.MaxValue), flags);
                Fill(Curve(points, tension, closed: true, 0, points.Length), brush, flags, evenOdd: (flags & 0x2000) == 0);
                break;
            }

            case 0x4017:
            {
                var tension = r.F32();
                var points = Points(r, (int)Math.Min(r.U32(), int.MaxValue), flags);
                Draw(Curve(points, tension, closed: true, 0, points.Length), flags & 0xFF);
                break;
            }

            case 0x4018:
            {
                var tension = r.F32();
                var (offset, segments) = (r.U32(), r.U32());
                var points = Points(r, (int)Math.Min(r.U32(), int.MaxValue), flags);
                if (offset < points.Length)
                {
                    Draw(Curve(points, tension, closed: false, (int)offset, (int)Math.Min(segments, points.Length - offset - 1)), flags & 0xFF);
                }

                break;
            }

            case 0x4019:
            {
                var points = Points(r, (int)Math.Min(r.U32(), int.MaxValue), flags);
                Draw(Beziers(points), flags & 0xFF);
                break;
            }

            case 0x401A or 0x401B:
                DrawImage(type == 0x401B, flags, r);
                break;
            case 0x401C:
                DrawString(flags, r);
                break;
            case 0x4036:
                DrawDriverString(flags, r);
                break;
            case 0x4025 or 0x4028:
                Save(r.U32());
                break;
            case 0x4026 or 0x4029:
                Restore(r.U32());
                break;
            case 0x4027:
                BeginContainer(r);
                break;
            case 0x402A:
                SetWorld(Matrix(r));
                break;
            case 0x402B:
                SetWorld(SvgMatrix.Identity);
                break;
            case 0x402C:
                Multiply(Matrix(r), flags);
                break;
            case 0x402D:
                Multiply(SvgMatrix.Translate(r.F32(), r.F32()), flags);
                break;
            case 0x402E:
                Multiply(SvgMatrix.Scale(r.F32(), r.F32()), flags);
                break;
            case 0x402F:
                Multiply(SvgMatrix.Rotate(r.F32()), flags);
                break;
            case 0x4030:
            {
                var scale = r.F32();
                _state.PageUnit = flags & 0xFF;
                _state.PageScale = scale > 0 ? scale : 1;
                break;
            }

            case 0x4031:
                _state.Clip = null;
                break;
            case 0x4032:
            {
                var rect = Rect(r, compressed: false);
                Clip(Polygon([rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft], close: true), evenOdd: false, (flags >> 8) & 0xF);
                break;
            }

            case 0x4033:
                if (Get(flags & 0xFF) is PlusPath clipPath)
                {
                    Clip(clipPath.ToForm(WorldToForm, false), evenOdd: true, (flags >> 8) & 0xF);
                }

                break;
            case 0x4034:
                if (Get(flags & 0xFF) is PlusRegion clipRegion)
                {
                    Combine(RegionToForm(clipRegion), (flags >> 8) & 0xF);
                }

                break;
            case 0x4035:
            {
                var (dx, dy) = (r.F32(), r.F32());
                var m = WorldToForm;
                _state.Clip = _state.Clip?.Offset((m.A * dx) + (m.C * dy), (m.B * dx) + (m.D * dy));
                break;
            }
        }
    }

    // ----- Transforms -----

    /// <summary>Pixels per page unit ([MS-EMFPLUS] UnitType), horizontally and vertically.</summary>
    private (double X, double Y) UnitToPixels(int unit) => unit switch
    {
        3 => (_dpi.X / 72, _dpi.Y / 72),
        4 => (_dpi.X, _dpi.Y),
        5 => (_dpi.X / 300, _dpi.Y / 300),
        6 => (_dpi.X / 25.4, _dpi.Y / 25.4),
        _ => (1, 1),
    };

    /// <summary>Device pixels to form points: millimeters from the EMF frame's top left, y up.</summary>
    private SvgMatrix DeviceToForm
    {
        get
        {
            const double PointsPerMillimeter = 72 / 25.4;
            return new SvgMatrix(72 / _dpi.X, 0, 0, -72 / _dpi.Y, -_frame.Left * PointsPerMillimeter, _painter.Height + (_frame.Top * PointsPerMillimeter));
        }
    }

    /// <summary>World coordinates to form space: the world transform, the page transform, then the device.</summary>
    private SvgMatrix WorldToForm
    {
        get
        {
            var (ux, uy) = UnitToPixels(_state.PageUnit);
            return _state.World.Then(SvgMatrix.Scale(ux * _state.PageScale, uy * _state.PageScale)).Then(DeviceToForm);
        }
    }

    private static double Scale(SvgMatrix m)
    {
        var scale = Math.Sqrt(Math.Abs((m.A * m.D) - (m.B * m.C)));
        return double.IsFinite(scale) ? scale : 0;
    }

    private void SetWorld(SvgMatrix world)
    {
        if (world.IsFinite)
        {
            _state.World = world;
        }
    }

    /// <summary>Multiplies the world transform: the new matrix applies after the old one when the A flag is set, else before.</summary>
    private void Multiply(SvgMatrix matrix, int flags) => SetWorld((flags & 0x2000) != 0 ? _state.World.Then(matrix) : matrix.Then(_state.World));

    private static SvgMatrix Matrix(MetaReader r) => new(r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32());

    private void Save(uint index)
    {
        if (_saved.Count < MaxSaved)
        {
            _saved.Add((index, _state.Clone()));
        }
    }

    private void Restore(uint index)
    {
        for (var i = _saved.Count - 1; i >= 0; i--)
        {
            if (_saved[i].Index == index)
            {
                _state = _saved[i].State;
                _saved.RemoveRange(i, _saved.Count - i);
                return;
            }
        }
    }

    private void BeginContainer(MetaReader r)
    {
        var dest = Rect(r, compressed: false);
        var src = Rect(r, compressed: false);
        Save(r.U32());
        if (src.Width == 0 || src.Height == 0)
        {
            return;
        }

        // The source rectangle maps onto the destination (in world units); as other players do, the source's unit
        // (in the record's flags) is taken to be the world's.
        var (sx, sy) = (dest.Width / src.Width, dest.Height / src.Height);
        var container = new SvgMatrix(sx, 0, 0, sy, dest.X - (src.X * sx), dest.Y - (src.Y * sy));
        SetWorld(container.Then(_state.World));
    }

    // ----- Clipping -----

    private void Clip(MetaPath path, bool evenOdd, int mode) => Combine(ClipRegion.Of(path, evenOdd), mode);

    /// <summary>Combines the clip with a region ([MS-EMFPLUS] CombineMode: replace, intersect, union, xor, exclude, complement).</summary>
    private void Combine(ClipRegion? region, int mode)
    {
        _state.Clip = mode switch
        {
            0 => region,
            1 => ClipRegion.Combine(_state.Clip, region, ClipRegion.And, _painter.Far),
            2 => ClipRegion.Combine(_state.Clip, region, ClipRegion.Or, _painter.Far),
            3 => ClipRegion.Combine(_state.Clip, region, ClipRegion.Xor, _painter.Far),
            4 => ClipRegion.Combine(_state.Clip, region, ClipRegion.Diff, _painter.Far),
            5 => ClipRegion.Combine(region, _state.Clip, ClipRegion.Diff, _painter.Far),
            _ => _state.Clip,
        };
    }

    // ----- Geometry -----

    private readonly record struct RectF(double X, double Y, double Width, double Height)
    {
        public (double X, double Y) TopLeft => (X, Y);

        public (double X, double Y) TopRight => (X + Width, Y);

        public (double X, double Y) BottomRight => (X + Width, Y + Height);

        public (double X, double Y) BottomLeft => (X, Y + Height);
    }

    private static RectF Rect(MetaReader r, bool compressed) =>
        compressed ? new RectF(r.I16(), r.I16(), r.I16(), r.I16()) : new RectF(r.F32(), r.F32(), r.F32(), r.F32());

    private static RectF ReadRect(MetaReader r, int flags) => Rect(r, (flags & 0x4000) != 0);

    /// <summary>Points: floats, 16-bit integers (C flag) or relative to the previous point (P flag).</summary>
    private (double X, double Y)[] Points(MetaReader r, int count, int flags)
    {
        var relative = (flags & 0x0800) != 0;
        var compressed = (flags & 0x4000) != 0;
        var minimum = relative ? 2 : compressed ? 4 : 8;
        if (count < 0 || count > r.Remaining / minimum)
        {
            throw new MetafileFormatException("an EMF+ record has more points than it holds");
        }

        _context.Count(count);
        var points = new (double X, double Y)[count];
        (double X, double Y) last = (0, 0);
        for (var i = 0; i < count; i++)
        {
            if (relative)
            {
                last = (last.X + RelativeValue(r), last.Y + RelativeValue(r));
                points[i] = last;
            }
            else
            {
                points[i] = compressed ? (r.I16(), r.I16()) : (r.F32(), r.F32());
            }
        }

        return points;
    }

    /// <summary>An EmfPlusInteger7 or EmfPlusInteger15.</summary>
    private static int RelativeValue(MetaReader r)
    {
        var first = r.U8();
        if ((first & 0x80) == 0)
        {
            return (first & 0x40) != 0 ? first - 0x80 : first;
        }

        var value = ((first & 0x7F) << 8) | r.U8();
        return (value & 0x4000) != 0 ? value - 0x8000 : value;
    }

    private MetaPath Polygon(ReadOnlySpan<(double X, double Y)> points, bool close)
    {
        var m = WorldToForm;
        var path = new MetaPath();
        foreach (var (x, y) in points)
        {
            path.LineTo(m.Apply(x, y));
        }

        return close ? path.ClosePath() : path;
    }

    private MetaPath Ellipse(RectF rect)
    {
        var path = new MetaPath();
        path.Arc(WorldToForm, rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2), Math.Abs(rect.Width / 2), Math.Abs(rect.Height / 2), 0, 2 * Math.PI);
        return path.ClosePath();
    }

    /// <summary>An arc or pie of the ellipse in a rectangle: angles in degrees, clockwise (with y down) from the x axis.</summary>
    private MetaPath Pie(RectF rect, double startDegrees, double sweepDegrees, bool pie)
    {
        var (cx, cy, rx, ry) = (rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2), Math.Abs(rect.Width / 2), Math.Abs(rect.Height / 2));
        var path = new MetaPath();
        if (rx <= 0 || ry <= 0)
        {
            return path;
        }

        // The angles are of points on the ellipse, not its parameter.
        static double Parameter(double degrees, double rx, double ry)
        {
            var a = degrees * Math.PI / 180;
            var t = Math.Atan2(rx * Math.Sin(a), ry * Math.Cos(a));
            return t + (Math.Round((a - t) / (2 * Math.PI)) * 2 * Math.PI);
        }

        var sweep = Math.Clamp(sweepDegrees, -360, 360);
        var t0 = Parameter(startDegrees, rx, ry);
        var t1 = Math.Abs(sweep) >= 360 ? t0 + (Math.Sign(sweep) * 2 * Math.PI) : Parameter(startDegrees + sweep, rx, ry);
        var m = WorldToForm;
        if (pie)
        {
            path.MoveTo(m.Apply(cx, cy));
        }

        path.Arc(m, cx, cy, rx, ry, t0, t1 - t0);
        return pie ? path.ClosePath() : path;
    }

    private MetaPath Beziers(ReadOnlySpan<(double X, double Y)> points)
    {
        var m = WorldToForm;
        var path = new MetaPath();
        if (points.Length < 4)
        {
            return path;
        }

        path.MoveTo(m.Apply(points[0].X, points[0].Y));
        for (var i = 1; i + 2 < points.Length; i += 3)
        {
            path.CurveTo(m.Apply(points[i].X, points[i].Y), m.Apply(points[i + 1].X, points[i + 1].Y), m.Apply(points[i + 2].X, points[i + 2].Y));
        }

        return path;
    }

    /// <summary>A cardinal spline through points as Béziers (tension 0.5 is a Catmull-Rom spline).</summary>
    private MetaPath Curve(ReadOnlySpan<(double X, double Y)> points, double tension, bool closed, int first, int segments)
    {
        var path = new MetaPath();
        var n = points.Length;
        if (n < 2 || segments <= 0)
        {
            return path;
        }

        var m = WorldToForm;
        var k = tension / 3;
        (double X, double Y) At(int i, ReadOnlySpan<(double X, double Y)> p) => closed ? p[((i % n) + n) % n] : p[Math.Clamp(i, 0, n - 1)];
        var count = closed ? n : Math.Min(segments, n - 1 - first);
        path.MoveTo(m.Apply(At(first, points).X, At(first, points).Y));
        for (var s = 0; s < count; s++)
        {
            var i = first + s;
            var (p0, p1, p2, p3) = (At(i - 1, points), At(i, points), At(i + 1, points), At(i + 2, points));
            path.CurveTo(
                m.Apply(p1.X + (k * (p2.X - p0.X)), p1.Y + (k * (p2.Y - p0.Y))),
                m.Apply(p2.X - (k * (p3.X - p1.X)), p2.Y - (k * (p3.Y - p1.Y))),
                m.Apply(p2.X, p2.Y));
        }

        return closed ? path.ClosePath() : path;
    }

    // ----- Fills and lines -----

    private static PdfColor Argb(uint argb) =>
        PdfColor.FromRgba((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (argb >> 24) / 255.0);

    /// <summary>The fill of a brush: a color (S flag) or a brush object.</summary>
    private MetaBrush? Brush(uint brush, int flags)
    {
        if ((flags & 0x8000) != 0)
        {
            return new SolidBrush(Argb(brush));
        }

        return Get((int)(brush & 0xFF)) is PlusBrush plus ? plus.ToMeta(WorldToForm, 72 / _dpi.X, _context) : null;
    }

    private void Fill(MetaPath path, uint brush, int flags, bool evenOdd)
    {
        if (Brush(brush, flags) is not { } fill)
        {
            return;
        }

        if (fill is ClippedBrush clipped)
        {
            // A path gradient fills only inside its boundary.
            _painter.Fill(path, clipped.Brush, evenOdd, ClipRegion.Combine(_state.Clip, ClipRegion.Of(clipped.Boundary), ClipRegion.And, _painter.Far));
            return;
        }

        _painter.Fill(path, fill, evenOdd, _state.Clip);
    }

    private void FillRegion(ClipRegion region, uint brush, int flags)
    {
        if (region.Bounds() is not { } box)
        {
            return;
        }

        var rect = MetaPath.Rectangle((box.Left, box.Bottom), (box.Right, box.Top));
        var saved = _state.Clip;
        _state.Clip = ClipRegion.Combine(saved, region, ClipRegion.And, _painter.Far);
        Fill(rect, brush, flags, evenOdd: false);
        _state.Clip = saved;
    }

    private void Draw(MetaPath path, int pen)
    {
        if (Get(pen & 0xFF) is PlusPen plus && plus.ToStroke(WorldToForm, 72 / _dpi.X, UnitToPixels) is { } stroke)
        {
            _painter.Stroke(path, stroke, _state.Clip);
            plus.DrawCaps(path, stroke, _painter, _state.Clip);
        }
    }

    private void FillRects(int flags, MetaReader r)
    {
        var brush = r.U32();
        var count = r.U32();
        if (count > r.Remaining / ((flags & 0x4000) != 0 ? 8 : 16))
        {
            throw new MetafileFormatException("an EMF+ record has more rectangles than it holds");
        }

        _context.Count(count);
        var path = new MetaPath();
        for (var i = 0; i < count; i++)
        {
            var rect = ReadRect(r, flags);
            path.Append(Polygon([rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft], close: true));
        }

        Fill(path, brush, flags, evenOdd: false);
    }

    private void DrawRects(int flags, MetaReader r)
    {
        var count = r.U32();
        if (count > r.Remaining / ((flags & 0x4000) != 0 ? 8 : 16))
        {
            throw new MetafileFormatException("an EMF+ record has more rectangles than it holds");
        }

        _context.Count(count);
        var path = new MetaPath();
        for (var i = 0; i < count; i++)
        {
            var rect = ReadRect(r, flags);
            path.Append(Polygon([rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft], close: true));
        }

        Draw(path, flags & 0xFF);
    }

    // ----- Images -----

    private void DrawImage(bool points, int flags, MetaReader r)
    {
        r.U32(); // Image attributes.
        r.I32(); // The source rectangle's unit: pixels of the image, as for other players.
        var src = Rect(r, compressed: false);
        (double X, double Y) a, b, c;
        if (points)
        {
            var count = r.U32();
            if (count != 3)
            {
                return;
            }

            var p = Points(r, 3, flags);
            (a, b, c) = (p[0], p[1], p[2]);
        }
        else
        {
            var dest = ReadRect(r, flags);
            (a, b, c) = (dest.TopLeft, dest.TopRight, dest.BottomLeft);
        }

        if (Get(flags & 0xFF) is not PlusImage image)
        {
            return;
        }

        var m = WorldToForm;
        var (fa, fb, fc) = (m.Apply(a.X, a.Y), m.Apply(b.X, b.Y), m.Apply(c.X, c.Y));
        var (w, h) = (image.Image.PixelWidth, image.Image.PixelHeight);
        if (image.Metafile || src.Width <= 0 || src.Height <= 0)
        {
            src = new RectF(0, 0, w, h);
        }

        // The source rectangle's corners go to a, b and c; the whole image is placed accordingly and clipped to them.
        var ux = ((fb.X - fa.X) / src.Width, (fb.Y - fa.Y) / src.Width);
        var vy = ((fc.X - fa.X) / src.Height, (fc.Y - fa.Y) / src.Height);
        var origin = (fa.X - (ux.Item1 * src.X) - (vy.Item1 * src.Y), fa.Y - (ux.Item2 * src.X) - (vy.Item2 * src.Y));
        var unit = new SvgMatrix(ux.Item1 * w, ux.Item2 * w, -vy.Item1 * h, -vy.Item2 * h, origin.Item1 + (vy.Item1 * h), origin.Item2 + (vy.Item2 * h));
        var whole = src.X <= 0 && src.Y <= 0 && src.X + src.Width >= w && src.Y + src.Height >= h;
        _painter.Image(image.Image, unit, _state.Clip, whole ? null : MetaPath.Parallelogram(fa, fb, fc));
    }

    // ----- Text -----

    private void DrawString(int flags, MetaReader r)
    {
        var brush = r.U32();
        var formatId = r.U32();
        var length = r.U32();
        var layout = Rect(r, compressed: false);
        if (length > r.Remaining / 2)
        {
            throw new MetafileFormatException("an EMF+ string is longer than its record");
        }

        _context.Count(length);
        var text = Encoding.Unicode.GetString(r.Bytes((int)length * 2));
        if (Get(flags & 0xFF) is not PlusFont font || Brush(brush, flags) is not { } fill)
        {
            return;
        }

        var format = Get((int)(formatId & 0xFF)) as PlusStringFormat;
        var color = Color(fill);
        var (frame, fonts, em) = TextFrame(font, layout.X, layout.Y);
        if (em <= 0)
        {
            return;
        }

        var primary = fonts[0];
        var lineHeight = (primary.Ascent - primary.Descent + primary.LineGap) * em;
        var wrap = layout.Width > 0 && (format is null || (format.Flags & 0x1000) == 0);
        var margin = format is null ? em / 6 : format.LeadingMargin * em;
        var available = layout.Width - (2 * margin);
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (!wrap || available <= 0)
            {
                lines.Add(paragraph);
                continue;
            }

            Wrap(paragraph, fonts, em, available, lines);
        }

        var total = lines.Count * lineHeight;
        var alignment = format?.Alignment ?? 0;
        var lineAlignment = format?.LineAlignment ?? 0;
        var top = layout.Height > 0 ? lineAlignment switch { 1 => (layout.Height - total) / 2, 2 => layout.Height - total, _ => 0 } : 0;

        // Local space: x right, y up, origin at the layout rectangle's top left (in world units scaled by the frame).
        for (var i = 0; i < lines.Count; i++)
        {
            var clusters = Clusters(lines[i], fonts, em);
            var width = clusters.Sum(run => run.Clusters.Sum(c => c.Advance));
            var x = layout.Width > 0 ? alignment switch { 1 => (layout.Width - width) / 2, 2 => layout.Width - width - margin, _ => margin } : alignment switch { 1 => -width / 2, 2 => -width - margin, _ => margin };
            var baseline = -(top + (i * lineHeight) + (primary.Ascent * em));
            var runs = clusters.Select(run => (run.Font, run.X + x, run.Clusters)).ToList();
            _painter.Text(SvgMatrix.Translate(0, baseline).Then(frame), runs, em, color, _state.Clip);
            if ((font.Style & 0xC) != 0)
            {
                Decorations(font, primary, em, SvgMatrix.Translate(x, baseline).Then(frame), width, color);
            }
        }
    }

    private void DrawDriverString(int flags, MetaReader r)
    {
        var brush = r.U32();
        var options = r.U32();
        var matrixPresent = r.U32();
        var count = r.U32();
        if (count > r.Remaining / 10)
        {
            throw new MetafileFormatException("an EMF+ string is longer than its record");
        }

        _context.Count(count);
        var glyphs = new char[count];
        for (var i = 0; i < count; i++)
        {
            glyphs[i] = (char)r.U16();
        }

        var positions = new (double X, double Y)[count];
        for (var i = 0; i < count; i++)
        {
            positions[i] = (r.F32(), r.F32());
        }

        var transform = matrixPresent != 0 && r.Remaining >= 24 ? Matrix(r) : SvgMatrix.Identity;
        if ((options & 1) == 0)
        {
            _context.Report("EMF+ text stored as glyph numbers is left out.");
            return;
        }

        if (count == 0 || Get(flags & 0xFF) is not PlusFont font || Brush(brush, flags) is not { } fill)
        {
            return;
        }

        var color = Color(fill);
        var text = new string(glyphs);
        var realized = (options & 4) != 0;

        // Runs of glyphs on one baseline; each glyph at its position (or, with realized advances, after the previous).
        var start = 0;
        while (start < text.Length)
        {
            var end = start + 1;
            while (end < text.Length && (realized || Math.Abs(positions[end].Y - positions[start].Y) < 1e-3))
            {
                end++;
            }

            var origin = positions[start];
            var (frame, fonts, em) = TextFrame(font, origin.X, origin.Y, transform);
            if (em <= 0)
            {
                return;
            }

            var runs = new List<(PdfFont Font, double X, List<(string Text, double Advance)> Clusters)>();
            var pen = 0.0;
            for (var i = start; i < end; i++)
            {
                var chosen = Choose(fonts, text[i]);
                var natural = chosen.CanMeasure ? chosen.MeasureText(text[i].ToString(), em) : em / 2;
                var advance = !realized && i + 1 < end ? positions[i + 1].X - positions[i].X : natural;
                if (runs.Count == 0 || !ReferenceEquals(runs[^1].Font, chosen))
                {
                    runs.Add((chosen, pen, []));
                }

                runs[^1].Clusters.Add((text[i].ToString(), advance));
                pen += advance;
            }

            _painter.Text(frame, runs, em, color, _state.Clip);
            start = end;
        }
    }

    /// <summary>
    /// The space text is drawn in (x right, y up, origin at a point in world coordinates), the fonts, and the em size
    /// in that space's units.
    /// </summary>
    private (SvgMatrix Frame, IReadOnlyList<PdfFont> Fonts, double Em) TextFrame(PlusFont font, double x, double y, SvgMatrix? glyphTransform = null)
    {
        var fonts = _context.Fonts(font.Family, (font.Style & 1) != 0, (font.Style & 2) != 0);
        var m = WorldToForm;

        // World space has y down: flip the local space so glyphs stand upright.
        var flip = ((m.A * m.D) - (m.B * m.C)) < 0 ? -1 : 1;
        var local = new SvgMatrix(1, 0, 0, flip, 0, 0);
        if (glyphTransform is { } g && !g.IsIdentity)
        {
            local = local.Then(new SvgMatrix(g.A, g.B, g.C, g.D, 0, 0));
        }

        var frame = local.Then(SvgMatrix.Translate(x, y)).Then(m);
        return (frame, fonts, font.WorldEm(this));
    }

    private void Decorations(PlusFont font, PdfFont primary, double em, SvgMatrix frame, double width, PdfColor color)
    {
        foreach (var (on, position, thickness) in new[] { ((font.Style & 4) != 0, primary.UnderlinePosition, primary.UnderlineThickness), ((font.Style & 8) != 0, primary.StrikeoutPosition, primary.StrikeoutThickness) })
        {
            if (on)
            {
                var (y0, y1) = ((position - (thickness / 2)) * em, (position + (thickness / 2)) * em);
                var line = new MetaPath().MoveTo(frame.Apply(0, y0)).LineTo(frame.Apply(width, y0)).LineTo(frame.Apply(width, y1)).LineTo(frame.Apply(0, y1)).ClosePath();
                _painter.Fill(line, new SolidBrush(color), false, _state.Clip);
            }
        }
    }

    private static PdfColor Color(MetaBrush brush) => brush switch
    {
        SolidBrush solid => solid.Color,
        HatchBrush hatch => hatch.Color,
        GradientBrush gradient when gradient.Stops.Count > 0 => gradient.Stops[0].Color,
        ClippedBrush clipped => Color(clipped.Brush),
        PatternBrush pattern => pattern.Average,
        _ => PdfColor.Black,
    };

    /// <summary>Text as runs of clusters in the fonts that have them, with their natural advances.</summary>
    private static List<(PdfFont Font, double X, List<(string Text, double Advance)> Clusters)> Clusters(string text, IReadOnlyList<PdfFont> fonts, double em)
    {
        var runs = new List<(PdfFont Font, double X, List<(string Text, double Advance)> Clusters)>();
        var pen = 0.0;
        for (var i = 0; i < text.Length; i++)
        {
            var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var cluster = length == 1 && char.IsSurrogate(text[i]) ? ((char)0xFFFD).ToString() : text.Substring(i, length);
            var codePoint = char.ConvertToUtf32(cluster, 0);
            i += length - 1;
            if (codePoint < 0x20)
            {
                continue;
            }

            var chosen = Choose(fonts, codePoint);
            var advance = chosen.CanMeasure ? chosen.MeasureText(cluster, em) : em / 2;
            if (runs.Count == 0 || !ReferenceEquals(runs[^1].Font, chosen))
            {
                runs.Add((chosen, pen, []));
            }

            runs[^1].Clusters.Add((cluster, advance));
            pen += advance;
        }

        return runs;
    }

    private static PdfFont Choose(IReadOnlyList<PdfFont> fonts, int codePoint) => GdiDevice.Choose(fonts, codePoint);

    /// <summary>Breaks a paragraph into lines that fit a width, at spaces (a word longer than the width gets a line of its own).</summary>
    private static void Wrap(string paragraph, IReadOnlyList<PdfFont> fonts, double em, double width, List<string> lines)
    {
        var words = paragraph.Split(' ');
        var line = new StringBuilder();
        foreach (var word in words)
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Width(candidate, fonts, em) > width * 1.0001)
            {
                lines.Add(line.ToString());
                line.Clear().Append(word);
            }
            else
            {
                line.Clear().Append(candidate);
            }
        }

        lines.Add(line.ToString());
    }

    private static double Width(string text, IReadOnlyList<PdfFont> fonts, double em) =>
        Clusters(text, fonts, em).Sum(run => run.Clusters.Sum(c => c.Advance));

    // ----- Objects -----

    private object? Get(int id) => id is >= 0 and < MaxObjects ? _objects[id] : null;

    /// <summary>An object record: whole, or in parts (continued) that are joined first.</summary>
    private void ObjectRecord(int flags, MetaReader r)
    {
        var id = flags & 0xFF;
        var type = (flags >> 8) & 0x7F;
        var continued = (flags & 0x8000) != 0;
        if (id >= MaxObjects)
        {
            return;
        }

        if (continued)
        {
            var total = r.U32();
            if (total > ResourceLimits.Current.MaxResourceBytes || total > int.MaxValue)
            {
                throw new MetafileFormatException("an EMF+ object is too large");
            }

            if (_pending is not { } p || p.Id != id || p.Type != type)
            {
                _pending = (id, type, total, new List<byte>((int)Math.Min(total, 1 << 20)));
            }

            _pending.Value.Data.AddRange(r.Bytes(r.Remaining));
            if (_pending.Value.Data.Count > total)
            {
                _pending = null;
                throw new MetafileFormatException("an EMF+ object is longer than it says");
            }

            return;
        }

        byte[] bytes;
        if (_pending is { } pending && pending.Id == id && pending.Type == type)
        {
            pending.Data.AddRange(r.Bytes(r.Remaining));
            bytes = [.. pending.Data];
            _pending = null;
        }
        else
        {
            bytes = r.Bytes(r.Remaining).ToArray();
        }

        var reader = new MetaReader(bytes, 0, bytes.Length);
        _objects[id] = type switch
        {
            1 => PlusBrush.Read(reader, this),
            2 => PlusPen.Read(reader, this),
            3 => PlusPath.Read(reader, this),
            4 => PlusRegion.Read(reader, this, 0),
            5 => PlusImage.Read(reader, this),
            6 => PlusFont.Read(reader),
            7 => PlusStringFormat.Read(reader),
            _ => null,
        };
    }

    /// <summary>A region in form space for the current transform.</summary>
    private ClipRegion? RegionToForm(PlusRegion region) => region.ToForm(WorldToForm, _painter.Far);

    // Shared with the object types.
    internal MetafileContext Context => _context;

    internal (double X, double Y) Dpi => _dpi;

    internal SvgMatrix CurrentWorldToForm => WorldToForm;
}

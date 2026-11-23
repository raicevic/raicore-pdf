using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Svg;

namespace RaiCore.Pdf.Metafiles;

/// <summary>The device context of a WMF or EMF picture: what SaveDC saves.</summary>
internal sealed class DcState
{
    public int MapMode { get; set; } = 1;

    public (double X, double Y) WindowOrg { get; set; }

    public (double X, double Y) WindowExt { get; set; } = (1, 1);

    public (double X, double Y) ViewportOrg { get; set; }

    public (double X, double Y) ViewportExt { get; set; } = (1, 1);

    public SvgMatrix World { get; set; } = SvgMatrix.Identity;

    public GdiPen Pen { get; set; } = GdiPen.Black;

    public GdiBrush Brush { get; set; } = GdiBrush.White;

    public GdiFont Font { get; set; } = GdiFont.Default;

    public GdiPalette? Palette { get; set; }

    public PdfColor TextColor { get; set; } = PdfColor.Black;

    public PdfColor BackgroundColor { get; set; } = PdfColor.White;

    /// <summary>1 transparent, 2 opaque.</summary>
    public int BackgroundMode { get; set; } = 2;

    /// <summary>1 alternate (even-odd), 2 winding (nonzero).</summary>
    public int PolyFillMode { get; set; } = 1;

    public int TextAlign { get; set; }

    public int Rop2 { get; set; } = 13;

    /// <summary>1 counterclockwise (the default), 2 clockwise.</summary>
    public int ArcDirection { get; set; } = 1;

    public double MiterLimit { get; set; } = 10;

    public (double X, double Y) Current { get; set; }

    public ClipRegion? Clip { get; set; }

    public DcState Clone() => (DcState)MemberwiseClone();
}

/// <summary>
/// The drawing operations shared by WMF and EMF: GDI's shapes, paths, clipping, text and bitmaps on logical
/// coordinates, mapped to the form's space and drawn with a <see cref="MetaPainter"/>. The players decode records into
/// these calls.
/// </summary>
internal abstract class GdiDevice(MetafileContext context, MetaPainter painter)
{
    private const double Kappa = 0.5522847498307936;
    private const int MaxSavedStates = 1024;

    private readonly Stack<DcState> _saved = new();
    private readonly Dictionary<(MonoBits, PdfColor, PdfColor), PatternBrush> _monoPatterns = [];
    private SvgMatrix _toForm;
    private bool _mapDirty = true;
    private MetaPath? _path;
    private bool _recording;
    private bool _widened;

    protected MetafileContext Context { get; } = context;

    protected MetaPainter Painter { get; } = painter;

    protected DcState State { get; private set; } = new();

    /// <summary>The matrix from logical coordinates to form space for the current state.</summary>
    protected abstract SvgMatrix PageToForm(DcState state);

    /// <summary>The size of a device pixel in form units (the width of a cosmetic pen).</summary>
    protected abstract double DevicePixel { get; }

    /// <summary>The matrix from logical coordinates to form space.</summary>
    protected SvgMatrix ToForm
    {
        get
        {
            if (_mapDirty)
            {
                _toForm = PageToForm(State);
                _mapDirty = false;
            }

            return _toForm;
        }
    }

    /// <summary>Notes a change of the mapping (mode, window, viewport or world transform).</summary>
    protected void MappingChanged() => _mapDirty = true;

    protected (double X, double Y) Map(double x, double y) => ToForm.Apply(x, y);

    /// <summary>How much the mapping scales lengths, on average.</summary>
    protected double Scale
    {
        get
        {
            var m = ToForm;
            var scale = Math.Sqrt(Math.Abs((m.A * m.D) - (m.B * m.C)));
            return double.IsFinite(scale) ? scale : 0;
        }
    }

    // ----- State -----

    protected void SaveDC()
    {
        if (_saved.Count < MaxSavedStates)
        {
            _saved.Push(State.Clone());
        }
    }

    protected void RestoreDC(int which)
    {
        // Negative: that many back; positive: the state saved as number "which" (from 1).
        var pops = which < 0 ? -which : _saved.Count - which + 1;
        if (pops <= 0 || pops > _saved.Count)
        {
            return;
        }

        DcState state = State;
        for (var i = 0; i < pops; i++)
        {
            state = _saved.Pop();
        }

        State = state;
        MappingChanged();
    }

    protected void SetMapMode(int mode)
    {
        State.MapMode = mode is >= 1 and <= 8 ? mode : 1;
        MappingChanged();
    }

    protected void SetWindowOrg(double x, double y)
    {
        State.WindowOrg = (x, y);
        MappingChanged();
    }

    protected void SetWindowExt(double x, double y)
    {
        if (x != 0 && y != 0)
        {
            State.WindowExt = (x, y);
            MappingChanged();
        }
    }

    protected void SetViewportOrg(double x, double y)
    {
        State.ViewportOrg = (x, y);
        MappingChanged();
    }

    protected void SetViewportExt(double x, double y)
    {
        if (x != 0 && y != 0)
        {
            State.ViewportExt = (x, y);
            MappingChanged();
        }
    }

    protected void ScaleWindowExt(double xNum, double xDenom, double yNum, double yDenom)
    {
        if (xDenom != 0 && yDenom != 0)
        {
            SetWindowExt(State.WindowExt.X * xNum / xDenom, State.WindowExt.Y * yNum / yDenom);
        }
    }

    protected void ScaleViewportExt(double xNum, double xDenom, double yNum, double yDenom)
    {
        if (xDenom != 0 && yDenom != 0)
        {
            SetViewportExt(State.ViewportExt.X * xNum / xDenom, State.ViewportExt.Y * yNum / yDenom);
        }
    }

    protected void SetWorld(SvgMatrix world)
    {
        if (world.IsFinite && !world.IsSingular)
        {
            State.World = world;
            MappingChanged();
        }
    }

    // ----- Colors, pens and brushes -----

    /// <summary>A COLORREF: an RGB color, or (flag 1) an index into the selected palette.</summary>
    protected PdfColor Color((byte R, byte G, byte B, byte Flags) color)
    {
        if (color.Flags == 1)
        {
            var index = color.R | (color.G << 8);
            return State.Palette is { } palette && index < palette.Colors.Length ? palette.Colors[index] : PdfColor.Black;
        }

        return PdfColor.FromRgb(color.R, color.G, color.B);
    }

    /// <summary>The stroke of the current pen, or null when it draws nothing.</summary>
    protected MetaStroke? CurrentStroke() => Stroke(State.Pen);

    protected MetaStroke? Stroke(GdiPen pen)
    {
        if (pen.IsNull || State.Rop2 == 11)
        {
            return null;
        }

        if (MixColor(pen.Color) is not { } color)
        {
            return null;
        }

        var cosmetic = pen.Cosmetic || pen.Width <= 0;
        var width = cosmetic ? DevicePixel : pen.Width * Scale;
        var unit = cosmetic ? DevicePixel : width;
        double[]? dashes = (pen.Style & 0xF) switch
        {
            1 => cosmetic ? [18, 6] : [3, 1],
            2 => cosmetic ? [3, 3] : [1, 1],
            3 => cosmetic ? [9, 6, 3, 6] : [3, 1, 1, 1],
            4 => cosmetic ? [9, 3, 3, 3, 3, 3] : [3, 1, 1, 1, 1, 1],
            GdiPen.Alternate => [1, 1],
            GdiPen.UserStyle when pen.Dashes is { Length: > 0 } user => [.. user],
            _ => null,
        };
        if (dashes is not null)
        {
            var dashUnit = (pen.Style & 0xF) == GdiPen.UserStyle && !cosmetic ? Scale : unit;
            for (var i = 0; i < dashes.Length; i++)
            {
                dashes[i] *= dashUnit;
            }
        }

        var cap = (pen.Style & 0x0F00) switch { 0x100 => LineCap.Square, 0x200 => LineCap.Butt, _ => LineCap.Round };
        var join = (pen.Style & 0xF000) switch { 0x1000 => LineJoin.Bevel, 0x2000 => LineJoin.Miter, _ => LineJoin.Round };
        if (dashes is not null && cosmetic)
        {
            cap = LineCap.Butt;
        }

        return new MetaStroke(color, width, dashes, cap, join, State.MiterLimit);
    }

    /// <summary>The color a pen of <paramref name="color"/> paints with the current ROP2 mix mode, or null for none.</summary>
    private PdfColor? MixColor(PdfColor color)
    {
        switch (State.Rop2)
        {
            case 13:
                return color;
            case 1:
                return PdfColor.Black;
            case 16:
                return PdfColor.White;
            case 11:
                return null;
            case 4:
                return new PdfColor(1 - color.Red, 1 - color.Green, 1 - color.Blue, color.Alpha);
            case 6:
                Context.Report("Inverted lines (the ROP2 mix mode R2_NOT) are left out.");
                return null;
            default:
                Context.Report("Lines and fills that mix with what is under them (ROP2 mix modes) are drawn opaque.");
                return color;
        }
    }

    /// <summary>The fill of the current brush, or null when it fills nothing.</summary>
    protected MetaBrush? CurrentBrush() => Brush(State.Brush);

    protected MetaBrush? Brush(GdiBrush brush)
    {
        if (brush.IsNull || State.Rop2 == 11)
        {
            return null;
        }

        if (brush.Mono is { } mono)
        {
            var key = (mono, State.TextColor, State.BackgroundColor);
            if (!_monoPatterns.TryGetValue(key, out var colored))
            {
                colored = mono.Colorize(State.TextColor, State.BackgroundColor);
                _monoPatterns[key] = colored;
            }

            return colored with { PixelSize = DevicePixel };
        }

        if (brush.Pattern is { } pattern)
        {
            return pattern with { PixelSize = DevicePixel };
        }

        if (MixColor(brush.Color) is not { } color)
        {
            return null;
        }

        return brush.Style == GdiBrush.Hatched
            ? new HatchBrush(Math.Clamp(brush.Hatch, 0, 5), color, State.BackgroundMode == 2 ? State.BackgroundColor : null, 8 * DevicePixel, DevicePixel)
            : new SolidBrush(color);
    }

    // ----- Shapes -----

    /// <summary>Draws a closed shape with the pen and brush, or adds it to the path being recorded.</summary>
    private void Closed(MetaPath shape)
    {
        if (_recording)
        {
            _path!.Append(shape);
            return;
        }

        Painter.FillAndStroke(shape, CurrentBrush(), CurrentStroke(), State.PolyFillMode == 1, State.Clip);
    }

    /// <summary>Draws an open shape with the pen, or adds it to the path being recorded.</summary>
    private void Open(MetaPath shape)
    {
        if (_recording)
        {
            _path!.Append(shape);
            return;
        }

        if (CurrentStroke() is { } stroke)
        {
            Painter.Stroke(shape, stroke, State.Clip);
        }
    }

    protected void MoveTo(double x, double y)
    {
        State.Current = (x, y);
        if (_recording)
        {
            _path!.MoveTo(Map(x, y));
        }
    }

    protected void LineTo(double x, double y)
    {
        var from = State.Current;
        State.Current = (x, y);
        if (_recording)
        {
            if (!_path!.HasCurrentPoint)
            {
                _path.MoveTo(Map(from.X, from.Y));
            }

            _path.LineTo(Map(x, y));
            return;
        }

        Open(new MetaPath().MoveTo(Map(from.X, from.Y)).LineTo(Map(x, y)));
    }

    protected void Polyline(ReadOnlySpan<(double X, double Y)> points)
    {
        if (points.Length < 2)
        {
            return;
        }

        var path = new MetaPath();
        foreach (var (x, y) in points)
        {
            path.LineTo(Map(x, y));
        }

        Open(path);
    }

    protected void PolylineTo(ReadOnlySpan<(double X, double Y)> points)
    {
        if (points.IsEmpty)
        {
            return;
        }

        if (_recording)
        {
            foreach (var (x, y) in points)
            {
                LineTo(x, y);
            }

            return;
        }

        var path = new MetaPath().MoveTo(Map(State.Current.X, State.Current.Y));
        foreach (var (x, y) in points)
        {
            path.LineTo(Map(x, y));
        }

        State.Current = points[^1];
        Open(path);
    }

    protected void Polygon(ReadOnlySpan<(double X, double Y)> points)
    {
        if (points.Length < 2)
        {
            return;
        }

        var path = new MetaPath();
        foreach (var (x, y) in points)
        {
            path.LineTo(Map(x, y));
        }

        Closed(path.ClosePath());
    }

    protected void PolyPolygon(ReadOnlySpan<(double X, double Y)> points, ReadOnlySpan<int> counts)
    {
        var path = new MetaPath();
        var start = 0;
        foreach (var count in counts)
        {
            if (count < 0 || (long)start + count > points.Length)
            {
                break;
            }

            if (count >= 2)
            {
                path.MoveTo(Map(points[start].X, points[start].Y));
                for (var i = start + 1; i < start + count; i++)
                {
                    path.LineTo(Map(points[i].X, points[i].Y));
                }

                path.ClosePath();
            }

            start += count;
        }

        if (!path.IsEmpty)
        {
            Closed(path);
        }
    }

    protected void PolyPolyline(ReadOnlySpan<(double X, double Y)> points, ReadOnlySpan<int> counts)
    {
        var path = new MetaPath();
        var start = 0;
        foreach (var count in counts)
        {
            if (count < 0 || (long)start + count > points.Length)
            {
                break;
            }

            if (count >= 2)
            {
                path.MoveTo(Map(points[start].X, points[start].Y));
                for (var i = start + 1; i < start + count; i++)
                {
                    path.LineTo(Map(points[i].X, points[i].Y));
                }
            }

            start += count;
        }

        if (!path.IsEmpty)
        {
            Open(path);
        }
    }

    protected void PolyBezier(ReadOnlySpan<(double X, double Y)> points)
    {
        if (points.Length < 4)
        {
            return;
        }

        var path = new MetaPath().MoveTo(Map(points[0].X, points[0].Y));
        for (var i = 1; i + 2 < points.Length; i += 3)
        {
            path.CurveTo(Map(points[i].X, points[i].Y), Map(points[i + 1].X, points[i + 1].Y), Map(points[i + 2].X, points[i + 2].Y));
        }

        Open(path);
    }

    protected void PolyBezierTo(ReadOnlySpan<(double X, double Y)> points)
    {
        if (points.Length < 3)
        {
            return;
        }

        var path = _recording ? _path! : new MetaPath();
        if (!path.HasCurrentPoint || !_recording)
        {
            path.MoveTo(Map(State.Current.X, State.Current.Y));
        }

        var i = 0;
        for (; i + 2 < points.Length; i += 3)
        {
            path.CurveTo(Map(points[i].X, points[i].Y), Map(points[i + 1].X, points[i + 1].Y), Map(points[i + 2].X, points[i + 2].Y));
        }

        State.Current = points[i - 1];
        if (!_recording)
        {
            Open(path);
        }
    }

    /// <summary>EMR_POLYDRAW: points with types (6 move, 2 line, 4 Bézier; 1 closes the figure).</summary>
    protected void PolyDraw(ReadOnlySpan<(double X, double Y)> points, ReadOnlySpan<byte> types)
    {
        var path = _recording ? _path! : new MetaPath();
        if (!path.HasCurrentPoint)
        {
            path.MoveTo(Map(State.Current.X, State.Current.Y));
        }

        for (var i = 0; i < points.Length && i < types.Length; i++)
        {
            var type = types[i] & ~1;
            if (type == 6)
            {
                path.MoveTo(Map(points[i].X, points[i].Y));
            }
            else if (type == 4 && i + 2 < points.Length)
            {
                path.CurveTo(Map(points[i].X, points[i].Y), Map(points[i + 1].X, points[i + 1].Y), Map(points[i + 2].X, points[i + 2].Y));
                i += 2;
            }
            else
            {
                path.LineTo(Map(points[i].X, points[i].Y));
            }

            State.Current = points[i];
            if ((types[i] & 1) != 0)
            {
                path.ClosePath();
            }
        }

        if (!_recording)
        {
            Open(path);
        }
    }

    protected void Rectangle(double left, double top, double right, double bottom)
    {
        var path = new MetaPath().MoveTo(Map(left, top)).LineTo(Map(right, top)).LineTo(Map(right, bottom)).LineTo(Map(left, bottom)).ClosePath();
        Closed(path);
    }

    protected void RoundRect(double left, double top, double right, double bottom, double width, double height)
    {
        (left, right) = (Math.Min(left, right), Math.Max(left, right));
        (top, bottom) = (Math.Min(top, bottom), Math.Max(top, bottom));
        var rx = Math.Min(Math.Abs(width) / 2, (right - left) / 2);
        var ry = Math.Min(Math.Abs(height) / 2, (bottom - top) / 2);
        if (rx <= 0 || ry <= 0)
        {
            Rectangle(left, top, right, bottom);
            return;
        }

        var path = new MetaPath().MoveTo(Map(left + rx, top)).LineTo(Map(right - rx, top));
        AddArc(path, right - rx, top + ry, rx, ry, -Math.PI / 2, Math.PI / 2);
        path.LineTo(Map(right, bottom - ry));
        AddArc(path, right - rx, bottom - ry, rx, ry, 0, Math.PI / 2);
        path.LineTo(Map(left + rx, bottom));
        AddArc(path, left + rx, bottom - ry, rx, ry, Math.PI / 2, Math.PI / 2);
        path.LineTo(Map(left, top + ry));
        AddArc(path, left + rx, top + ry, rx, ry, Math.PI, Math.PI / 2);
        Closed(path.ClosePath());
    }

    protected void Ellipse(double left, double top, double right, double bottom)
    {
        var (cx, cy, rx, ry) = ((left + right) / 2, (top + bottom) / 2, Math.Abs(right - left) / 2, Math.Abs(bottom - top) / 2);
        var path = new MetaPath().MoveTo(Map(cx + rx, cy));
        AddArc(path, cx, cy, rx, ry, 0, 2 * Math.PI);
        Closed(path.ClosePath());
    }

    /// <summary>Arc (0), chord (1) or pie (2) of the ellipse in a rectangle, between the radials through two points.</summary>
    protected void Arc(double left, double top, double right, double bottom, double xStart, double yStart, double xEnd, double yEnd, int kind, bool toCurrent = false)
    {
        var (cx, cy, rx, ry) = ((left + right) / 2, (top + bottom) / 2, Math.Abs(right - left) / 2, Math.Abs(bottom - top) / 2);
        if (rx <= 0 || ry <= 0)
        {
            return;
        }

        var t0 = Math.Atan2((yStart - cy) / ry, (xStart - cx) / rx);
        var t1 = Math.Atan2((yEnd - cy) / ry, (xEnd - cx) / rx);
        var sweep = Sweep(t0, t1);
        var start = (cx + (rx * Math.Cos(t0)), cy + (ry * Math.Sin(t0)));
        var end = (cx + (rx * Math.Cos(t0 + sweep)), cy + (ry * Math.Sin(t0 + sweep)));
        MetaPath path;
        if (toCurrent)
        {
            // ArcTo: a line from the current position to the arc's start, then the arc; the end becomes current.
            path = _recording ? _path! : new MetaPath();
            if (!path.HasCurrentPoint)
            {
                path.MoveTo(Map(State.Current.X, State.Current.Y));
            }

            path.LineTo(Map(start.Item1, start.Item2));
            AddArc(path, cx, cy, rx, ry, t0, sweep);
            State.Current = end;
            if (!_recording)
            {
                Open(path);
            }

            return;
        }

        path = new MetaPath();
        if (kind == 2)
        {
            path.MoveTo(Map(cx, cy)).LineTo(Map(start.Item1, start.Item2));
        }
        else
        {
            path.MoveTo(Map(start.Item1, start.Item2));
        }

        AddArc(path, cx, cy, rx, ry, t0, sweep);
        if (kind == 0)
        {
            Open(path);
        }
        else
        {
            Closed(path.ClosePath());
        }
    }

    /// <summary>EMR_ANGLEARC: a line from the current position to the arc's start, then the arc of a circle.</summary>
    protected void AngleArc(double x, double y, double radius, double startDegrees, double sweepDegrees)
    {
        // Angles are counterclockwise as seen, whatever the orientation of the logical space.
        var flip = Orientation() < 0 ? -1 : 1;
        var t0 = flip * startDegrees * Math.PI / 180;
        var sweep = flip * sweepDegrees * Math.PI / 180;
        var path = _recording ? _path! : new MetaPath();
        if (!path.HasCurrentPoint)
        {
            path.MoveTo(Map(State.Current.X, State.Current.Y));
        }

        path.LineTo(Map(x + (radius * Math.Cos(t0)), y + (radius * Math.Sin(t0))));
        AddArc(path, x, y, radius, radius, t0, sweep);
        State.Current = (x + (radius * Math.Cos(t0 + sweep)), y + (radius * Math.Sin(t0 + sweep)));
        if (!_recording)
        {
            Open(path);
        }
    }

    /// <summary>The sign of the mapping's determinant: negative when logical y points down on the page.</summary>
    protected double Orientation()
    {
        var m = ToForm;
        return (m.A * m.D) - (m.B * m.C);
    }

    /// <summary>
    /// The sweep from angle t0 to t1 (radians in logical space) in the arc direction, counterclockwise as seen by
    /// default; a full turn when they are equal.
    /// </summary>
    private double Sweep(double t0, double t1)
    {
        // In a space whose y points down on the page (determinant below zero), counterclockwise as seen is decreasing t.
        var seenCounterclockwise = State.ArcDirection != 2;
        var sign = (Orientation() < 0) == seenCounterclockwise ? -1 : 1;
        var delta = (t1 - t0) * sign;
        delta %= 2 * Math.PI;
        if (delta <= 1e-9)
        {
            delta += 2 * Math.PI;
        }

        return delta * sign;
    }

    /// <summary>Adds an elliptical arc (logical center and radii, parameter angles) as Béziers; continues an open figure.</summary>
    protected void AddArc(MetaPath path, double cx, double cy, double rx, double ry, double t0, double sweep) =>
        path.Arc(ToForm, cx, cy, rx, ry, t0, sweep);

    protected void SetPixel(double x, double y, PdfColor color)
    {
        var half = 0.5;
        var path = MetaPath.Parallelogram(Map(x - half, y - half), Map(x + half, y - half), Map(x - half, y + half));
        Painter.Fill(path, new SolidBrush(color), false, State.Clip);
    }

    // ----- Paths -----

    protected void BeginPath()
    {
        _path = new MetaPath();
        _recording = true;
        _widened = false;
    }

    protected void EndPath() => _recording = false;

    protected void AbortPath()
    {
        _path = null;
        _recording = false;
    }

    protected void CloseFigure()
    {
        if (_recording)
        {
            _path!.ClosePath();
        }
    }

    protected void WidenPath()
    {
        // The widened path is the outline of the stroke: filling it is drawing the stroke.
        _widened = true;
    }

    /// <summary>Fills (and strokes) the recorded path, which is then discarded.</summary>
    protected void PaintPath(bool fill, bool stroke)
    {
        if (_path is null || _recording)
        {
            return;
        }

        var path = _path;
        _path = null;
        if (_widened)
        {
            if (fill && CurrentStroke() is { } outline && CurrentBrush() is SolidBrush solid)
            {
                Painter.Stroke(path, outline with { Color = solid.Color }, State.Clip);
            }

            _widened = false;
            return;
        }

        Painter.FillAndStroke(path, fill ? CurrentBrush() : null, stroke ? CurrentStroke() : null, State.PolyFillMode == 1, State.Clip);
    }

    /// <summary>Combines the clip with the recorded path, which is then discarded.</summary>
    protected void SelectClipPath(int mode)
    {
        if (_path is null || _recording)
        {
            return;
        }

        var path = _path;
        _path = null;
        Clip(ClipRegion.Of(path, State.PolyFillMode == 1), mode);
    }

    // ----- Clipping -----

    protected void Clip(ClipRegion? region, int mode) => State.Clip = ClipRegion.Combine(State.Clip, region, mode, Painter.Far);

    protected void IntersectClipRect(double left, double top, double right, double bottom) =>
        Clip(ClipRegion.Of(MetaPath.Parallelogram(Map(left, top), Map(right, top), Map(left, bottom))), ClipRegion.And);

    protected void ExcludeClipRect(double left, double top, double right, double bottom) =>
        Clip(ClipRegion.Of(MetaPath.Parallelogram(Map(left, top), Map(right, top), Map(left, bottom))), ClipRegion.Diff);

    protected void OffsetClip(double dx, double dy)
    {
        var m = ToForm;
        State.Clip = State.Clip?.Offset((m.A * dx) + (m.C * dy), (m.B * dx) + (m.D * dy));
    }

    /// <summary>Fills a region (in form space) with a brush, inside the clip.</summary>
    protected void FillRegion(ClipRegion? region, MetaBrush? brush)
    {
        if (region is null || brush is null || region.Parts.Count == 0)
        {
            return;
        }

        var clip = State.Clip;
        for (var i = 1; i < region.Parts.Count; i++)
        {
            clip = ClipRegion.Combine(clip, ClipRegion.Of(region.Parts[i].Path, region.Parts[i].EvenOdd), ClipRegion.And, Painter.Far);
        }

        Painter.Fill(region.Parts[0].Path, brush, region.Parts[0].EvenOdd, clip);
    }

    /// <summary>Draws a region's outline with a brush's color, <paramref name="width"/> device pixels wide.</summary>
    protected void FrameRegion(ClipRegion? region, MetaBrush? brush, double width)
    {
        if (region is null || region.Parts.Count == 0 || brush is null)
        {
            return;
        }

        var color = brush is SolidBrush solid ? solid.Color : State.TextColor;
        Painter.Stroke(region.Parts[0].Path, new MetaStroke(color, Math.Max(1, width) * DevicePixel, null, LineCap.Butt, LineJoin.Miter, 10), State.Clip);
    }

    /// <summary>A rectangle in logical coordinates as a path in form space.</summary>
    protected MetaPath LogicalRectangle(double left, double top, double right, double bottom) =>
        MetaPath.Parallelogram(Map(left, top), Map(right, top), Map(left, bottom));

    // ----- Bitmaps -----

    /// <summary>The raster operations ([MS-WMF] TernaryRasterOperation) that need no source bitmap.</summary>
    protected static bool UsesSource(uint rop) => (((rop >> 2) ^ rop) & 0x330000u) != 0;

    /// <summary>Fills a rectangle by a raster operation without a source: the brush (PATCOPY), black or white.</summary>
    protected void PatBlt(double x, double y, double width, double height, uint rop)
    {
        MetaBrush? brush;
        switch (rop >> 16)
        {
            case 0xF0:
                brush = CurrentBrush();
                break;
            case 0x00:
                brush = new SolidBrush(PdfColor.Black);
                break;
            case 0xFF:
                brush = new SolidBrush(PdfColor.White);
                break;
            case 0xAA:
                return;
            case 0x55:
                Context.Report("Inverted areas (the raster operation DSTINVERT) are left out.");
                return;
            default:
                Context.Report("Raster operations that mix a pattern with what is under it are drawn as plain fills.");
                brush = CurrentBrush();
                break;
        }

        if (brush is not null)
        {
            Painter.Fill(LogicalRectangle(x, y, x + width, y + height), brush, false, State.Clip);
        }
    }

    /// <summary>
    /// Draws the source rectangle of an image (pixels, from its top left) into a destination rectangle in logical
    /// coordinates.
    /// </summary>
    protected void DrawBitmap(PdfImage image, double x, double y, double width, double height, double srcX, double srcY, double srcWidth, double srcHeight)
    {
        var (imageWidth, imageHeight) = (image.PixelWidth, image.PixelHeight);
        if (srcWidth <= 0 || srcHeight <= 0)
        {
            (srcX, srcY, srcWidth, srcHeight) = (0, 0, imageWidth, imageHeight);
        }

        var (kx, ky) = (width / srcWidth, height / srcHeight);
        var (ix, iy) = (x - (srcX * kx), y - (srcY * ky));
        var (iw, ih) = (imageWidth * kx, imageHeight * ky);
        var unit = new SvgMatrix(iw, 0, 0, -ih, ix, iy + ih).Then(ToForm);
        var whole = srcX <= 0 && srcY <= 0 && srcX + srcWidth >= imageWidth && srcY + srcHeight >= imageHeight;
        Painter.Image(image, unit, State.Clip, whole ? null : LogicalRectangle(x, y, x + width, y + height));
    }

    // ----- Text -----

    /// <summary>
    /// Draws text at a reference point (logical) in the current font, color and alignment. <paramref name="dx"/> holds
    /// each UTF-16 unit's advance in logical units, when the record has them. With <paramref name="glyphIndices"/>
    /// (ETO_GLYPH_INDEX) the units are glyph numbers: drawn only in a font of the picture's font name, as they belong
    /// to the font the picture was made with.
    /// </summary>
    protected void DrawText(double x, double y, string text, double[]? dx, int options, (double Left, double Top, double Right, double Bottom)? rect, bool glyphIndices = false)
    {
        var font = State.Font;
        if (rect is { } r && (options & 0x2) != 0)
        {
            // ETO_OPAQUE: the rectangle is filled with the background color.
            Painter.Fill(LogicalRectangle(r.Left, r.Top, r.Right, r.Bottom), new SolidBrush(State.BackgroundColor), false, State.Clip);
        }

        if (text.Length == 0)
        {
            return;
        }

        if (_recording)
        {
            Context.Report("Text that is part of a path is drawn as text.");
        }

        if (!glyphIndices && (font.Charset == GdiFont.SymbolCharset || font.Face.Equals("Symbol", StringComparison.OrdinalIgnoreCase)))
        {
            text = SymbolFont.Map(text, font.Face);
        }

        if ((State.TextAlign & 1) != 0)
        {
            (x, y) = State.Current;
        }

        var fonts = Context.Fonts(font.Face, font.Bold, font.Italic);
        var primary = fonts[0];
        var height = Math.Abs(font.Height);
        var em = font.Height < 0 ? height
            : font.Height > 0 ? height / Math.Max(0.5, primary.Ascent - primary.Descent)
            : Scale > 0 ? 12 / Scale : 12;
        if (!double.IsFinite(em) || em <= 0)
        {
            return;
        }

        // Clusters (a character or a surrogate pair) with their advances, in fonts that have them; or glyphs.
        var runs = new List<(PdfFont Font, double X, List<(string Text, double Advance)> Clusters)>();
        List<PositionedGlyph>? glyphs = null;
        var pen = 0.0;
        if (glyphIndices)
        {
            if (primary is not OpenTypePdfFont { Font.EmbeddingRestricted: false } openType || !openType.FamilyName.Equals(font.Face, StringComparison.OrdinalIgnoreCase))
            {
                Context.Report("Text stored as glyph numbers (ETO_GLYPH_INDEX) is left out unless its font is available.");
                return;
            }

            glyphs = Glyphs(openType, text, dx, em, out pen);
            text = string.Empty;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var cluster = text.Substring(i, length);
            var codePoint = length == 2 ? char.ConvertToUtf32(text[i], text[i + 1]) : char.IsSurrogate(text[i]) ? 0xFFFD : text[i];
            if (codePoint == 0xFFFD)
            {
                cluster = ((char)0xFFFD).ToString();
            }
            var chosen = Choose(fonts, codePoint);
            double advance;
            if (dx is not null)
            {
                advance = 0;
                for (var k = 0; k < length && i + k < dx.Length; k++)
                {
                    advance += dx[i + k];
                }
            }
            else
            {
                advance = chosen.CanMeasure ? chosen.MeasureText(cluster, em) : em / 2;
            }

            i += length - 1;
            if (codePoint < 0x20)
            {
                pen += dx is null ? 0 : advance;
                continue;
            }

            if (runs.Count == 0 || !ReferenceEquals(runs[^1].Font, chosen))
            {
                runs.Add((chosen, pen, []));
            }

            runs[^1].Clusters.Add((cluster, advance));
            pen += advance;
        }

        var total = pen;
        var start = (State.TextAlign & 6) switch { 6 => -total / 2, 2 => -total, _ => 0 };
        var baseline = (State.TextAlign & 24) switch { 24 => 0, 8 => -primary.Descent * em, _ => -primary.Ascent * em };

        // The text's own space: x along the baseline (turned by the escapement, counterclockwise as seen), y up.
        var angle = font.Escapement / 10 * Math.PI / 180;
        var (sin, cos) = Math.SinCos(angle);
        var down = Orientation() < 0;
        var u = (X: cos, Y: down ? -sin : sin);
        var v = (X: -sin, Y: down ? -cos : cos);
        var frame = new SvgMatrix(u.X, u.Y, v.X, v.Y, x, y).Then(ToForm);
        var shifted = SvgMatrix.Translate(start, baseline).Then(frame);

        if (State.BackgroundMode == 2 && total != 0)
        {
            var box = new MetaPath().MoveTo(shifted.Apply(0, primary.Descent * em)).LineTo(shifted.Apply(total, primary.Descent * em))
                .LineTo(shifted.Apply(total, primary.Ascent * em)).LineTo(shifted.Apply(0, primary.Ascent * em)).ClosePath();
            Painter.Fill(box, new SolidBrush(State.BackgroundColor), false, State.Clip);
        }

        var extra = rect is { } c && (options & 0x4) != 0 ? LogicalRectangle(c.Left, c.Top, c.Right, c.Bottom) : null;
        if (glyphs is not null)
        {
            Painter.Glyphs(shifted, glyphs, (OpenTypePdfFont)primary, em, State.TextColor, State.Clip, extra);
        }
        else
        {
            Painter.Text(shifted, runs, em, State.TextColor, State.Clip, extra);
        }

        foreach (var (on, position, thickness) in new[] { (font.Underline, primary.UnderlinePosition, primary.UnderlineThickness), (font.StrikeOut, primary.StrikeoutPosition, primary.StrikeoutThickness) })
        {
            if (on && total != 0)
            {
                var (y0, y1) = ((position - (thickness / 2)) * em, (position + (thickness / 2)) * em);
                var line = new MetaPath().MoveTo(shifted.Apply(0, y0)).LineTo(shifted.Apply(total, y0)).LineTo(shifted.Apply(total, y1)).LineTo(shifted.Apply(0, y1)).ClosePath();
                Painter.Fill(line, new SolidBrush(State.TextColor), false, State.Clip);
            }
        }

        if ((State.TextAlign & 1) != 0)
        {
            var move = (State.TextAlign & 6) switch { 6 => 0, 2 => -total, _ => total };
            State.Current = (x + (u.X * move), y + (u.Y * move));
        }
    }

    /// <summary>
    /// Glyph numbers as positioned glyphs: their advances (fractions of the em) from the record or the font, the text
    /// each stands for from the font's character map (for text extraction).
    /// </summary>
    private static List<PositionedGlyph> Glyphs(OpenTypePdfFont font, string ids, double[]? dx, double em, out double width)
    {
        var characters = Characters(font.Font);
        var glyphs = new List<PositionedGlyph>(ids.Length);
        width = 0;
        for (var i = 0; i < ids.Length; i++)
        {
            int id = ids[i];
            if (id >= font.Font.NumGlyphs)
            {
                id = 0;
            }

            var advance = dx is not null && i < dx.Length ? dx[i] : font.Font.GetAdvanceWidth(id) / (double)font.Font.UnitsPerEm * em;
            glyphs.Add(new PositionedGlyph(id, advance / em, Text: characters.TryGetValue(id, out var c) ? char.ConvertFromUtf32(c) : null));
            width += advance;
        }

        return glyphs;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Fonts.OpenType.OpenTypeFont, Dictionary<int, int>> CharacterMaps = [];

    /// <summary>The character each glyph of a font stands for (the lowest code point that maps to it).</summary>
    private static Dictionary<int, int> Characters(Fonts.OpenType.OpenTypeFont font) => CharacterMaps.GetValue(font, f =>
    {
        var map = new Dictionary<int, int>();
        foreach (var codePoint in f.GetMappedCodePoints().Order())
        {
            map.TryAdd(f.GetGlyphId(codePoint), codePoint);
        }

        return map;
    });

    /// <summary>The first font with a glyph for the character (and that may be embedded), else the first.</summary>
    internal static PdfFont Choose(IReadOnlyList<PdfFont> fonts, int codePoint)
    {
        foreach (var font in fonts)
        {
            if (font.HasGlyph(codePoint) && font is not OpenTypePdfFont { Font.EmbeddingRestricted: true })
            {
                return font;
            }
        }

        foreach (var font in fonts)
        {
            if (font is not OpenTypePdfFont { Font.EmbeddingRestricted: true })
            {
                return font;
            }
        }

        return PdfFont.Standard(StandardFont.Helvetica);
    }
}

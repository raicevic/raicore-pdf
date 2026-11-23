using System.Buffers.Binary;
using System.Text;
using RaiCore.Pdf.Images;
using RaiCore.Pdf.Svg;

namespace RaiCore.Pdf.Metafiles;

/// <summary>The header of an EMF file ([MS-EMF] 2.3.4.2): its frame and reference device.</summary>
/// <param name="Bounds">The drawing's bounds in device pixels (left, top, right, bottom).</param>
/// <param name="Frame">The picture's frame in 0.01 mm.</param>
/// <param name="DevicePixels">The reference device's size in pixels.</param>
/// <param name="DeviceMillimeters">The reference device's size in millimeters.</param>
/// <param name="Handles">The size of the object table.</param>
/// <param name="Records">The number of records the header announces.</param>
internal readonly record struct EmfHeader(
    (int Left, int Top, int Right, int Bottom) Bounds, (int Left, int Top, int Right, int Bottom) Frame,
    (int X, int Y) DevicePixels, (double X, double Y) DeviceMillimeters, int Handles, int Records)
{
    /// <summary>Whether the data starts with an EMF header (record type 1 with the " EMF" signature).</summary>
    public static bool IsEmf(ReadOnlySpan<byte> data) =>
        data.Length >= 88 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(data[40..]) == 0x464D4520;

    /// <summary>Reads the header; a header without a usable size is not a picture.</summary>
    /// <exception cref="ImageFormatException">The header is damaged.</exception>
    public static EmfHeader Read(byte[] data)
    {
        if (!IsEmf(data))
        {
            throw new ImageFormatException("Not an EMF file.");
        }

        var span = data.AsSpan();
        var size = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        if (size < 88 || size > data.Length)
        {
            throw new ImageFormatException("The EMF header is damaged.");
        }

        (int, int, int, int) Rect(int at) => (
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 4)),
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 8)), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 12)));
        var device = (BinaryPrimitives.ReadInt32LittleEndian(span[72..]), BinaryPrimitives.ReadInt32LittleEndian(span[76..]));
        (double, double) millimeters = (BinaryPrimitives.ReadInt32LittleEndian(span[80..]), BinaryPrimitives.ReadInt32LittleEndian(span[84..]));
        if (size >= 108)
        {
            // szlMicrometers: the same size, more precisely (some writers put other values there).
            var (mx, my) = (BinaryPrimitives.ReadInt32LittleEndian(span[100..]), BinaryPrimitives.ReadInt32LittleEndian(span[104..]));
            if (mx > 0 && my > 0 && Math.Abs((mx / 1000.0) - millimeters.Item1) <= 1 && Math.Abs((my / 1000.0) - millimeters.Item2) <= 1)
            {
                millimeters = (mx / 1000.0, my / 1000.0);
            }
        }

        if (device.Item1 <= 0 || device.Item2 <= 0 || millimeters.Item1 <= 0 || millimeters.Item2 <= 0)
        {
            // A missing reference device: a 96 dpi screen.
            device = (1024, 768);
            millimeters = (1024 * 25.4 / 96, 768 * 25.4 / 96);
        }

        var handles = BinaryPrimitives.ReadUInt16LittleEndian(span[56..]);
        var records = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(span[52..]), int.MaxValue);
        return new EmfHeader(Rect(8), Rect(24), device, millimeters, handles, records);
    }

    /// <summary>
    /// Whether the file has text stored as glyph numbers (ETO_GLYPH_INDEX), which cannot be drawn without the font
    /// it was made with: a WMF that carries this EMF then draws its own records, which have the characters.
    /// </summary>
    public static bool HasGlyphIndexText(byte[] data)
    {
        var offset = 0;
        for (var steps = 0; offset <= data.Length - 8 && steps < 1_000_000; steps++)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            if (size < 8 || size > data.Length - offset || type == 14)
            {
                break;
            }

            var options = type switch
            {
                83 or 84 when size >= 8 + 28 + 20 => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8 + 28 + 16)),
                108 when size >= 8 + 16 => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8 + 12)),
                _ => 0u,
            };
            if ((options & 0x10) != 0)
            {
                return true;
            }

            offset += (int)size;
        }

        return false;
    }

    /// <summary>Millimeters per device pixel.</summary>
    public (double X, double Y) PixelSize => (DeviceMillimeters.X / DevicePixels.X, DeviceMillimeters.Y / DevicePixels.Y);

    /// <summary>The picture's frame in millimeters (left, top, width, height): the frame, else the bounds.</summary>
    public (double Left, double Top, double Width, double Height) FrameMillimeters
    {
        get
        {
            var f = Frame;
            if (f.Right > f.Left && f.Bottom > f.Top)
            {
                return (f.Left / 100.0, f.Top / 100.0, ((long)f.Right - f.Left) / 100.0, ((long)f.Bottom - f.Top) / 100.0);
            }

            var b = Bounds;
            var (px, py) = PixelSize;
            if (b.Right > b.Left && b.Bottom > b.Top)
            {
                return (b.Left * px, b.Top * py, ((long)b.Right - b.Left) * px, ((long)b.Bottom - b.Top) * py);
            }

            throw new ImageFormatException("The EMF picture has no size.");
        }
    }
}

/// <summary>
/// Plays EMF records ([MS-EMF]) onto a <see cref="MetaPainter"/>. EMF+ records in comments ([MS-EMFPLUS]) are drawn by
/// an <see cref="EmfPlusPlayer"/> instead of the EMF records that duplicate them, unless they use what the EMF+ player
/// cannot draw: then a dual-mode file is drawn from its EMF records.
/// </summary>
internal sealed class EmfPlayer : GdiDevice
{
    private const uint EmfPlusSignature = 0x2B464D45;

    private readonly byte[] _data;
    private readonly EmfHeader _header;
    private readonly SvgMatrix _deviceToForm;
    private readonly double _devicePixel;
    private readonly object?[] _objects;
    private readonly (double Left, double Top, double Width, double Height) _frame;
    private EmfPlusPlayer? _plus;
    private bool _gdiEnabled = true;

    public EmfPlayer(MetafileContext context, MetaPainter painter, byte[] data, EmfHeader header)
        : base(context, painter)
    {
        _data = data;
        _header = header;
        _frame = header.FrameMillimeters;
        var (px, py) = header.PixelSize;
        const double PointsPerMillimeter = 72 / 25.4;

        // Device pixels to form points: millimeters from the frame's top left, y up.
        _deviceToForm = new SvgMatrix(px * PointsPerMillimeter, 0, 0, -py * PointsPerMillimeter,
            -_frame.Left * PointsPerMillimeter, painter.Height + (_frame.Top * PointsPerMillimeter));
        _devicePixel = Math.Sqrt(px * py) * PointsPerMillimeter;
        _objects = new object?[Math.Max(1, (int)header.Handles)];
    }

    protected override double DevicePixel => _devicePixel;

    protected override SvgMatrix PageToForm(DcState state)
    {
        var (sx, sy) = state.MapMode switch
        {
            2 => Metric(0.1),
            3 => Metric(0.01),
            4 => Metric(0.254),
            5 => Metric(0.0254),
            6 => Metric(25.4 / 1440),
            7 or 8 => (state.ViewportExt.X / state.WindowExt.X, state.ViewportExt.Y / state.WindowExt.Y),
            _ => (1.0, 1.0),
        };
        if (state.MapMode == 7)
        {
            var m = Math.Min(Math.Abs(sx), Math.Abs(sy));
            (sx, sy) = (Math.CopySign(m, sx), Math.CopySign(m, sy));
        }

        var page = new SvgMatrix(sx, 0, 0, sy, state.ViewportOrg.X - (state.WindowOrg.X * sx), state.ViewportOrg.Y - (state.WindowOrg.Y * sy));
        return state.World.Then(page).Then(_deviceToForm);

        (double, double) Metric(double millimeters)
        {
            var (px, py) = _header.PixelSize;
            return (millimeters / px, -millimeters / py);
        }
    }

    /// <summary>Plays the records; a damaged record ends the drawing (reported).</summary>
    public void Play()
    {
        var usePlus = PreferEmfPlus();
        var offset = 0;
        var records = 0;
        try
        {
            while (offset <= _data.Length - 8)
            {
                var type = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset));
                var size = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset + 4));
                if (size < 8 || size > _data.Length - offset)
                {
                    throw new MetafileFormatException($"record {records + 1} has an invalid size");
                }

                Context.Count(1);
                records++;
                var reader = new MetaReader(_data, offset, offset + (int)size) { Position = 8 };
                if (type == 14)
                {
                    break;
                }

                if (type == 70 && IsEmfPlus(reader))
                {
                    if (usePlus)
                    {
                        _plus ??= new EmfPlusPlayer(Context, Painter, _frame);
                        _gdiEnabled = _plus.Play(_data, offset + 16, offset + (int)size, _gdiEnabled);
                    }
                }
                else if (!usePlus || _gdiEnabled || type == 1)
                {
                    Record(type, reader);
                }

                offset += (int)size;
            }

            if (offset < _data.Length && offset > _data.Length - 8)
            {
                throw new MetafileFormatException($"it ends in the middle of record {records + 1}");
            }
        }
        catch (MetafileFormatException ex)
        {
            Context.Report($"The picture is damaged ({ex.Message}); it is drawn up to there.", damaged: true);
        }
        catch (MetafileLimitException ex)
        {
            Context.Report($"The picture is cut short: {ex.Message}.", damaged: true);
        }
        finally
        {
            Painter.Finish();
        }
    }

    /// <summary>Whether a comment record holds EMF+ records.</summary>
    private static bool IsEmfPlus(MetaReader reader) =>
        reader.Length >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(reader.At(12, 4)) == EmfPlusSignature;

    /// <summary>
    /// Whether to draw the EMF+ records rather than the EMF ones: when there are EMF+ records and either the file has
    /// nothing else (EMF+ only) or the EMF+ player can draw all of them.
    /// </summary>
    private bool PreferEmfPlus()
    {
        var offset = 0;
        var found = false;
        var dual = false;
        var supported = true;
        for (var steps = 0; offset <= _data.Length - 8 && steps < 1_000_000; steps++)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset + 4));
            if (size < 8 || size > _data.Length - offset || type == 14)
            {
                break;
            }

            if (type == 70 && size >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset + 12)) == EmfPlusSignature)
            {
                var (header, dualMode, ok) = EmfPlusPlayer.Scan(_data, offset + 16, offset + (int)size);
                if (header)
                {
                    found = true;
                    dual = dualMode;
                }

                supported &= ok;
            }

            offset += (int)size;
        }

        if (found && dual && !supported)
        {
            Context.Report("The picture's EMF+ records use features that are not supported; it is drawn from its EMF records.");
        }

        return found && (!dual || supported);
    }

    private void Record(uint type, MetaReader r)
    {
        switch (type)
        {
            case 1: // EMR_HEADER
                break;
            case 2 or 3 or 4 or 5 or 6 or 85 or 86 or 87 or 88 or 89:
                Poly(type, r);
                break;
            case 7 or 8 or 90 or 91:
                PolyPoly(type, r);
                break;
            case 9:
                SetWindowExt(r.I32(), r.I32());
                break;
            case 10:
                SetWindowOrg(r.I32(), r.I32());
                break;
            case 11:
                SetViewportExt(r.I32(), r.I32());
                break;
            case 12:
                SetViewportOrg(r.I32(), r.I32());
                break;
            case 15:
                SetPixel(r.I32(), r.I32(), Color(r.ColorRef()));
                break;
            case 17:
                SetMapMode(r.I32());
                break;
            case 18:
                State.BackgroundMode = r.I32();
                break;
            case 19:
                State.PolyFillMode = r.I32();
                break;
            case 20:
                State.Rop2 = r.I32();
                break;
            case 22:
                State.TextAlign = r.I32();
                break;
            case 24:
                State.TextColor = Color(r.ColorRef());
                break;
            case 25:
                State.BackgroundColor = Color(r.ColorRef());
                break;
            case 26:
                OffsetClip(r.I32(), r.I32());
                break;
            case 27:
                MoveTo(r.I32(), r.I32());
                break;
            case 28:
                // SetMetaRgn: the clip becomes the meta region, which later clips intersect; what is drawn stays clipped alike.
                break;
            case 29:
                ExcludeClipRect(r.I32(), r.I32(), r.I32(), r.I32());
                break;
            case 30:
                IntersectClipRect(r.I32(), r.I32(), r.I32(), r.I32());
                break;
            case 31:
                ScaleViewportExt(r.I32(), r.I32(), r.I32(), r.I32());
                break;
            case 32:
                ScaleWindowExt(r.I32(), r.I32(), r.I32(), r.I32());
                break;
            case 33:
                SaveDC();
                break;
            case 34:
                RestoreDC(r.I32());
                break;
            case 35:
                SetWorld(XForm(r));
                break;
            case 36:
            {
                var xform = XForm(r);
                switch (r.U32())
                {
                    case 1:
                        SetWorld(SvgMatrix.Identity);
                        break;
                    case 2:
                        SetWorld(xform.Then(State.World));
                        break;
                    case 3:
                        SetWorld(State.World.Then(xform));
                        break;
                    case 4:
                        SetWorld(xform);
                        break;
                }

                break;
            }

            case 37:
                Select(r.U32());
                break;
            case 38:
                CreatePen(r);
                break;
            case 39:
                CreateBrush(r);
                break;
            case 40:
                Delete(r.U32());
                break;
            case 41:
                AngleArc(r.I32(), r.I32(), r.U32(), r.F32(), r.F32());
                break;
            case 42:
                Ellipse(r.I32(), r.I32(), r.I32(), r.I32());
                break;
            case 43:
                Rectangle(r.I32(), r.I32(), r.I32(), r.I32());
                break;
            case 44:
                RoundRect(r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32());
                break;
            case 45 or 46 or 47 or 55:
                Arc(r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), type switch { 46 => 1, 47 => 2, _ => 0 }, toCurrent: type == 55);
                break;
            case 48:
            {
                var index = r.U32();
                if (Get(index) is GdiPalette palette)
                {
                    State.Palette = palette;
                }

                break;
            }

            case 49:
                CreatePalette(r);
                break;
            case 50:
                SetPaletteEntries(r);
                break;
            case 53:
                Context.Report("Flood fills are left out.");
                break;
            case 54:
                LineTo(r.I32(), r.I32());
                break;
            case 56 or 92:
                PolyDrawRecord(type == 92, r);
                break;
            case 57:
                State.ArcDirection = r.I32();
                break;
            case 58:
                State.MiterLimit = Math.Max(1, r.U32());
                break;
            case 59:
                BeginPath();
                break;
            case 60:
                EndPath();
                break;
            case 61:
                CloseFigure();
                break;
            case 62:
                PaintPath(fill: true, stroke: false);
                break;
            case 63:
                PaintPath(fill: true, stroke: true);
                break;
            case 64:
                PaintPath(fill: false, stroke: true);
                break;
            case 66:
                WidenPath();
                break;
            case 67:
                SelectClipPath(r.I32());
                break;
            case 68:
                AbortPath();
                break;
            case 71 or 72 or 74:
                RegionPaint(type, r);
                break;
            case 73:
                Context.Report("Inverted regions are left out.");
                break;
            case 75:
            {
                var length = r.U32();
                var mode = r.I32();
                var region = length == 0 ? null : Region(r, length, _deviceToForm);
                if (length == 0 && mode != ClipRegion.Copy)
                {
                    break;
                }

                Clip(region, mode);
                break;
            }

            case 76 or 77 or 78 or 79 or 114 or 116:
                Blt(type, r);
                break;
            case 80:
                SetDIBitsToDevice(r);
                break;
            case 81:
                StretchDIBits(r);
                break;
            case 82:
                CreateFont(r);
                break;
            case 83 or 84:
                r.Skip(16 + 12);
                TextRecord(r, wide: type == 84);
                break;
            case 93 or 94:
                CreatePatternBrush(r, mono: type == 93);
                break;
            case 95:
                ExtCreatePen(r);
                break;
            case 96 or 97:
            {
                r.Skip(16 + 12);
                var count = r.I32();
                for (var i = 0; i < count && r.Remaining >= 40; i++)
                {
                    TextRecord(r, wide: type == 97);
                }

                break;
            }

            case 108:
                SmallTextOut(r);
                break;
            case 118:
                GradientFill(r);
                break;
        }
    }

    private static SvgMatrix XForm(MetaReader r)
    {
        var (m11, m12, m21, m22, dx, dy) = (r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32());
        return new SvgMatrix(m11, m12, m21, m22, dx, dy);
    }

    // ----- Objects -----

    private object? Get(uint index)
    {
        if ((index & 0x80000000) != 0)
        {
            return Stock(index & 0x7FFFFFFF);
        }

        return index < _objects.Length ? _objects[index] : null;
    }

    /// <summary>The stock objects ([MS-EMF] StockObject).</summary>
    private static object? Stock(uint index) => index switch
    {
        0 => GdiBrush.White,
        1 => GdiBrush.Gray(0.75),
        2 => GdiBrush.Gray(0.5),
        3 => GdiBrush.Gray(0.25),
        4 => GdiBrush.Black,
        5 => GdiBrush.None,
        6 => GdiPen.White,
        7 => GdiPen.Black,
        8 => GdiPen.None,
        10 or 11 or 16 => GdiFont.Default with { Face = "Courier New" },
        12 or 13 or 14 or 17 => GdiFont.Default,
        18 => GdiBrush.White,
        19 => GdiPen.Black,
        _ => null,
    };

    private void Put(uint index, object value)
    {
        if (index > 0 && index < _objects.Length)
        {
            _objects[index] = value;
        }
    }

    private void Select(uint index)
    {
        switch (Get(index))
        {
            case GdiPen pen:
                State.Pen = pen;
                break;
            case GdiBrush brush:
                State.Brush = brush;
                break;
            case GdiFont font:
                State.Font = font;
                break;
            case GdiPalette palette:
                State.Palette = palette;
                break;
        }
    }

    private void Delete(uint index)
    {
        if (index < _objects.Length)
        {
            _objects[index] = null;
        }
    }

    private void CreatePen(MetaReader r)
    {
        var index = r.U32();
        var style = r.I32();
        var width = r.I32();
        r.I32();
        Put(index, new GdiPen(style, Math.Abs((double)width), Color(r.ColorRef())));
    }

    private void ExtCreatePen(MetaReader r)
    {
        var index = r.U32();
        r.Skip(16);
        var style = r.I32();
        var width = r.U32();
        var brushStyle = r.U32();
        var color = Color(r.ColorRef());
        r.U32();
        var count = r.U32();
        double[]? dashes = null;
        if ((style & 0xF) == GdiPen.UserStyle && count is > 0 and <= 64 && r.Remaining >= count * 4)
        {
            dashes = new double[count];
            for (var i = 0; i < count; i++)
            {
                dashes[i] = r.U32();
            }
        }

        var geometric = (style & 0x10000) != 0;
        if (brushStyle == GdiBrush.Null)
        {
            style = (style & ~0xF) | GdiPen.Null;
        }

        Put(index, new GdiPen(style, geometric ? width : 0, color, dashes, Cosmetic: !geometric));
    }

    private void CreateBrush(MetaReader r)
    {
        var index = r.U32();
        var style = (int)r.U32();
        var color = Color(r.ColorRef());
        var hatch = (int)r.U32();
        Put(index, new GdiBrush(style is 0 or 1 or 2 ? style : 0, color, hatch));
    }

    private void CreatePatternBrush(MetaReader r, bool mono)
    {
        var index = r.U32();
        var usage = (int)r.U32();
        var (offBmi, cbBmi, offBits, cbBits) = (r.U32(), r.U32(), r.U32(), r.U32());
        var image = Dib.Decode(Context, r.At(offBmi, cbBmi), r.At(offBits, cbBits), usage, State.Palette,
            mono ? (PdfColor.Black, PdfColor.White) : null, keepAlpha: false);
        Put(index, PatternBrush(image, mono));
    }

    /// <summary>A pattern brush of a bitmap (monochrome ones keep their bits), or gray when it cannot be read.</summary>
    internal static GdiBrush PatternBrush(DecodedImage? image, bool mono)
    {
        if (image is null || Dib.Pattern(image) is not { } pattern)
        {
            return new GdiBrush(GdiBrush.Solid, PdfColor.FromRgb(128, 128, 128));
        }

        return mono ? new GdiBrush(3, pattern.Average, 0, null, MonoBits.Of(pattern)) : new GdiBrush(5, pattern.Average, 0, pattern);
    }

    private void CreateFont(MetaReader r)
    {
        var index = r.U32();
        var height = r.I32();
        var width = r.I32();
        var escapement = r.I32();
        r.I32(); // Orientation.
        var weight = r.I32();
        var italic = r.U8() != 0;
        var underline = r.U8() != 0;
        var strikeOut = r.U8() != 0;
        var charset = r.U8();
        r.Skip(4);
        var face = Encoding.Unicode.GetString(r.Bytes(64));
        var nul = face.IndexOf('\0', StringComparison.Ordinal);
        face = (nul >= 0 ? face[..nul] : face).Trim();
        Put(index, new GdiFont(face.Length > 0 ? face : "Arial", height, width, escapement, weight, italic, underline, strikeOut, charset));
    }

    private void CreatePalette(MetaReader r)
    {
        var index = r.U32();
        r.U16();
        var count = r.U16();
        var colors = new PdfColor[Math.Min(count, r.Remaining / 4)];
        for (var i = 0; i < colors.Length; i++)
        {
            var (red, green, blue, _) = r.ColorRef();
            colors[i] = PdfColor.FromRgb(red, green, blue);
        }

        Put(index, new GdiPalette(colors));
    }

    private void SetPaletteEntries(MetaReader r)
    {
        var index = r.U32();
        var start = r.U32();
        var count = r.U32();
        if (Get(index) is not GdiPalette palette || start > 4096 || count > 4096)
        {
            return;
        }

        var colors = palette.Colors;
        if (start + count > colors.Length)
        {
            Array.Resize(ref colors, (int)(start + count));
        }

        for (var i = 0; i < count && r.Remaining >= 4; i++)
        {
            var (red, green, blue, _) = r.ColorRef();
            colors[start + i] = PdfColor.FromRgb(red, green, blue);
        }

        Put(index, new GdiPalette(colors));
    }

    // ----- Shapes -----

    private (double X, double Y)[] Points(MetaReader r, uint count, bool small)
    {
        if (count > r.Remaining / (small ? 4 : 8))
        {
            throw new MetafileFormatException("a record has more points than it holds");
        }

        Context.Count(count);
        var points = new (double X, double Y)[count];
        for (var i = 0; i < count; i++)
        {
            points[i] = small ? (r.I16(), r.I16()) : (r.I32(), r.I32());
        }

        return points;
    }

    private void Poly(uint type, MetaReader r)
    {
        r.Skip(16);
        var small = type >= 85;
        var points = Points(r, r.U32(), small);
        switch (small ? type - 83 : type)
        {
            case 2:
                PolyBezier(points);
                break;
            case 3:
                Polygon(points);
                break;
            case 4:
                Polyline(points);
                break;
            case 5:
                PolyBezierTo(points);
                break;
            case 6:
                PolylineTo(points);
                break;
        }
    }

    private void PolyPoly(uint type, MetaReader r)
    {
        r.Skip(16);
        var small = type >= 90;
        var polygons = r.U32();
        var total = r.U32();
        if (polygons > r.Remaining / 4)
        {
            throw new MetafileFormatException("a record has more polygons than it holds");
        }

        var counts = new int[polygons];
        for (var i = 0; i < polygons; i++)
        {
            counts[i] = (int)Math.Min(r.U32(), int.MaxValue);
        }

        var points = Points(r, total, small);
        if (type is 7 or 90)
        {
            PolyPolyline(points, counts);
        }
        else
        {
            PolyPolygon(points, counts);
        }
    }

    private void PolyDrawRecord(bool small, MetaReader r)
    {
        r.Skip(16);
        var count = r.U32();
        var points = Points(r, count, small);
        var types = r.Bytes((int)count).ToArray();
        PolyDraw(points, types);
    }

    // ----- Regions -----

    /// <summary>A region's rectangles (RgnData), mapped by a matrix, as one path.</summary>
    private ClipRegion Region(MetaReader r, uint length, SvgMatrix matrix)
    {
        var start = r.Position;
        if (length < 32 || length > r.Remaining)
        {
            throw new MetafileFormatException("a region is damaged");
        }

        r.Skip(8);
        var count = r.U32();
        r.Skip(4 + 16);
        if (count > (length - 32) / 16)
        {
            throw new MetafileFormatException("a region has more rectangles than it holds");
        }

        Context.Count(count);
        var path = new MetaPath();
        for (var i = 0; i < count; i++)
        {
            var (left, top, right, bottom) = (r.I32(), r.I32(), r.I32(), r.I32());
            path.MoveTo(matrix.Apply(left, top)).LineTo(matrix.Apply(right, top)).LineTo(matrix.Apply(right, bottom)).LineTo(matrix.Apply(left, bottom)).ClosePath();
        }

        r.Position = start + (int)length;
        return count == 0 ? ClipRegion.Empty : ClipRegion.Of(path);
    }

    private void RegionPaint(uint type, MetaReader r)
    {
        r.Skip(16);
        var length = r.U32();
        var brush = type == 74 ? CurrentBrush() : Get(r.U32()) is GdiBrush b ? Brush(b) : null;
        var (width, _) = type == 72 ? (r.I32(), r.I32()) : (0, 0);
        var region = Region(r, length, ToForm);
        if (type == 72)
        {
            FrameRegion(region, brush, width);
        }
        else
        {
            FillRegion(region, brush);
        }
    }

    // ----- Bitmaps -----

    private DecodedImage? Bitmap(MetaReader r, uint offBmi, uint cbBmi, uint offBits, uint cbBits, int usage, bool keepAlpha = false)
    {
        if (cbBmi == 0)
        {
            return null;
        }

        return Dib.Decode(Context, r.At(offBmi, cbBmi), r.At(offBits, cbBits), usage, State.Palette, null, keepAlpha);
    }

    /// <summary>BitBlt, StretchBlt, MaskBlt, PlgBlt, AlphaBlend and TransparentBlt.</summary>
    private void Blt(uint type, MetaReader r)
    {
        r.Skip(16);
        (double X, double Y)[]? parallelogram = null;
        double x = 0, y = 0, width = 0, height = 0;
        if (type == 79)
        {
            parallelogram = [(r.I32(), r.I32()), (r.I32(), r.I32()), (r.I32(), r.I32())];
        }
        else
        {
            (x, y, width, height) = (r.I32(), r.I32(), r.I32(), r.I32());
        }

        var rop = type == 79 ? 0x00CC0020u : r.U32();
        var (srcX, srcY) = (r.I32(), r.I32());
        double srcWidth = 0, srcHeight = 0;
        if (type == 79)
        {
            (srcWidth, srcHeight) = (r.I32(), r.I32());
        }

        r.Skip(24); // The source's world transform.
        r.ColorRef();
        var usage = (int)r.U32();
        var (offBmi, cbBmi, offBits, cbBits) = (r.U32(), r.U32(), r.U32(), r.U32());
        if (type is 77 or 114 or 116)
        {
            (srcWidth, srcHeight) = (r.I32(), r.I32());
        }
        else if (type is 76 or 78)
        {
            (srcWidth, srcHeight) = (width, height);
        }

        if (cbBmi == 0)
        {
            if (type is 76 or 77)
            {
                PatBlt(x, y, width, height, rop);
            }

            return;
        }

        var image = Bitmap(r, offBmi, cbBmi, offBits, cbBits, usage, keepAlpha: type == 114 && ((rop >> 24) & 1) != 0);
        if (image is null)
        {
            return;
        }

        if (type == 116)
        {
            image = Dib.WithTransparent(image, PdfColor.FromRgb((byte)rop, (byte)(rop >> 8), (byte)(rop >> 16)));
            rop = 0x00CC0020;
        }
        else if (type == 114)
        {
            var constant = (rop >> 16) & 0xFF;
            if (constant < 255)
            {
                Context.Report("Bitmaps blended with a constant opacity are drawn opaque.");
            }

            rop = 0x00CC0020;
        }
        else if (type == 78)
        {
            Context.Report("The masks of masked bitmaps (EMR_MASKBLT) are left out.");
        }

        if (Raster(image, rop) is not { } pdfImage)
        {
            return;
        }

        if (parallelogram is { } p)
        {
            // PlgBlt: the source rectangle's top left, top right and bottom left corners go to the three points.
            var (iw, ih) = (pdfImage.PixelWidth, pdfImage.PixelHeight);
            if (srcWidth <= 0 || srcHeight <= 0)
            {
                (srcX, srcY, srcWidth, srcHeight) = (0, 0, iw, ih);
            }

            var ux = ((p[1].X - p[0].X) / srcWidth, (p[1].Y - p[0].Y) / srcWidth);
            var vy = ((p[2].X - p[0].X) / srcHeight, (p[2].Y - p[0].Y) / srcHeight);
            var origin = (p[0].X - (ux.Item1 * srcX) - (vy.Item1 * srcY), p[0].Y - (ux.Item2 * srcX) - (vy.Item2 * srcY));

            // The unit square (v up from the image's bottom) to logical space, then to form space.
            var unit = new SvgMatrix(ux.Item1 * iw, ux.Item2 * iw, -vy.Item1 * ih, -vy.Item2 * ih, origin.Item1 + (vy.Item1 * ih), origin.Item2 + (vy.Item2 * ih)).Then(ToForm);
            Painter.Image(pdfImage, unit, State.Clip, MetaPath.Parallelogram(Map(p[0].X, p[0].Y), Map(p[1].X, p[1].Y), Map(p[2].X, p[2].Y)));
            return;
        }

        DrawBitmap(pdfImage, x, y, width, height, srcX, srcY, srcWidth, srcHeight);
    }

    /// <summary>
    /// The image to draw for a raster operation: the bitmap itself for SRCCOPY; for operations that keep what is
    /// under white (AND) or black (OR, XOR) pixels, the bitmap with that color transparent; others are approximated by
    /// SRCCOPY (reported) or left out.
    /// </summary>
    private PdfImage? Raster(DecodedImage image, uint rop)
    {
        switch (rop >> 16)
        {
            case 0xCC or 0xC0:
                return PdfImage.FromDecoded(image);
            case 0x88:
                return PdfImage.FromDecoded(Dib.WithTransparent(image, PdfColor.White));
            case 0xEE or 0x66:
                return PdfImage.FromDecoded(Dib.WithTransparent(image, PdfColor.Black));
            case 0xAA:
                return null;
            default:
                Context.Report("Bitmaps drawn with raster operations other than copying are drawn as copies.");
                return PdfImage.FromDecoded(image);
        }
    }

    private void StretchDIBits(MetaReader r)
    {
        r.Skip(16);
        var (x, y, srcX, srcY, srcWidth, srcHeight) = (r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32());
        var (offBmi, cbBmi, offBits, cbBits) = (r.U32(), r.U32(), r.U32(), r.U32());
        var usage = (int)r.U32();
        var rop = r.U32();
        var (width, height) = (r.I32(), r.I32());
        if (cbBmi == 0)
        {
            PatBlt(x, y, width, height, rop);
            return;
        }

        if (Bitmap(r, offBmi, cbBmi, offBits, cbBits, usage) is not { } image || Raster(image, rop) is not { } pdfImage)
        {
            return;
        }

        DrawBitmap(pdfImage, x, y, width, height, srcX, FromTop(r, offBmi, cbBmi, pdfImage.PixelHeight, srcY, srcHeight), srcWidth, srcHeight);
    }

    private void SetDIBitsToDevice(MetaReader r)
    {
        r.Skip(16);
        var (x, y, srcX, srcY, srcWidth, srcHeight) = (r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32());
        var (offBmi, cbBmi, offBits, cbBits) = (r.U32(), r.U32(), r.U32(), r.U32());
        var usage = (int)r.U32();
        if (Bitmap(r, offBmi, cbBmi, offBits, cbBits, usage) is not { } image)
        {
            return;
        }

        var pdfImage = PdfImage.FromDecoded(image);
        DrawBitmap(pdfImage, x, y, srcWidth, srcHeight, srcX, FromTop(r, offBmi, cbBmi, pdfImage.PixelHeight, srcY, srcHeight), srcWidth, srcHeight);
    }

    /// <summary>
    /// The source rectangle's top for a DIB function: its y counts from the bottom row of a bottom-up bitmap (positive
    /// height), from the top of a top-down one.
    /// </summary>
    private static double FromTop(MetaReader r, uint offBmi, uint cbBmi, int imageHeight, int srcY, int srcHeight)
    {
        if (cbBmi < 12)
        {
            return srcY;
        }

        var info = r.At(offBmi, cbBmi);
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(info);
        var bottomUp = headerSize == 12 || (info.Length >= 12 && BinaryPrimitives.ReadInt32LittleEndian(info[8..]) > 0);
        return bottomUp ? imageHeight - srcY - srcHeight : srcY;
    }

    // ----- Text -----

    /// <summary>An EmrText structure (after the bounds and graphics mode fields).</summary>
    private void TextRecord(MetaReader r, bool wide)
    {
        var (x, y) = (r.I32(), r.I32());
        var count = r.U32();
        var offString = r.U32();
        var options = (int)r.U32();
        (double, double, double, double)? rect = (r.I32(), r.I32(), r.I32(), r.I32());
        var offDx = r.U32();
        if (count > 0x10000)
        {
            throw new MetafileFormatException("a text record is too long");
        }

        Context.Count(count);
        var text = count == 0 ? string.Empty : wide ? r.Utf16At(offString, count) : State.Font.Encoding.GetString(r.At(offString, count));
        double[]? dx = null;
        var pairs = (options & 0x2000) != 0 ? 2 : 1;
        if (offDx != 0 && count > 0 && offDx <= r.Length - (count * 4L * pairs))
        {
            var span = r.At(offDx, count * 4L * pairs);
            var advances = new double[count];
            for (var i = 0; i < count; i++)
            {
                advances[i] = BinaryPrimitives.ReadInt32LittleEndian(span[(i * 4 * pairs)..]);
            }

            dx = wide ? advances : PerChar(advances, State.Font.Encoding, text);
        }

        if ((options & 0x100) != 0)
        {
            rect = null;
        }

        DrawText(x, y, text, dx, options, rect, glyphIndices: (options & 0x10) != 0 && wide);
    }

    /// <summary>Advances per byte made advances per UTF-16 unit of the decoded text (double-byte characters take two).</summary>
    internal static double[] PerChar(double[] perByte, Encoding encoding, string text)
    {
        if (text.Length == perByte.Length)
        {
            return perByte;
        }

        var result = new double[text.Length];
        var b = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            var byteCount = Math.Max(1, encoding.GetByteCount(text.AsSpan(i, length)));
            for (var k = 0; k < byteCount && b < perByte.Length; k++, b++)
            {
                result[i] += perByte[b];
            }

            i += length - 1;
        }

        return result;
    }

    private void SmallTextOut(MetaReader r)
    {
        var (x, y) = (r.I32(), r.I32());
        var count = r.U32();
        var options = (int)r.U32();
        r.Skip(12);
        (double, double, double, double)? rect = null;
        if ((options & 0x100) == 0)
        {
            rect = (r.I32(), r.I32(), r.I32(), r.I32());
        }

        if (count > 0x10000)
        {
            throw new MetafileFormatException("a text record is too long");
        }

        var text = (options & 0x200) != 0 ? State.Font.Encoding.GetString(r.Bytes((int)count)) : Encoding.Unicode.GetString(r.Bytes((int)count * 2));
        DrawText(x, y, text, null, options, rect, glyphIndices: (options & 0x10) != 0 && (options & 0x200) == 0);
    }

    // ----- Gradients -----

    private void GradientFill(MetaReader r)
    {
        r.Skip(16);
        var (vertices, items, mode) = (r.U32(), r.U32(), r.U32());
        if (vertices > r.Remaining / 16 || items > r.Remaining / 8)
        {
            throw new MetafileFormatException("a gradient has more vertices than it holds");
        }

        Context.Count(vertices + items);
        var points = new (double X, double Y, PdfColor Color)[vertices];
        for (var i = 0; i < vertices; i++)
        {
            var (x, y) = (r.I32(), r.I32());
            var (red, green, blue) = (r.U16(), r.U16(), r.U16());
            r.U16();
            points[i] = (x, y, new PdfColor(red / 65535.0, green / 65535.0, blue / 65535.0));
        }

        for (var i = 0; i < items; i++)
        {
            if (mode == 2)
            {
                var (a, b, c) = (r.U32(), r.U32(), r.U32());
                if (a >= vertices || b >= vertices || c >= vertices)
                {
                    continue;
                }

                // A triangle with a color at each corner: filled with their average.
                var (pa, pb, pc) = (points[a], points[b], points[c]);
                var color = new PdfColor((pa.Color.Red + pb.Color.Red + pc.Color.Red) / 3, (pa.Color.Green + pb.Color.Green + pc.Color.Green) / 3, (pa.Color.Blue + pb.Color.Blue + pc.Color.Blue) / 3);
                var triangle = new MetaPath().MoveTo(Map(pa.X, pa.Y)).LineTo(Map(pb.X, pb.Y)).LineTo(Map(pc.X, pc.Y)).ClosePath();
                Painter.Fill(triangle, new SolidBrush(color), false, State.Clip);
                Context.Report("Triangle gradients are filled with one color.");
            }
            else
            {
                var (a, b) = (r.U32(), r.U32());
                if (a >= vertices || b >= vertices)
                {
                    continue;
                }

                var (p0, p1) = (points[a], points[b]);
                var start = Map(p0.X, p0.Y);
                var end = mode == 0 ? Map(p1.X, p0.Y) : Map(p0.X, p1.Y);
                var rect = LogicalRectangle(p0.X, p0.Y, p1.X, p1.Y);
                Painter.Fill(rect, new GradientBrush(new AxialGeometry(start.X, start.Y, end.X, end.Y), [(0, p0.Color), (1, p1.Color)]), false, State.Clip);
            }
        }
    }
}

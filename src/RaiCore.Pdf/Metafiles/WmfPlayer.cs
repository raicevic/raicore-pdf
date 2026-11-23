using System.Buffers.Binary;
using RaiCore.Pdf.Svg;

namespace RaiCore.Pdf.Metafiles;

/// <summary>The geometry of a WMF file: where its records start and the window its coordinates map from.</summary>
/// <param name="RecordsStart">The offset of the first record (after the placeable and standard headers).</param>
/// <param name="Window">The logical rectangle (left, top, width, height) that fills the picture.</param>
/// <param name="Size">The natural size in points.</param>
/// <param name="Objects">The size of the object table.</param>
internal readonly record struct WmfHeader(int RecordsStart, (double Left, double Top, double Width, double Height) Window, (double Width, double Height) Size, int Objects)
{
    private const uint PlaceableKey = 0x9AC6CDD7;

    /// <summary>Whether the data starts with a placeable header.</summary>
    public static bool IsPlaceable(ReadOnlySpan<byte> data) => data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == PlaceableKey;

    /// <summary>Whether the data starts with a placeable header or a standard WMF header.</summary>
    public static bool IsWmf(ReadOnlySpan<byte> data)
    {
        if (IsPlaceable(data))
        {
            return true;
        }

        return data.Length >= 18 && BinaryPrimitives.ReadUInt16LittleEndian(data) is 1 or 2 && BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) == 9
            && BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) is 0x100 or 0x300;
    }

    /// <exception cref="ImageFormatException">The header is damaged, or the picture has no size.</exception>
    public static WmfHeader Read(byte[] data)
    {
        var span = data.AsSpan();
        var start = 0;
        (double Left, double Top, double Width, double Height)? box = null;
        var inch = 0;
        if (data.Length >= 22 && BinaryPrimitives.ReadUInt32LittleEndian(span) == PlaceableKey)
        {
            var (left, top, right, bottom) = (BinaryPrimitives.ReadInt16LittleEndian(span[6..]), BinaryPrimitives.ReadInt16LittleEndian(span[8..]),
                BinaryPrimitives.ReadInt16LittleEndian(span[10..]), BinaryPrimitives.ReadInt16LittleEndian(span[12..]));
            inch = BinaryPrimitives.ReadUInt16LittleEndian(span[14..]);
            if (right != left && bottom != top)
            {
                box = (left, top, right - left, bottom - top);
            }

            start = 22;
        }

        if (start > 0 && data.Length >= start + 20 && BinaryPrimitives.ReadUInt16LittleEndian(span[(start + 2)..]) != 9
            && BinaryPrimitives.ReadUInt16LittleEndian(span[(start + 4)..]) == 9)
        {
            // Some writers put two bytes of padding after the placeable header.
            start += 2;
        }

        if (data.Length < start + 18 || BinaryPrimitives.ReadUInt16LittleEndian(span[(start + 2)..]) != 9)
        {
            throw new ImageFormatException("The WMF header is damaged.");
        }

        var objects = BinaryPrimitives.ReadUInt16LittleEndian(span[(start + 10)..]);
        var recordsStart = start + 18;
        var window = box ?? FindWindow(data, recordsStart) ?? throw new ImageFormatException("The WMF picture has no size (no placeable header or window extent).");

        // The natural size: the placeable header's units per inch, else the window at 1440 units per inch (twips) for a
        // large window, at 96 for a small one.
        var unitsPerInch = box is not null && inch > 0 ? inch : Math.Max(Math.Abs(window.Width), Math.Abs(window.Height)) > 4000 ? 1440.0 : 96.0;
        (double, double) size = (Math.Abs(window.Width) / unitsPerInch * 72, Math.Abs(window.Height) / unitsPerInch * 72);
        return new WmfHeader(recordsStart, window, size, objects);
    }

    /// <summary>The window of the first SetWindowExt (and SetWindowOrg) record, for a WMF without a placeable header.</summary>
    internal static (double Left, double Top, double Width, double Height)? FindWindow(byte[] data, int offset)
    {
        (double X, double Y) origin = (0, 0);
        for (var steps = 0; offset <= data.Length - 6 && steps < 100_000; steps++)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) * 2L;
            var function = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
            if (size < 6 || size > data.Length - offset || function == 0)
            {
                break;
            }

            if (size >= 10 && function == 0x020B)
            {
                origin = (BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 8)), BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 6)));
            }
            else if (size >= 10 && function == 0x020C)
            {
                var (x, y) = (BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 8)), BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 6)));
                if (x != 0 && y != 0)
                {
                    return (origin.X, origin.Y, x, y);
                }
            }

            offset += (int)size;
        }

        return null;
    }

    /// <summary>
    /// The EMF file an Office WMF often carries in comment records (META_ESCAPE MFCOMMENT, "WMFC" chunks), which draws
    /// the same picture with more fidelity; null when there is none or it is incomplete.
    /// </summary>
    public static byte[]? EmbeddedEmf(byte[] data, int offset)
    {
        List<byte>? emf = null;
        long expected = 0;
        for (var steps = 0; offset <= data.Length - 6 && steps < 1_000_000; steps++)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) * 2L;
            var function = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
            if (size < 6 || size > data.Length - offset || function == 0)
            {
                break;
            }

            var span = data.AsSpan(offset + 6, (int)size - 6);
            if (function == 0x0626 && span.Length >= 38 && BinaryPrimitives.ReadUInt16LittleEndian(span) == 0x000F
                && BinaryPrimitives.ReadUInt32LittleEndian(span[4..]) == 0x43464D57 && BinaryPrimitives.ReadUInt32LittleEndian(span[8..]) == 1)
            {
                var current = BinaryPrimitives.ReadUInt32LittleEndian(span[26..]);
                var total = BinaryPrimitives.ReadUInt32LittleEndian(span[34..]);
                if (current > span.Length - 38 || total > ResourceLimits.Current.MaxResourceBytes)
                {
                    return null;
                }

                emf ??= new List<byte>((int)Math.Min(total, 1 << 24));
                expected = total;
                emf.AddRange(span.Slice(38, (int)current));
                if (emf.Count > expected)
                {
                    return null;
                }
            }

            offset += (int)size;
        }

        return emf is not null && emf.Count == expected && expected > 0 ? [.. emf] : null;
    }
}

/// <summary>
/// Plays WMF records ([MS-WMF]) onto a <see cref="MetaPainter"/>. The window (the placeable header's bounding box at
/// first, then what SetWindowOrg and SetWindowExt set) fills the picture, as when a WMF is played into a rectangle.
/// </summary>
internal sealed class WmfPlayer : GdiDevice
{
    private readonly byte[] _data;
    private readonly WmfHeader _header;
    private readonly double _devicePixel;
    private object?[] _objects;

    public WmfPlayer(MetafileContext context, MetaPainter painter, byte[] data, WmfHeader header)
        : base(context, painter)
    {
        _data = data;
        _header = header;
        _objects = new object?[Math.Max(1, header.Objects)];
        _devicePixel = Math.Min(0.75, Math.Max(painter.Width, painter.Height) / 100);
        SetMapMode(8);
        SetWindowOrg(header.Window.Left, header.Window.Top);
        SetWindowExt(header.Window.Width, header.Window.Height);
    }

    protected override double DevicePixel => _devicePixel;

    protected override SvgMatrix PageToForm(DcState state)
    {
        var (w, h) = (Painter.Width, Painter.Height);
        var (sx, sy) = (w / state.WindowExt.X, h / state.WindowExt.Y);
        if (state.MapMode == 7)
        {
            var m = Math.Min(Math.Abs(sx), Math.Abs(sy));
            (sx, sy) = (Math.CopySign(m, sx), Math.CopySign(m, sy));
        }

        return new SvgMatrix(sx, 0, 0, -sy, -state.WindowOrg.X * sx, h + (state.WindowOrg.Y * sy));
    }

    /// <summary>Plays the records; a damaged record ends the drawing (reported).</summary>
    public void Play()
    {
        var offset = _header.RecordsStart;
        var records = 0;
        try
        {
            while (offset <= _data.Length - 6)
            {
                var size = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset)) * 2L;
                var function = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset + 4));
                if (function == 0)
                {
                    break;
                }

                if (size < 6 || size > _data.Length - offset)
                {
                    throw new MetafileFormatException($"record {records + 1} has an invalid size");
                }

                Context.Count(1);
                records++;
                Record(function, new MetaReader(_data, offset, offset + (int)size) { Position = 6 });
                offset += (int)size;
            }

            if (offset < _data.Length && offset > _data.Length - 6)
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

    /// <summary>Counts the records (for tests): those before the end record or the first damaged one, as Apache POI does.</summary>
    public static int CountRecords(byte[] data, WmfHeader header)
    {
        var offset = header.RecordsStart;
        var count = 0;
        while (offset <= data.Length - 6)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) * 2L;
            if (size < 6 || size > data.Length - offset)
            {
                break;
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4)) == 0)
            {
                break;
            }

            count++;
            offset += (int)size;
        }

        return count;
    }

    private void Record(ushort function, MetaReader r)
    {
        switch (function)
        {
            case 0x001E:
                SaveDC();
                break;
            case 0x0127:
                RestoreDC(r.I16());
                break;
            case 0x0102:
                State.BackgroundMode = r.U16();
                break;
            case 0x0103:
                SetMapMode(r.U16());
                break;
            case 0x0104:
                State.Rop2 = r.U16();
                break;
            case 0x0106:
                State.PolyFillMode = r.U16();
                break;
            case 0x012E:
                State.TextAlign = r.U16();
                break;
            case 0x0201:
                State.BackgroundColor = Color(r.ColorRef());
                break;
            case 0x0209:
                State.TextColor = Color(r.ColorRef());
                break;
            case 0x020B:
            {
                var y = r.I16();
                SetWindowOrg(r.I16(), y);
                break;
            }

            case 0x020C:
            {
                var y = r.I16();
                SetWindowExt(r.I16(), y);
                break;
            }

            case 0x020F:
            {
                var y = r.I16();
                var x = r.I16();
                SetWindowOrg(State.WindowOrg.X + x, State.WindowOrg.Y + y);
                break;
            }

            case 0x0410:
            {
                var (yDenom, yNum, xDenom, xNum) = (r.I16(), r.I16(), r.I16(), r.I16());
                ScaleWindowExt(xNum, xDenom, yNum, yDenom);
                break;
            }

            case 0x0213:
            {
                var y = r.I16();
                LineTo(r.I16(), y);
                break;
            }

            case 0x0214:
            {
                var y = r.I16();
                MoveTo(r.I16(), y);
                break;
            }

            case 0x0220:
            {
                var y = r.I16();
                OffsetClip(r.I16(), y);
                break;
            }

            case 0x0415 or 0x0416 or 0x0418 or 0x041B:
            {
                var (bottom, right, top, left) = (r.I16(), r.I16(), r.I16(), r.I16());
                switch (function)
                {
                    case 0x0415:
                        ExcludeClipRect(left, top, right, bottom);
                        break;
                    case 0x0416:
                        IntersectClipRect(left, top, right, bottom);
                        break;
                    case 0x0418:
                        Ellipse(left, top, right, bottom);
                        break;
                    default:
                        Rectangle(left, top, right, bottom);
                        break;
                }

                break;
            }

            case 0x041F:
            {
                var color = Color(r.ColorRef());
                var y = r.I16();
                SetPixel(r.I16(), y, color);
                break;
            }

            case 0x061C:
            {
                var (height, width, bottom, right, top, left) = (r.I16(), r.I16(), r.I16(), r.I16(), r.I16(), r.I16());
                RoundRect(left, top, right, bottom, width, height);
                break;
            }

            case 0x061D:
            {
                var rop = r.U32();
                var (height, width, y, x) = (r.I16(), r.I16(), r.I16(), r.I16());
                PatBlt(x, y, width, height, rop);
                break;
            }

            case 0x0817 or 0x081A or 0x0830:
            {
                var (yEnd, xEnd, yStart, xStart, bottom, right, top, left) = (r.I16(), r.I16(), r.I16(), r.I16(), r.I16(), r.I16(), r.I16(), r.I16());
                Arc(left, top, right, bottom, xStart, yStart, xEnd, yEnd, function switch { 0x081A => 2, 0x0830 => 1, _ => 0 });
                break;
            }

            case 0x0324:
                Polygon(Points(r, r.I16()));
                break;
            case 0x0325:
                Polyline(Points(r, r.I16()));
                break;
            case 0x0538:
            {
                var polygons = r.U16();
                if (polygons > r.Remaining / 2)
                {
                    throw new MetafileFormatException("a record has more polygons than it holds");
                }

                var counts = new int[polygons];
                var total = 0;
                for (var i = 0; i < polygons; i++)
                {
                    counts[i] = r.U16();
                    total += counts[i];
                }

                PolyPolygon(Points(r, total), counts);
                break;
            }

            case 0x0521:
            {
                var length = r.I16();
                if (length < 0)
                {
                    break;
                }

                var bytes = r.Bytes(length).ToArray();
                r.Skip(length & 1);
                var y = r.I16();
                var x = r.I16();
                DrawText(x, y, State.Font.Encoding.GetString(bytes), null, 0, null);
                break;
            }

            case 0x0A32:
                ExtTextOut(r);
                break;
            case 0x0626:
                break;
            case 0x012D:
                Select(r.U16());
                break;
            case 0x01F0:
            {
                var index = r.U16();
                if (index < _objects.Length)
                {
                    _objects[index] = null;
                }

                break;
            }

            case 0x02FA:
            {
                var style = r.U16();
                var width = r.I16();
                r.I16();
                Add(new GdiPen(style, Math.Abs((double)width), Color(r.ColorRef())));
                break;
            }

            case 0x02FB:
                CreateFont(r);
                break;
            case 0x02FC:
            {
                var style = r.U16();
                var color = Color(r.ColorRef());
                var hatch = r.Remaining >= 2 ? r.U16() : 0;
                Add(new GdiBrush(style is 0 or 1 or 2 ? style : 0, color, hatch));
                break;
            }

            case 0x0142:
            {
                var style = r.U16();
                var usage = r.U16();
                var dib = r.Bytes(r.Remaining);
                var mono = style == 3 && dib.Length >= 16 && BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]) == 1;
                var image = Dib.InfoLength(dib, usage) is { } infoLength
                    ? Dib.Decode(Context, dib[..infoLength], dib[infoLength..], usage, State.Palette, mono ? (PdfColor.Black, PdfColor.White) : null, keepAlpha: false)
                    : null;
                Add(EmfPlayer.PatternBrush(image, mono));
                break;
            }

            case 0x01F9:
                // A pattern brush of a device-dependent bitmap: gray.
                Add(new GdiBrush(GdiBrush.Solid, PdfColor.FromRgb(128, 128, 128)));
                Context.Report("Pattern brushes of device-dependent bitmaps are drawn gray.");
                break;
            case 0x00F7:
            {
                r.U16();
                var count = r.U16();
                var colors = new PdfColor[Math.Min(count, r.Remaining / 4)];
                for (var i = 0; i < colors.Length; i++)
                {
                    var (red, green, blue, _) = r.ColorRef();
                    colors[i] = PdfColor.FromRgb(red, green, blue);
                }

                Add(new GdiPalette(colors));
                break;
            }

            case 0x0234:
                if (Get(r.U16()) is GdiPalette palette)
                {
                    State.Palette = palette;
                }

                break;
            case 0x06FF:
                Add(new GdiRegion(Region(r)));
                break;
            case 0x012C:
            {
                var index = r.U16();
                Clip(Get(index) is GdiRegion region ? region.Region : null, ClipRegion.Copy);
                break;
            }

            case 0x0228:
            {
                var (region, brush) = (r.U16(), r.U16());
                FillRegion((Get(region) as GdiRegion)?.Region, Get(brush) is GdiBrush b ? Brush(b) : null);
                break;
            }

            case 0x0429:
            {
                var (region, brush, _, width) = (r.U16(), r.U16(), r.I16(), r.I16());
                FrameRegion((Get(region) as GdiRegion)?.Region, Get(brush) is GdiBrush b ? Brush(b) : null, width);
                break;
            }

            case 0x012B:
                FillRegion((Get(r.U16()) as GdiRegion)?.Region, CurrentBrush());
                break;
            case 0x012A:
                Context.Report("Inverted regions are left out.");
                break;
            case 0x0940 or 0x0B41 or 0x0922 or 0x0B23:
                Blt(function, r);
                break;
            case 0x0F43:
            {
                var rop = r.U32();
                var usage = r.U16();
                var (srcHeight, srcWidth, srcY, srcX, height, width, y, x) = (r.I16(), r.I16(), r.I16(), r.I16(), r.I16(), r.I16(), r.I16(), r.I16());
                Bitmap(r, usage, rop, x, y, width, height, srcX, srcY, srcWidth, srcHeight, dibCoordinates: true);
                break;
            }

            case 0x0D33:
            {
                var usage = r.U16();
                r.Skip(4);
                var (srcY, srcX, height, width, y, x) = (r.I16(), r.I16(), r.I16(), r.I16(), r.I16(), r.I16());
                Bitmap(r, usage, 0x00CC0020, x, y, width, height, srcX, srcY, width, height, dibCoordinates: true);
                break;
            }

            case 0x0419 or 0x0548:
                Context.Report("Flood fills are left out.");
                break;
        }
    }

    private (double X, double Y)[] Points(MetaReader r, int count)
    {
        if (count < 0 || count > r.Remaining / 4)
        {
            throw new MetafileFormatException("a record has more points than it holds");
        }

        Context.Count(count);
        var points = new (double X, double Y)[count];
        for (var i = 0; i < count; i++)
        {
            points[i] = (r.I16(), r.I16());
        }

        return points;
    }

    // ----- Objects -----

    /// <summary>Puts a new object in the first free slot of the table (growing it if needed).</summary>
    private void Add(object value)
    {
        var index = Array.IndexOf(_objects, null);
        if (index < 0)
        {
            if (_objects.Length >= 0x10000)
            {
                return;
            }

            index = _objects.Length;
            Array.Resize(ref _objects, Math.Min(0x10000, _objects.Length * 2));
            Context.Count(_objects.Length - index);
        }

        _objects[index] = value;
    }

    private object? Get(int index) => index >= 0 && index < _objects.Length ? _objects[index] : null;

    private void Select(int index)
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
            case GdiRegion region:
                Clip(region.Region, ClipRegion.Copy);
                break;
        }
    }

    private void CreateFont(MetaReader r)
    {
        var (height, width, escapement) = (r.I16(), r.I16(), r.I16());
        r.I16();
        var weight = r.I16();
        var italic = r.U8() != 0;
        var underline = r.U8() != 0;
        var strikeOut = r.U8() != 0;
        var charset = r.U8();
        r.Skip(4);
        var faceBytes = r.Bytes(Math.Min(32, r.Remaining));
        var nul = faceBytes.IndexOf((byte)0);
        var font = new GdiFont("Arial", height, width, escapement, weight, italic, underline, strikeOut, charset);
        var face = font.Encoding.GetString(nul >= 0 ? faceBytes[..nul] : faceBytes).Trim();
        Add(font with { Face = face.Length > 0 ? face : "Arial" });
    }

    /// <summary>A region object ([MS-WMF] Region): its scans' rectangles as one path.</summary>
    private ClipRegion Region(MetaReader r)
    {
        r.Skip(2 + 2 + 4 + 2);
        var scans = r.U16();
        r.Skip(2 + 8);
        var path = new MetaPath();
        for (var s = 0; s < scans && r.Remaining >= 6; s++)
        {
            var count = r.U16();
            var (top, bottom) = (r.I16(), r.I16());
            if (count > r.Remaining / 2)
            {
                throw new MetafileFormatException("a region is damaged");
            }

            Context.Count(count);
            for (var i = 0; i + 1 < count; i += 2)
            {
                var (left, right) = (r.I16(), r.I16());
                path.Append(LogicalRectangle(left, top, right, bottom));
            }

            r.Skip(2);
        }

        return path.IsEmpty ? ClipRegion.Empty : ClipRegion.Of(path);
    }

    // ----- Text -----

    private void ExtTextOut(MetaReader r)
    {
        var (y, x) = (r.I16(), r.I16());
        var length = r.I16();
        var options = r.U16();
        (double, double, double, double)? rect = null;
        if ((options & 0x6) != 0 && r.Remaining >= 8)
        {
            rect = (r.I16(), r.I16(), r.I16(), r.I16());
        }

        if (length <= 0)
        {
            DrawText(x, y, string.Empty, null, options, rect);
            return;
        }

        var bytes = r.Bytes(Math.Min(length, r.Remaining)).ToArray();
        r.Skip(Math.Min(length & 1, r.Remaining));
        var encoding = State.Font.Encoding;
        var text = encoding.GetString(bytes);
        double[]? dx = null;
        if (r.Remaining >= bytes.Length * 2)
        {
            var advances = new double[bytes.Length];
            for (var i = 0; i < advances.Length; i++)
            {
                advances[i] = r.I16();
            }

            dx = EmfPlayer.PerChar(advances, encoding, text);
        }

        DrawText(x, y, text, dx, options, rect);
    }

    // ----- Bitmaps -----

    private void Blt(ushort function, MetaReader r)
    {
        var stretch = function is 0x0B41 or 0x0B23;
        var withBitmap = r.Length != ((function >> 8) + 3) * 2;
        var rop = r.U32();
        double srcHeight = 0, srcWidth = 0;
        if (stretch)
        {
            (srcHeight, srcWidth) = (r.I16(), r.I16());
        }

        var (srcY, srcX) = (r.I16(), r.I16());
        if (!withBitmap)
        {
            r.I16();
        }

        var (height, width, y, x) = (r.I16(), r.I16(), r.I16(), r.I16());
        if (!stretch)
        {
            (srcWidth, srcHeight) = (width, height);
        }

        if (!withBitmap)
        {
            PatBlt(x, y, width, height, rop);
            return;
        }

        if (function is 0x0922 or 0x0B23)
        {
            Context.Report("Device-dependent bitmaps are left out.");
            return;
        }

        Bitmap(r, 0, rop, x, y, width, height, srcX, srcY, srcWidth, srcHeight, dibCoordinates: false);
    }

    private void Bitmap(MetaReader r, int usage, uint rop, double x, double y, double width, double height, double srcX, double srcY, double srcWidth, double srcHeight, bool dibCoordinates)
    {
        var dib = r.Bytes(r.Remaining);
        if (Dib.InfoLength(dib, usage) is not { } infoLength)
        {
            if (!UsesSource(rop))
            {
                PatBlt(x, y, width, height, rop);
            }

            return;
        }

        if (Dib.Decode(Context, dib[..infoLength], dib[infoLength..], usage, State.Palette, null, keepAlpha: false) is not { } image)
        {
            return;
        }

        var keyed = (rop >> 16) switch
        {
            0x88 => Dib.WithTransparent(image, PdfColor.White),
            0xEE or 0x66 => Dib.WithTransparent(image, PdfColor.Black),
            _ => image,
        };
        if ((rop >> 16) is 0xAA)
        {
            return;
        }

        if ((rop >> 16) is not (0xCC or 0xC0 or 0x88 or 0xEE or 0x66))
        {
            Context.Report("Bitmaps drawn with raster operations other than copying are drawn as copies.");
        }

        var pdfImage = PdfImage.FromDecoded(keyed);
        if (dibCoordinates && BinaryPrimitives.ReadInt32LittleEndian(dib) != 12 && dib.Length >= 12 && BinaryPrimitives.ReadInt32LittleEndian(dib[8..]) > 0)
        {
            srcY = pdfImage.PixelHeight - srcY - srcHeight;
        }

        DrawBitmap(pdfImage, x, y, width, height, srcX, srcY, srcWidth, srcHeight);
    }
}

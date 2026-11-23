using RaiCore.Pdf.Metafiles;
using RaiCore.Pdf.Slides.Model;

namespace RaiCore.Pdf.Slides.Ppt;

/// <summary>Shapes: boxes, groups, preset and free-form geometry, fills, lines and pictures.</summary>
internal sealed partial class PptReader
{
    /// <summary>EMUs per point (line widths and text insets are in EMUs).</summary>
    private const double Emu = 12700;

    /// <summary>The coordinate size of the preset shapes' adjust values.</summary>
    private const double GeoSize = 21600;

    /// <summary>The preset shapes by OfficeArt shape type (<c>MSOSPT</c>), as DrawingML names them.</summary>
    private static readonly Dictionary<int, string> Presets = new()
    {
        [1] = "rect", [2] = "roundRect", [3] = "ellipse", [4] = "diamond", [5] = "triangle", [6] = "rtTriangle",
        [7] = "parallelogram", [8] = "trapezoid", [9] = "hexagon", [10] = "octagon", [11] = "plus", [12] = "star5",
        [13] = "rightArrow", [14] = "rightArrow", [15] = "homePlate", [16] = "cube", [18] = "star16", [19] = "arc",
        [20] = "line", [21] = "plaque", [22] = "can", [23] = "donut", [32] = "straightConnector1",
        [33] = "bentConnector2", [34] = "bentConnector3", [35] = "bentConnector4", [36] = "bentConnector5",
        [37] = "curvedConnector2", [38] = "curvedConnector3", [39] = "curvedConnector4", [40] = "curvedConnector5",
        [55] = "chevron", [56] = "pentagon", [58] = "star8", [59] = "star16", [60] = "star32", [61] = "wedgeRectCallout",
        [62] = "wedgeRoundRectCallout", [63] = "wedgeEllipseCallout", [65] = "foldedCorner", [66] = "leftArrow",
        [67] = "downArrow", [68] = "upArrow", [69] = "leftRightArrow", [70] = "upDownArrow", [74] = "heart",
        [75] = "rect", [84] = "bevel", [85] = "leftBracket", [86] = "rightBracket", [87] = "leftBrace",
        [88] = "rightBrace", [92] = "star24", [93] = "stripedRightArrow", [94] = "notchedRightArrow", [95] = "blockArc",
        [109] = "flowChartProcess", [110] = "flowChartDecision", [111] = "flowChartInputOutput",
        [112] = "flowChartPredefinedProcess", [114] = "flowChartDocument", [116] = "flowChartTerminator",
        [117] = "flowChartPreparation", [118] = "flowChartManualInput", [119] = "flowChartManualOperation",
        [120] = "flowChartConnector", [127] = "flowChartExtract", [128] = "flowChartMerge", [176] = "flowChartAlternateProcess",
        [177] = "flowChartOffpageConnector", [185] = "moon", [186] = "bracketPair", [187] = "bracePair", [188] = "star4",
        [202] = "rect",
    };

    /// <summary>The presets whose first adjust value is a length of the shorter side (21600 = all of it).</summary>
    private static readonly HashSet<int> LengthAdjusted = [2, 5, 7, 8, 9, 10];

    /// <summary>Converts a shape to the slide model; <c>null</c> for a hidden, deleted or unplaced one.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="owner">The sheet the shape belongs to (its text styles and outline text).</param>
    /// <param name="slide">The slide drawn (its colors).</param>
    /// <param name="inGroup">Whether the shape is a member of a group (placed by its child anchor).</param>
    /// <param name="depth">The group nesting.</param>
    /// <param name="field">For a master's footer placeholder, the text it shows.</param>
    private Shape? Convert(EscherShape shape, Sheet owner, Sheet slide, bool inGroup, int depth, string? field = null)
    {
        _checkpoint?.Invoke();
        if ((shape.Flags & (ShapeFlags.Deleted | ShapeFlags.Patriarch)) != 0 || shape.Flag(Op.GroupBooleans, Op.HiddenBit, null) == true
            || depth > _limits.MaxNestingDepth)
        {
            return null;
        }

        if (Box(shape, inGroup) is not { } box)
        {
            return null;
        }

        var name = shape.Get(Op.Name)?.Complex is { } nameData ? Utf16(nameData) : null;
        var alt = shape.Get(Op.Description)?.Complex is { } altData ? Utf16(altData) : null;
        var placeholder = PlaceholderType(shape);
        var kind = placeholder is 0 ? PlaceholderKind.None : placeholder is 1 or 3 or 13 or 15 or 17 ? PlaceholderKind.Title : PlaceholderKind.Other;
        var link = ShapeLink(shape);
        if (shape.IsGroup)
        {
            var bounds = shape.GroupBounds ?? new AnchorRect(0, 0, 0, 0);
            var group = new GroupShape
            {
                Box = box,
                Name = name,
                AltText = alt,
                Link = link,
                ChildX = Math.Min(bounds.Left, bounds.Right) / MasterUnits,
                ChildY = Math.Min(bounds.Top, bounds.Bottom) / MasterUnits,
                ChildWidth = Math.Abs((double)bounds.Right - bounds.Left) / MasterUnits,
                ChildHeight = Math.Abs((double)bounds.Bottom - bounds.Top) / MasterUnits,
            };
            foreach (var child in shape.Children)
            {
                if (Convert(child, owner, slide, true, depth + 1) is { } converted)
                {
                    group.Children.Add(converted);
                }
            }

            return group;
        }

        if (shape.Get(Op.Pib) is { Value: > 0 } pib)
        {
            if ((shape.Flags & ShapeFlags.OleShape) != 0)
            {
                Report(_slideNumber, "object", "An embedded object is drawn as its picture.");
            }

            return new PictureShape
            {
                Box = box,
                Name = name,
                AltText = alt,
                Link = link,
                Placeholder = kind,
                Data = Blip(pib.Value, out var source),
                Source = source,
                Crop = new Crop(Fraction(shape, Op.CropLeft), Fraction(shape, Op.CropTop), Fraction(shape, Op.CropRight), Fraction(shape, Op.CropBottom)),
                Line = shape.Flag(Op.LineBooleans, Op.LineBit, null) == true ? Line(shape, slide) : null,
            };
        }

        if ((shape.Flags & ShapeFlags.OleShape) != 0)
        {
            Report(_slideNumber, "object", "An embedded object without a picture is not drawn.");
        }

        var geometry = Geometry(shape);
        if (shape.Get(Op.WordArtText)?.Complex is { } wordArt && Utf16(wordArt) is { } wordArtText)
        {
            return WordArt(shape, box, wordArtText, slide, name, alt, link);
        }

        var text = Text(shape, owner, slide, placeholder, field);
        return new AutoShape
        {
            Box = box,
            Name = name,
            AltText = alt,
            Link = link,
            Placeholder = kind,
            Geometry = geometry,
            Fill = geometry.IsLine ? null : Fill(shape, slide, slide, null),
            Line = shape.Flag(Op.LineBooleans, Op.LineBit, _drawingDefaults) != false ? Line(shape, slide) : null,
            Text = text,
        };
    }

    /// <summary>
    /// WordArt (its text in the geometry text properties) as a text box: its text centered in the shape's font and
    /// size, in its fill color, not bent or stretched (reported).
    /// </summary>
    private AutoShape WordArt(EscherShape shape, ShapeBox box, string text, Sheet slide, string? name, string? alt, Uri? link)
    {
        Report(_slideNumber, "WordArt", "WordArt is drawn as plain text, not bent or stretched to its shape.");
        var size = shape.Value(Op.WordArtSize, 36 << 16) / 65536.0;
        var style = new RunStyle
        {
            Font = shape.Get(Op.WordArtFont)?.Complex is { } font && Utf16(font) is { } face ? face : "Arial",
            Size = size is >= 1 and <= 4000 ? size : 36,
            Bold = shape.Flag(Op.WordArtBooleans, 5, null) == true,
            Italic = shape.Flag(Op.WordArtBooleans, 4, null) == true,
            Color = shape.Flag(Op.FillBooleans, Op.FilledBit, _drawingDefaults) == false ? PdfColor.Black : Color(shape, Op.FillColor, Op.FillOpacity, slide, PdfColor.Black),
        };
        var frame = new TextFrame { Anchor = TextAnchor.Middle, Wrap = false, LeftInset = 0, RightInset = 0, TopInset = 0, BottomInset = 0 };
        foreach (var line in text.Split('\n'))
        {
            var paragraph = new TextParagraph { Alignment = ParagraphAlignment.Center, EndStyle = style };
            paragraph.Runs.Add(new TextRun { Text = line.Trim('\r'), Style = style });
            frame.Paragraphs.Add(paragraph);
        }

        return new AutoShape { Box = box, Name = name, AltText = alt, Link = link, Text = frame };
    }

    /// <summary>
    /// A shape's box in points: its client anchor (master units) or, in a group, its child anchor. A shape turned
    /// 45 to 135 degrees (or 225 to 315) stores the box of the turned shape, so its own box is that one's turned
    /// back about its center.
    /// </summary>
    private static ShapeBox? Box(EscherShape shape, bool inGroup)
    {
        if ((inGroup ? shape.ChildAnchor ?? shape.ClientAnchor : shape.ClientAnchor ?? shape.ChildAnchor) is not { } anchor)
        {
            return null;
        }

        var x = Math.Min(anchor.Left, anchor.Right) / MasterUnits;
        var y = Math.Min(anchor.Top, anchor.Bottom) / MasterUnits;
        var width = Math.Abs((double)anchor.Right - anchor.Left) / MasterUnits;
        var height = Math.Abs((double)anchor.Bottom - anchor.Top) / MasterUnits;
        var rotation = shape.Value(Op.Rotation, 0) / 65536.0 % 360;
        if (rotation < 0)
        {
            rotation += 360;
        }

        if (rotation is >= 45 and < 135 or >= 225 and < 315)
        {
            (x, y, width, height) = (x + ((width - height) / 2), y + ((height - width) / 2), height, width);
        }

        return new ShapeBox(x, y, width, height, rotation, (shape.Flags & ShapeFlags.FlipH) != 0, (shape.Flags & ShapeFlags.FlipV) != 0);
    }

    /// <summary>The placeholder type of a shape (its <c>OEPlaceholderAtom</c>'s placement id), or 0.</summary>
    private int PlaceholderType(EscherShape shape) =>
        shape.ClientData is { } data && _stream.Child(data, Rt.PlaceholderAtom) is { } atom ? Bytes.U8(_stream.Body(atom), 4) : 0;

    /// <summary>The hyperlink a shape's click action opens, if any.</summary>
    private Uri? ShapeLink(EscherShape shape)
    {
        if (shape.ClientData is not { } data || _stream.Child(data, Rt.InteractiveInfo, 0) is not { } info
            || _stream.Child(info, Rt.InteractiveInfoAtom) is not { } atom)
        {
            return null;
        }

        var body = _stream.Body(atom);
        return Bytes.U8(body, 8) == 4 && _hyperlinks.TryGetValue(Bytes.I32(body, 4), out var uri) ? uri : null;
    }

    /// <summary>A 16.16 fixed-point fraction property (a picture's crop), kept within reason.</summary>
    private static double Fraction(EscherShape shape, int id) => Math.Clamp(shape.Value(id, 0) / 65536.0, -10, 1);

    private static string? Utf16(byte[] data)
    {
        var text = System.Text.Encoding.Unicode.GetString(data, 0, data.Length & ~1);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        text = end >= 0 ? text[..end] : text;
        return text.Length > 0 ? text : null;
    }

    /// <summary>The outline of a shape: a free form from its vertices, a preset, or a rectangle (reported).</summary>
    private Geometry Geometry(EscherShape shape)
    {
        if (shape.Get(Op.Vertices)?.Complex is { Length: > 6 } && FreeformGeometry.From(shape) is { } freeform)
        {
            return freeform;
        }

        if (Presets.TryGetValue(shape.ShapeType, out var preset))
        {
            Dictionary<string, double>? adjust = null;
            if (LengthAdjusted.Contains(shape.ShapeType) && shape.Get(Op.Adjust1) is { } adj)
            {
                adjust = new() { ["adj"] = Math.Clamp(adj.Value, -10 * GeoSize, 10 * GeoSize) * 100000 / GeoSize };
            }

            var geometry = new PresetGeometry(preset, adjust);
            if (!geometry.IsKnown && preset != "rect")
            {
                Report(_slideNumber, "shape", $"The preset shape '{preset}' is drawn as a rectangle.");
            }

            return geometry;
        }

        if (shape.ShapeType != 0)
        {
            Report(_slideNumber, "shape", $"The shape type {shape.ShapeType} is drawn as a rectangle.");
        }

        return Model.Geometry.Rectangle;
    }

    /// <summary>
    /// A shape's fill, or <c>null</c> for none: solid, pattern (as the average of its colors), picture or texture,
    /// gradient, or the slide's background.
    /// </summary>
    private Fill? Fill(EscherShape shape, Sheet colors, Sheet slide, Fill? background)
    {
        if (shape.Flag(Op.FillBooleans, Op.FilledBit, _drawingDefaults) == false)
        {
            return null;
        }

        var color = Color(shape, Op.FillColor, Op.FillOpacity, colors, PdfColor.White);
        switch (shape.Value(Op.FillType, 0))
        {
            case 1:
            {
                var back = Color(shape, Op.FillBackColor, Op.FillBackOpacity, colors, PdfColor.White);
                return new SolidFill(new PdfColor((color.Red + back.Red) / 2, (color.Green + back.Green) / 2, (color.Blue + back.Blue) / 2, color.Alpha));
            }

            case 2 or 3:
                if (shape.Get(Op.FillBlip) is { Value: > 0 } blip && Blip(blip.Value, out var source) is { } data)
                {
                    return new PictureFill(data, source ?? "picture");
                }

                return new SolidFill(color);
            case >= 4 and <= 8 and var type:
                return Gradient(shape, colors, color, type);
            case 9:
                return background ?? Background(slide);
            default:
                return new SolidFill(color);
        }
    }

    /// <summary>A gradient fill: its color stops (the fill and back colors, by focus) and angle.</summary>
    private GradientFill Gradient(EscherShape shape, Sheet colors, PdfColor color, int type)
    {
        var back = Color(shape, Op.FillBackColor, Op.FillBackOpacity, colors, PdfColor.White);
        var stops = new List<GradientStop>();
        foreach (var (data, offset, size) in EscherShape.Elements(shape.Get(Op.FillShadeColors)?.Complex))
        {
            if (size >= 8 && stops.Count < 64)
            {
                var stopColor = Color(Bytes.I32(data, offset), colors, shape, PdfColor.White);
                stops.Add(new GradientStop(Math.Clamp(Bytes.I32(data, offset + 4) / 65536.0, 0, 1), stopColor));
            }
        }

        if (stops.Count < 2)
        {
            stops.Clear();
            // The focus: 0 runs from the fill color to the back color, 100 back, 50 and -50 put one color in the
            // middle and the other at both ends.
            var focus = Math.Clamp(shape.Value(Op.FillFocus, 0), -100, 100);
            if (Math.Abs(focus) >= 75)
            {
                stops.AddRange([new(0, back), new(1, color)]);
            }
            else if (Math.Abs(focus) >= 25)
            {
                var (ends, middle) = focus > 0 ? (color, back) : (back, color);
                stops.AddRange([new(0, ends), new(0.5, middle), new(1, ends)]);
            }
            else
            {
                stops.AddRange([new(0, color), new(1, back)]);
            }
        }
        else
        {
            stops.Sort((a, b) => a.Position.CompareTo(b.Position));
        }

        var angle = 90 - (shape.Value(Op.FillAngle, 0) / 65536.0);
        angle = (angle % 360 + 360) % 360;
        return new GradientFill(stops, double.IsFinite(angle) ? angle : 90, type is 5 or 6);
    }

    /// <summary>A shape's line: color, width, dashes, joins, caps and arrowheads.</summary>
    private Outline Line(EscherShape shape, Sheet colors)
    {
        var width = shape.Value(Op.LineWidth, 9525) / Emu;
        var dash = shape.Value(Op.LineDashing, 0) switch
        {
            1 => [3, 1],
            2 => [1, 1],
            3 => [3, 1, 1, 1],
            4 => [3, 1, 1, 1, 1, 1],
            5 => [1, 3],
            6 => [4, 3],
            7 => [8, 3],
            8 => [4, 3, 1, 3],
            9 => [8, 3, 1, 3],
            10 => [8, 3, 1, 3, 1, 3],
            _ => (double[])[],
        };
        return new Outline
        {
            Width = width is > 0 and < 1000 ? width : 0.75,
            Color = Color(shape, Op.LineColor, Op.LineOpacity, colors, PdfColor.Black),
            Dash = dash,
            Join = shape.Value(Op.LineJoinStyle, 2) switch { 0 => LineJoin.Bevel, 1 => LineJoin.Miter, _ => LineJoin.Round },
            Cap = shape.Value(Op.LineEndCapStyle, 2) switch { 0 => LineCap.Round, 1 => LineCap.Square, _ => LineCap.Butt },
            Head = Arrowhead(shape.Value(Op.LineStartArrowhead, 0), shape.Value(Op.LineStartArrowWidth, 1), shape.Value(Op.LineStartArrowLength, 1)),
            Tail = Arrowhead(shape.Value(Op.LineEndArrowhead, 0), shape.Value(Op.LineEndArrowWidth, 1), shape.Value(Op.LineEndArrowLength, 1)),
        };
    }

    private static LineEnd? Arrowhead(int kind, int width, int length)
    {
        static double Size(int value) => value switch { 0 => 2, 2 => 5, _ => 3 };
        LineEndKind? end = kind switch
        {
            1 => LineEndKind.Triangle,
            2 => LineEndKind.Stealth,
            3 => LineEndKind.Diamond,
            4 => LineEndKind.Oval,
            5 => LineEndKind.Arrow,
            _ => null,
        };
        return end is { } k ? new LineEnd(k, Size(width), Size(length)) : null;
    }

    /// <summary>A color property with its opacity property, or a default.</summary>
    private PdfColor Color(EscherShape shape, int id, int opacityId, Sheet colors, PdfColor fallback)
    {
        var value = shape.Get(id)?.Value ?? (_drawingDefaults is not null && _drawingDefaults.TryGetValue(id, out var d) ? d.Value : (int?)null);
        var color = value is { } v ? Color(v, colors, shape, fallback) : fallback;
        var opacity = shape.Get(opacityId) is { } o ? Math.Clamp(o.Value / 65536.0, 0, 1) : 1;
        return opacity < 1 ? color.WithAlpha(opacity) : color;
    }

    /// <summary>
    /// An <c>OfficeArtCOLORREF</c>: red, green and blue, a color of the scheme, or a system color (the shape's fill or
    /// line color, for the few that refer to them).
    /// </summary>
    private static PdfColor Color(int value, Sheet colors, EscherShape? shape, PdfColor fallback, int depth = 0)
    {
        var flags = (value >> 24) & 0xFF;
        if ((flags & 0x08) != 0)
        {
            var index = value & 0xFF;
            return index < colors.Scheme.Length ? colors.Scheme[index] : fallback;
        }

        if ((flags & 0x10) != 0)
        {
            var id = (value & 0xFF) switch { 0xF0 => Op.FillColor, 0xF1 => Op.LineColor, 0xF3 => Op.FillBackColor, _ => 0 };
            return id != 0 && depth < 2 && shape?.Get(id) is { } other ? Color(other.Value, colors, shape, fallback, depth + 1) : fallback;
        }

        return PdfColor.FromRgb((byte)value, (byte)(value >> 8), (byte)(value >> 16));
    }

    /// <summary>
    /// The image of a picture store entry (from 1), read once: PNG, JPEG, TIFF and DIB (as BMP) bitmaps; EMF and WMF
    /// metafiles decompressed (a WMF with its placeable header); <c>null</c> when it is missing or cannot be read.
    /// </summary>
    private byte[]? Blip(int index, out string? source)
    {
        source = $"picture {index}";
        if (_blips.TryGetValue(index, out var cached))
        {
            return cached;
        }

        byte[]? data = null;
        var pict = false;
        if (index >= 1 && index <= _blipStore.Count && _blipStore[index - 1] is { Type: Rt.Fbse } entry)
        {
            var body = _stream.Body(entry);
            var name = Bytes.U8(body, 33);
            Record? blip = null;
            byte[]? blipData = null;
            if (body.Length > 36 + name + 8)
            {
                // The picture is in the entry itself.
                blipData = _stream.Data;
                blip = _stream.At(entry.Offset + 36 + name, entry.End);
            }
            else if (_file.Pictures is { } pictures)
            {
                var stream = new RecordStream(pictures, _limits);
                blipData = pictures;
                blip = stream.At(Bytes.U32(body, 28));
            }

            if (blip is { } b && blipData is not null)
            {
                data = BlipImage(blipData.AsSpan(b.Offset, b.Length), b.Type, b.Instance);
                pict = b.Type == 0xF01C;
            }
        }

        if (data is null)
        {
            Report(_slideNumber, "picture", pict
                ? $"The PICT picture '{source}' is not drawn: PICT is not supported."
                : $"The {source} is missing or in an unknown format; it is not drawn.");
        }

        _blips[index] = data;
        return data;
    }

    /// <summary>The image file of a BLIP record's body.</summary>
    private byte[]? BlipImage(ReadOnlySpan<byte> body, int type, int instance)
    {
        var uids = (instance & 1) != 0 ? 32 : 16;
        switch (type)
        {
            case 0xF01D or 0xF02A or 0xF01E or 0xF029:
                // JPEG, CMYK JPEG, PNG, TIFF: the image after the ids and a tag byte.
                return body.Length > uids + 1 ? body[(uids + 1)..].ToArray() : null;
            case 0xF01F:
                return body.Length > uids + 1 + 40 ? Bmp(body[(uids + 1)..]) : null;
            case 0xF01A or 0xF01B:
            {
                // EMF, WMF: a 34-byte metafile header and the zlib-compressed file.
                var file = body.Length > uids ? MetafileBlip.Decode(body[uids..], type == 0xF01B, _limits.MaxDecompressedBytes - _decompressed) : null;
                _decompressed += file?.Length ?? 0;
                return file;
            }

            default:
                return null;
        }
    }

    /// <summary>A device-independent bitmap as a BMP file: the file header put before it.</summary>
    private static byte[] Bmp(ReadOnlySpan<byte> dib)
    {
        var headerSize = (int)Math.Min(Bytes.U32(dib, 0), (uint)dib.Length);
        var bits = Bytes.U16(dib, 14);
        var compression = Bytes.U32(dib, 16);
        var used = Bytes.U32(dib, 32);
        long colors = used is > 0 and <= 256 ? used : bits <= 8 ? 1 << bits : 0;
        var masks = headerSize == 40 && compression == 3 ? 12 : headerSize == 40 && compression == 6 ? 16 : 0;
        var offset = 14 + headerSize + masks + (colors * 4);
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), (int)Math.Min(offset, file.Length));
        dib.CopyTo(file.AsSpan(14));
        return file;
    }
}

/// <summary>
/// A free form of OfficeArt (<c>pVertices</c> and <c>pSegmentInfo</c> in the shape's geometry space,
/// <c>geoLeft</c> to <c>geoRight</c>): lines, cubic curves, moves and closes, scaled to the shape's box.
/// </summary>
internal sealed class FreeformGeometry : Geometry
{
    /// <summary>The most segments read from a shape.</summary>
    private const int MaxSegments = 100_000;

    private readonly List<(List<(SegmentKind Kind, double[] Points)> Commands, bool Filled, bool Stroked)> _paths;

    private FreeformGeometry(List<(List<(SegmentKind, double[])>, bool, bool)> paths) => _paths = paths;

    /// <summary>The free form of a shape, or <c>null</c> when its vertices make no path.</summary>
    public static FreeformGeometry? From(EscherShape shape)
    {
        var vertices = new List<(double X, double Y)>();
        foreach (var (data, offset, size) in EscherShape.Elements(shape.Get(Op.Vertices)?.Complex))
        {
            vertices.Add(size >= 8 ? (Bytes.I32(data, offset), Bytes.I32(data, offset + 4)) : (Bytes.I16(data, offset), Bytes.I16(data, offset + 2)));
        }

        if (vertices.Count < 2)
        {
            return null;
        }

        var left = shape.Value(Op.GeoLeft, 0);
        var top = shape.Value(Op.GeoTop, 0);
        var width = (double)shape.Value(Op.GeoRight, 21600) - left;
        var height = (double)shape.Value(Op.GeoBottom, 21600) - top;
        if (width == 0 || height == 0)
        {
            return null;
        }

        double[] Point(int i) => [(vertices[i].X - left) / width, (vertices[i].Y - top) / height];
        var paths = new List<(List<(SegmentKind, double[])>, bool, bool)>();
        var commands = new List<(SegmentKind, double[])>();
        var (filled, stroked) = (true, true);
        var next = 0;
        var segments = EscherShape.Elements(shape.Get(Op.SegmentInfo)?.Complex).Take(MaxSegments).ToList();
        if (segments.Count == 0)
        {
            // No segments: a line through the vertices, closed for a closed path type.
            commands.Add((SegmentKind.Move, Point(0)));
            for (var i = 1; i < vertices.Count; i++)
            {
                commands.Add((SegmentKind.Line, Point(i)));
            }

            if (shape.Value(Op.ShapePath, 1) is 1 or 3)
            {
                commands.Add((SegmentKind.Close, []));
            }

            paths.Add((commands, true, true));
            return new FreeformGeometry(paths);
        }

        foreach (var (data, offset, _) in segments)
        {
            var info = Bytes.U16(data, offset);
            var type = (info >> 13) & 0x7;
            var count = info & 0x1FFF;
            switch (type)
            {
                case 0:
                    for (var i = 0; i < Math.Max(count, 1) && next < vertices.Count; i++)
                    {
                        commands.Add((commands.Count == 0 ? SegmentKind.Move : SegmentKind.Line, Point(next++)));
                    }

                    break;
                case 1:
                    for (var i = 0; i < Math.Max(count, 1) && next + 2 < vertices.Count; i++, next += 3)
                    {
                        if (commands.Count == 0)
                        {
                            commands.Add((SegmentKind.Move, Point(next)));
                        }

                        commands.Add((SegmentKind.Cubic, [.. Point(next), .. Point(next + 1), .. Point(next + 2)]));
                    }

                    break;
                case 2 when next < vertices.Count:
                    commands.Add((SegmentKind.Move, Point(next++)));
                    break;
                case 3 when commands.Count > 0:
                    commands.Add((SegmentKind.Close, []));
                    break;
                case 4:
                    if (commands.Count > 0)
                    {
                        paths.Add((commands, filled, stroked));
                    }

                    commands = [];
                    (filled, stroked) = (true, true);
                    break;
                case 5:
                {
                    // An escape: no fill or no line for the path, or vertices of a kind not drawn (as lines).
                    var escape = (info >> 8) & 0x1F;
                    var points = info & 0xFF;
                    if (escape == 0xA)
                    {
                        filled = false;
                    }
                    else if (escape == 0xB)
                    {
                        stroked = false;
                    }

                    for (var i = 0; i < points && next < vertices.Count; i++)
                    {
                        var point = Point(next++);
                        if (i == points - 1 && commands.Count > 0)
                        {
                            commands.Add((SegmentKind.Line, point));
                        }
                    }

                    break;
                }
            }
        }

        if (commands.Count > 0)
        {
            paths.Add((commands, filled, stroked));
        }

        return paths.Count > 0 ? new FreeformGeometry(paths) : null;
    }

    /// <inheritdoc/>
    public override IReadOnlyList<GeometryPath> Build(double width, double height)
    {
        var result = new List<GeometryPath>();
        foreach (var (commands, filled, stroked) in _paths)
        {
            var b = new PathBuilder();
            var started = false;
            foreach (var (kind, p) in commands)
            {
                switch (kind)
                {
                    case SegmentKind.Move:
                        b.MoveTo(p[0] * width, p[1] * height);
                        started = true;
                        break;
                    case SegmentKind.Line when started:
                        b.LineTo(p[0] * width, p[1] * height);
                        break;
                    case SegmentKind.Cubic when started:
                        b.CubicTo(p[0] * width, p[1] * height, p[2] * width, p[3] * height, p[4] * width, p[5] * height);
                        break;
                    case SegmentKind.Close when started:
                        b.Close();
                        break;
                }
            }

            if (b.Segments.Count > 0)
            {
                result.Add(b.ToPath(filled, stroked));
            }
        }

        return result;
    }
}

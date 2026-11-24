using System.Buffers;
using System.Buffers.Binary;
using RaiCore.Pdf.DrawingML;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Metafiles;
using RaiCore.Pdf.Sheets.Drawings;

namespace RaiCore.Pdf.Sheets.Xls;

/// <summary>
/// The drawing records of a binary worksheet, as they come: the OfficeArt data of its MSODRAWING records (one drawing
/// split over them), and its OBJ and TXO records, each with how much of the drawing data came before it (an OBJ
/// follows the record that holds its shape's client data, a TXO the one that holds its shape's client text box).
/// </summary>
internal sealed class XlsDrawingRecords
{
    private readonly ArrayBufferWriter<byte> _data = new();

    /// <summary>The OBJ records: the drawing data before each, its object type, whether it prints and is an embedded object.</summary>
    public List<(long At, int Type, bool Prints, bool Embedded)> Objects { get; } = [];

    /// <summary>The text of the TXO records: the drawing data before each, its text and runs, and its alignment.</summary>
    public List<(long At, XlsText Text)> Texts { get; } = [];

    /// <summary>The drawing data so far.</summary>
    public byte[] Data => _data.WrittenSpan.ToArray();

    /// <summary>Appends an MSODRAWING record's data.</summary>
    public void AddDrawing(ReadOnlySpan<byte> data) => _data.Write(data);

    /// <summary>Notes an OBJ record (BIFF8: its FtCmo first).</summary>
    public void AddObject(ReadOnlySpan<byte> data, bool embedded)
    {
        if (data.Length >= 10 && BiffReader.U16(data, 0) == 0x15)
        {
            Objects.Add((_data.WrittenCount, BiffReader.U16(data, 4), (BiffReader.U16(data, 8) & 0x10) != 0, embedded));
        }
        else
        {
            Objects.Add((_data.WrittenCount, -1, false, false));
        }
    }

    /// <summary>Notes a TXO record (its text and formatting runs follow in its CONTINUE records).</summary>
    public void AddText(ReadOnlySpan<byte> data, IReadOnlyList<int> breaks)
    {
        if (data.Length >= 14)
        {
            Texts.Add((_data.WrittenCount, ReadText(data, breaks)));
        }
    }

    /// <summary>The text of a TXO record: its characters (in its first CONTINUE record), formatting runs (in its last) and alignment.</summary>
    public static XlsText ReadText(ReadOnlySpan<byte> data, IReadOnlyList<int> breaks)
    {
        if (data.Length < 14)
        {
            return new XlsText(string.Empty, [], 0, 0);
        }

        var flags = BiffReader.U16(data, 0);
        var count = BiffReader.U16(data, 10);
        var text = string.Empty;
        var runs = new List<(int First, int Font)>();
        if (breaks.Count > 0 && count > 0)
        {
            var position = breaks[0];
            text = BiffReader.ReadUnicode(data, breaks, ref position, count);
            if (breaks.Count > 1)
            {
                // The formatting runs: the first character and the font of each, 8 bytes a run, the last one ending them.
                for (var at = breaks[^1]; at + 4 <= data.Length && runs.Count < 4096; at += 8)
                {
                    runs.Add((BiffReader.U16(data, at), BiffReader.U16(data, at + 2)));
                }
            }
        }

        return new XlsText(text, runs, (flags >> 1) & 0x7, (flags >> 4) & 0x7);
    }
}

/// <summary>The text of a text box (a TXO record): its characters, formatting runs (first character, font index) and alignment.</summary>
internal sealed record XlsText(string Text, IReadOnlyList<(int First, int Font)> Runs, int Horizontal, int Vertical);

/// <summary>
/// Reads the drawing of a binary worksheet ([MS-ODRAW] in [MS-XLS] 2.4.168 MSODRAWING): its pictures (from the
/// workbook's BLIP store: PNG, JPEG, DIB, TIFF, WMF and EMF; cropped), its simple shapes and text boxes (rectangles,
/// ovals, lines, arrows and the other common types, with their fill, line and text) and its groups, anchored to the
/// sheet's cells in fractions of them. Comments' shapes and objects that do not print are skipped; charts, controls,
/// embedded objects, free forms and pictures that cannot be read are counted as not supported.
/// </summary>
internal sealed class XlsDrawing
{
    private const int SpgrContainer = 0xF003;
    private const int SpContainer = 0xF004;
    private const int Fspgr = 0xF009;
    private const int Fsp = 0xF00A;
    private const int Fopt = 0xF00B;
    private const int ClientTextbox = 0xF00D;
    private const int ChildAnchor = 0xF00F;
    private const int ClientAnchor = 0xF010;
    private const int ClientData = 0xF011;
    private const int TertiaryFopt = 0xF122;
    private const double Emu = 12700;

    /// <summary>The OBJ types of shapes that are drawn: line, rectangle, oval, arc, text box, and Office drawing shapes.</summary>
    private static readonly HashSet<int> DrawnTypes = [0x01, 0x02, 0x03, 0x06, 0x1E];

    /// <summary>The preset geometry of the common OfficeArt shape types (MSOSPT).</summary>
    private static readonly Dictionary<int, string> Presets = new()
    {
        [1] = "rect", [2] = "roundRect", [3] = "ellipse", [4] = "diamond", [5] = "triangle", [6] = "rtTriangle",
        [7] = "parallelogram", [8] = "trapezoid", [9] = "hexagon", [10] = "octagon", [11] = "plus", [12] = "star5",
        [13] = "rightArrow", [15] = "homePlate", [16] = "cube", [20] = "line", [32] = "straightConnector1",
        [55] = "chevron", [56] = "pentagon", [66] = "leftArrow", [67] = "downArrow", [68] = "upArrow",
        [69] = "leftRightArrow", [70] = "upDownArrow", [109] = "flowChartProcess", [110] = "flowChartDecision",
        [116] = "flowChartTerminator", [120] = "flowChartConnector", [202] = "rect",
    };

    private readonly XlsWorkbook _workbook;
    private readonly ResourceLimits _limits;
    private readonly UnsupportedContent _unsupported;
    private readonly XlsDrawingRecords _records;
    private readonly byte[] _data;
    private int _nextObject;
    private int _nextText;
    private long _walked;

    private XlsDrawing(XlsWorkbook workbook, XlsDrawingRecords records, ResourceLimits limits, UnsupportedContent unsupported)
    {
        _workbook = workbook;
        _records = records;
        _data = records.Data;
        _limits = limits;
        _unsupported = unsupported;
    }

    /// <summary>A shape of the drawing as read: its record's flags, type, properties, anchors and object.</summary>
    private sealed class Shape
    {
        public int Type { get; set; }

        public int Flags { get; set; }

        public Dictionary<int, (int Value, int Complex)> Properties { get; } = [];

        public (int Col1, int Dx1, int Row1, int Dy1, int Col2, int Dx2, int Row2, int Dy2)? Anchor { get; set; }

        public PdfRectangle? Child { get; set; }

        public PdfRectangle? GroupBounds { get; set; }

        public (int Type, bool Prints, bool Embedded)? Object { get; set; }

        public XlsText? Text { get; set; }

        public List<Shape> Children { get; } = [];

        public bool IsGroup => (Flags & 0x1) != 0;

        public int Value(int id, int fallback) => Properties.TryGetValue(id, out var p) ? p.Value : fallback;

        /// <summary>A bit of a boolean property set whose "use" bit (16 higher) is set, or the fallback.</summary>
        public bool Flag(int id, int bit, bool fallback) => Properties.TryGetValue(id, out var p)
            && ((p.Value & (1 << (bit + 16))) != 0 || (p.Value & unchecked((int)0xFFFF0000)) == 0) ? (p.Value & (1 << bit)) != 0 : fallback;
    }

    /// <summary>Reads the drawings of a worksheet's drawing records; what cannot be drawn is counted.</summary>
    public static List<SheetDrawing> Read(XlsWorkbook workbook, XlsDrawingRecords records, ResourceLimits limits, UnsupportedContent unsupported)
    {
        var drawing = new XlsDrawing(workbook, records, limits, unsupported);
        var result = new List<SheetDrawing>();
        var shapes = new List<Shape>();
        drawing.Walk(0, drawing._data.Length, shapes, null, 0);
        foreach (var shape in shapes)
        {
            if (shape.Anchor is not { } anchor || drawing.Item(shape, 0) is not { } item)
            {
                continue;
            }

            result.Add(new SheetDrawing(AnchorKind.TwoCell, new AnchorEdge(anchor.Col1, 0, anchor.Dx1 / 1024.0), new AnchorEdge(anchor.Row1, 0, anchor.Dy1 / 256.0), item)
            {
                Right = new AnchorEdge(anchor.Col2, 0, anchor.Dx2 / 1024.0),
                Bottom = new AnchorEdge(anchor.Row2, 0, anchor.Dy2 / 256.0),
            });
        }

        // OBJ records no shape claimed (a damaged drawing) are counted by their kind.
        for (var i = drawing._nextObject; i < records.Objects.Count; i++)
        {
            drawing.CountObject(records.Objects[i]);
        }

        return result;
    }

    /// <summary>
    /// Walks the records in [<paramref name="start"/>, <paramref name="end"/>) of the drawing data: the shapes of a group
    /// container go to <paramref name="shapes"/> (its first shape, the group's own, describes <paramref name="group"/>).
    /// </summary>
    private void Walk(int start, int end, List<Shape> shapes, Shape? group, int depth)
    {
        if (depth > _limits.MaxNestingDepth)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), _limits.MaxNestingDepth,
                $"The drawing nests deeper than ResourceLimits.MaxNestingDepth allows ({_limits.MaxNestingDepth}).");
        }

        var position = start;
        var first = true;
        while (position + 8 <= end)
        {
            var (type, instance, version, length) = Header(position);
            var body = position + 8;
            var bodyEnd = (int)Math.Min((long)body + length, end);
            if (type == SpgrContainer)
            {
                var children = new List<Shape>();
                var owner = new Shape();
                Walk(body, bodyEnd, children, owner, depth + 1);
                if (group is null && depth <= 1)
                {
                    // The drawing's top group: its first shape is the patriarch, the others are the sheet's shapes.
                    shapes.AddRange(children);
                }
                else
                {
                    owner.Children.AddRange(children);
                    shapes.Add(owner);
                }
            }
            else if (type == SpContainer)
            {
                var shape = new Shape();
                ReadShape(body, bodyEnd, shape);
                if (group is not null && first && shape.IsGroup)
                {
                    // A group's own shape: its anchor and the coordinates of its children.
                    group.Flags = shape.Flags;
                    group.Type = shape.Type;
                    group.Anchor = shape.Anchor;
                    group.Child = shape.Child;
                    group.GroupBounds = shape.GroupBounds;
                    group.Object = shape.Object;
                    foreach (var property in shape.Properties)
                    {
                        group.Properties[property.Key] = property.Value;
                    }
                }
                else if ((shape.Flags & 0x4) == 0)
                {
                    shapes.Add(shape); // Not the patriarch.
                }
            }
            else if (version == 0xF)
            {
                Walk(body, bodyEnd, shapes, group, depth + 1);
            }

            first &= type != SpContainer;
            if (bodyEnd <= position)
            {
                break;
            }

            position = bodyEnd;
        }
    }

    /// <summary>Reads a shape container's records.</summary>
    private void ReadShape(int start, int end, Shape shape)
    {
        var position = start;
        while (position + 8 <= end)
        {
            var (type, instance, _, length) = Header(position);
            var body = position + 8;
            var bodyEnd = (int)Math.Min((long)body + length, end);
            var span = _data.AsSpan(body, bodyEnd - body);
            switch (type)
            {
                case Fsp when span.Length >= 8:
                    shape.Type = instance;
                    shape.Flags = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
                    break;
                case Fspgr when span.Length >= 16:
                    shape.GroupBounds = Rectangle(span);
                    break;
                case Fopt or TertiaryFopt:
                    var count = Math.Clamp(instance, 0, span.Length / 6);
                    for (var i = 0; i < count; i++)
                    {
                        var id = BinaryPrimitives.ReadUInt16LittleEndian(span[(6 * i)..]);
                        shape.Properties.TryAdd(id & 0x3FFF, (BinaryPrimitives.ReadInt32LittleEndian(span[((6 * i) + 2)..]), id & 0x8000));
                    }

                    break;
                case ClientAnchor when span.Length >= 18:
                    static int U(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);
                    shape.Anchor = (U(span, 2), Math.Min(U(span, 4), 1024), U(span, 6), Math.Min(U(span, 8), 256),
                        U(span, 10), Math.Min(U(span, 12), 1024), U(span, 14), Math.Min(U(span, 16), 256));
                    break;
                case ChildAnchor when span.Length >= 16:
                    shape.Child = Rectangle(span);
                    break;
                case ClientData:
                    shape.Object = NextObject(bodyEnd);
                    break;
                case ClientTextbox:
                    shape.Text = NextText(bodyEnd);
                    break;
            }

            if (bodyEnd <= position)
            {
                break;
            }

            position = bodyEnd;
        }
    }

    /// <summary>The OBJ record that follows the drawing data up to <paramref name="end"/>; OBJ records it skips are counted.</summary>
    private (int Type, bool Prints, bool Embedded)? NextObject(int end)
    {
        var objects = _records.Objects;
        while (_nextObject < objects.Count && objects[_nextObject].At < end)
        {
            CountObject(objects[_nextObject++]);
        }

        if (_nextObject >= objects.Count)
        {
            return null;
        }

        var (_, type, prints, embedded) = objects[_nextObject++];
        return (type, prints, embedded);
    }

    private XlsText? NextText(int end)
    {
        var texts = _records.Texts;
        while (_nextText < texts.Count && texts[_nextText].At < end)
        {
            _nextText++;
        }

        return _nextText < texts.Count ? texts[_nextText++].Text : null;
    }

    /// <summary>Counts an object that is not drawn by its kind (not comments' shapes, groups, or objects that do not print).</summary>
    private void CountObject((long At, int Type, bool Prints, bool Embedded) record)
    {
        if (record.Prints && record.Type is not (0x00 or 0x19 or -1))
        {
            _unsupported.Add(record.Type switch
            {
                0x05 => SheetDiagnosticKind.Chart,
                0x08 when record.Embedded => SheetDiagnosticKind.OleObject,
                0x08 => SheetDiagnosticKind.Picture,
                _ => SheetDiagnosticKind.Shape,
            });
        }
    }

    /// <summary>The item a shape draws, or <c>null</c> (counted, unless it does not print or is a comment's shape).</summary>
    private DrawingItem? Item(Shape shape, int depth)
    {
        if (++_walked > _limits.MaxElements)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxElements), _limits.MaxElements,
                $"The drawing has more shapes than ResourceLimits.MaxElements allows ({_limits.MaxElements}).");
        }

        if (shape.Object is not { } obj || !obj.Prints || shape.Flag(0x03BF, 1, false))
        {
            return null; // A hidden shape, or one that does not print. (A comment's shape, OBJ type 0x19, is no type drawn.)
        }

        var rotation = shape.Value(0x0004, 0) / 65536.0;
        var (flipH, flipV) = ((shape.Flags & 0x40) != 0, (shape.Flags & 0x80) != 0);
        if (shape.IsGroup)
        {
            var children = new List<(DrawingItem, PdfRectangle)>();
            foreach (var child in shape.Children)
            {
                if (child.Child is { } box && depth < _limits.MaxNestingDepth && Item(child, depth + 1) is { } item)
                {
                    children.Add((item, box));
                }
            }

            return children.Count == 0 || shape.GroupBounds is not { } bounds ? null
                : new GroupItem(children, bounds) { Rotation = rotation, FlipH = flipH, FlipV = flipV };
        }

        if (obj.Type == 0x08 && !obj.Embedded)
        {
            if (Picture(shape.Value(0x0104, 0)) is not { } image)
            {
                CountObject((0, obj.Type, true, false));
                return null;
            }

            static double Crop(Shape s, int id) => Math.Clamp(s.Value(id, 0) / 65536.0, -1, 1);
            return new PictureItem(image)
            {
                Crop = (Crop(shape, 0x0102), Crop(shape, 0x0100), Crop(shape, 0x0103), Crop(shape, 0x0101)),
                Line = shape.Flag(0x01FF, 3, false) ? Line(shape) : null,
                Rotation = rotation,
                FlipH = flipH,
                FlipV = flipV,
            };
        }

        if (!DrawnTypes.Contains(obj.Type) || !Presets.TryGetValue(shape.Type, out var preset) || shape.Properties.ContainsKey(0x0145))
        {
            CountObject((0, obj.Type, true, obj.Embedded));
            return null;
        }

        var geometry = new PresetGeometry(preset, shape.Properties.TryGetValue(0x0147, out var adjust) && preset == "roundRect"
            ? new Dictionary<string, double> { ["adj"] = adjust.Value / 21600.0 * 100000 } : null);
        return new ShapeItem(geometry)
        {
            Fill = shape.Flag(0x01BF, 4, true) && !geometry.IsLine ? Color(shape.Value(0x0181, 0xFFFFFF), PdfColor.White) : null,
            Line = shape.Flag(0x01FF, 3, true) ? Line(shape) : null,
            Text = Text(shape),
            Rotation = rotation,
            FlipH = flipH,
            FlipV = flipV,
        };
    }

    private ShapeLine Line(Shape shape) =>
        new(Math.Clamp(shape.Value(0x01CB, 9525) / Emu, 0, 1000), Color(shape.Value(0x01C0, 0), PdfColor.Black), shape.Value(0x01CE, 0) switch
        {
            1 => [4, 3],
            2 => [1, 1],
            3 => [4, 3, 1, 3],
            4 => [4, 3, 1, 3, 1, 3],
            6 => [8, 3],
            7 => [8, 3, 1, 3],
            8 => [8, 3, 1, 3, 1, 3],
            _ => [],
        });

    /// <summary>A text box's text in the workbook's fonts (its runs'), aligned as it says.</summary>
    private ShapeText? Text(Shape shape)
    {
        if (shape.Text is not { Text.Length: > 0 } text)
        {
            return null;
        }

        var styles = _workbook.Styles;
        var alignment = text.Horizontal switch
        {
            2 => TextAlignment.Center,
            3 => TextAlignment.Right,
            4 => TextAlignment.Justify,
            _ => TextAlignment.Left,
        };
        var paragraphs = new List<ShapeParagraph>();
        var runs = new List<ShapeRun>();
        var boundaries = text.Runs.Where(r => r.First < text.Text.Length).OrderBy(r => r.First).ToList();
        if (boundaries.Count == 0 || boundaries[0].First > 0)
        {
            boundaries.Insert(0, (0, 0));
        }

        for (var i = 0; i < boundaries.Count; i++)
        {
            var from = boundaries[i].First;
            var to = i + 1 < boundaries.Count ? boundaries[i + 1].First : text.Text.Length;
            if (to <= from)
            {
                continue;
            }

            // Font indexes skip 4 ([MS-XLS] 2.5.129): index 4 is not stored.
            var index = boundaries[i].Font;
            var font = styles.Fonts.ElementAtOrDefault(index >= 4 ? index - 1 : index) ?? styles.Fonts.FirstOrDefault();
            var color = font is { Color: not 0x7FFF } ? styles.PaletteColor(font.Color) ?? PdfColor.Black : PdfColor.Black;
            var lines = text.Text[from..to].Replace("\r\n", "\n").Split('\n');
            for (var l = 0; l < lines.Length; l++)
            {
                if (l > 0)
                {
                    paragraphs.Add(new ShapeParagraph(runs, alignment));
                    runs = [];
                }

                runs.Add(new ShapeRun(lines[l], font?.Size ?? 10, font?.Bold ?? false, font?.Italic ?? false,
                    font is { Underline: not CellUnderline.None }, color, font?.Name));
            }
        }

        paragraphs.Add(new ShapeParagraph(runs, alignment));
        return new ShapeText(paragraphs)
        {
            Anchor = text.Vertical switch
            {
                2 => VerticalAlignment.Middle,
                3 => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Top,
            },
        };
    }

    /// <summary>
    /// A color (OfficeArtCOLORREF): RGB, or an index into the workbook's palette (64 and 65 are the system's
    /// foreground and background); system colors are the fallback.
    /// </summary>
    private PdfColor Color(int value, PdfColor fallback)
    {
        var flags = (value >> 24) & 0xFF;
        var (r, g, b) = ((byte)value, (byte)(value >> 8), (byte)(value >> 16));
        if ((flags & 0x08) != 0 || (flags & 0x01) != 0)
        {
            return r switch
            {
                64 => PdfColor.Black,
                65 => PdfColor.White,
                _ => _workbook.Styles.PaletteColor(r) ?? fallback,
            };
        }

        return (flags & 0x10) != 0 ? fallback : PdfColor.FromRgb(r, g, b);
    }

    /// <summary>The picture of a BLIP store entry (from 1), loaded once; <c>null</c> when it cannot be drawn.</summary>
    private PdfImage? Picture(int index)
    {
        if (_workbook.Pictures.TryGetValue(index, out var cached))
        {
            return cached;
        }

        PdfImage? image = null;
        try
        {
            if (Blip(_workbook.DrawingGroup, index) is { } data)
            {
                image = PdfImage.IsMetafile(data) ? PdfImage.LoadMetafile(data, new MetafileOptions()) : PdfImage.Load(data);
            }
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException or ArgumentException
            or ResourceLimitExceededException { LimitName: nameof(ResourceLimits.MaxImagePixels) })
        {
            image = null;
        }

        _workbook.Pictures[index] = image;
        return image;
    }

    /// <summary>
    /// The file of a BLIP store entry (from 1) in the drawing group's data ([MS-ODRAW] 2.2.32 OfficeArtFBSE with the
    /// BLIP in it): a metafile decompressed, a bitmap as stored (a DIB with a file header added); <c>null</c> when
    /// there is none or it is a format that cannot be drawn (PICT).
    /// </summary>
    internal byte[]? Blip(byte[] group, int index)
    {
        if (index < 1 || FindStore(group, 0, group.Length, 0) is not var (start, end))
        {
            return null;
        }

        var position = start;
        for (var i = 1; position + 8 <= end; i++)
        {
            var (_, _, _, length) = Header(group, position);
            var body = position + 8;
            var bodyEnd = (int)Math.Min((long)body + length, end);
            if (i == index)
            {
                // The entry's 36 bytes, its name, then the BLIP record.
                var name = body + 33 < bodyEnd ? group[body + 33] : 0;
                var blip = body + 36 + name;
                return blip + 8 <= bodyEnd ? Decode(group, blip, bodyEnd) : null;
            }

            if (bodyEnd <= position)
            {
                break;
            }

            position = bodyEnd;
        }

        return null;
    }

    /// <summary>The body of the BLIP store container (0xF001) in the drawing group container, or <c>null</c>.</summary>
    private static (int Start, int End)? FindStore(byte[] group, int start, int end, int depth)
    {
        var position = start;
        while (position + 8 <= end && depth <= 2)
        {
            var (type, _, version, length) = Header(group, position);
            var body = position + 8;
            var bodyEnd = (int)Math.Min((long)body + length, end);
            if (type == 0xF001)
            {
                return (body, bodyEnd);
            }

            if (version == 0xF && FindStore(group, body, bodyEnd, depth + 1) is { } found)
            {
                return found;
            }

            if (bodyEnd <= position)
            {
                break;
            }

            position = bodyEnd;
        }

        return null;
    }

    /// <summary>The picture file of a BLIP record at an offset (its UIDs and tag skipped).</summary>
    private byte[]? Decode(byte[] data, int position, int end)
    {
        var (type, instance, _, length) = Header(data, position);
        var body = position + 8;
        var bodyEnd = (int)Math.Min((long)body + length, end);
        var uids = 16 * ((instance & 1) == 1 ? 2 : 1);
        var span = data.AsSpan(body, Math.Max(0, bodyEnd - body));
        switch (type)
        {
            case 0xF01A or 0xF01B when span.Length > uids:
                return MetafileBlip.Decode(span[uids..], wmf: type == 0xF01B, _limits.MaxDecompressedBytes);
            case 0xF01D or 0xF01E or 0xF029 or 0xF02A when span.Length > uids + 1:
                return span[(uids + 1)..].ToArray();
            case 0xF01F when span.Length > uids + 1 + 40:
                return WithFileHeader(span[(uids + 1)..]);
            default:
                return null; // PICT, or damaged.
        }
    }

    /// <summary>A device-independent bitmap as a BMP file: with the 14-byte file header that says where its pixels start.</summary>
    internal static byte[] WithFileHeader(ReadOnlySpan<byte> dib)
    {
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var bits = dib.Length >= 16 ? BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]) : 0;
        var used = dib.Length >= 36 && headerSize >= 40 ? BinaryPrimitives.ReadInt32LittleEndian(dib[32..]) : 0;
        var compression = dib.Length >= 20 && headerSize >= 40 ? BinaryPrimitives.ReadInt32LittleEndian(dib[16..]) : 0;
        var colors = used is > 0 and <= 256 ? used : bits is > 0 and <= 8 ? 1 << bits : 0;
        var masks = compression == 3 && headerSize == 40 ? 12 : 0;
        var offset = 14 + Math.Clamp(headerSize, 12, 1024) + (4 * colors) + masks;
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), Math.Min(offset, file.Length));
        dib.CopyTo(file.AsSpan(14));
        return file;
    }

    private (int Type, int Instance, int Version, uint Length) Header(int position) => Header(_data, position);

    /// <summary>An OfficeArt record header: its type, instance, version and body length.</summary>
    private static (int Type, int Instance, int Version, uint Length) Header(byte[] data, int position)
    {
        var versionInstance = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
        return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 2)), versionInstance >> 4, versionInstance & 0xF,
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4)));
    }

    /// <summary>A rectangle of four 32-bit coordinates (left, top, right, bottom).</summary>
    private static PdfRectangle Rectangle(ReadOnlySpan<byte> span)
    {
        var (l, t, r, b) = (BinaryPrimitives.ReadInt32LittleEndian(span), BinaryPrimitives.ReadInt32LittleEndian(span[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[8..]), BinaryPrimitives.ReadInt32LittleEndian(span[12..]));
        return new PdfRectangle(l, t, Math.Max(0, (double)r - l), Math.Max(0, (double)b - t));
    }
}

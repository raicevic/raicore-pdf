namespace RaiCore.Pdf.Slides.Ppt;

/// <summary>The OfficeArt shape property ids ([MS-ODRAW] 2.3) the reader uses.</summary>
internal static class Op
{
    public const int Rotation = 0x0004;
    public const int TextLeft = 0x0081;
    public const int TextTop = 0x0082;
    public const int TextRight = 0x0083;
    public const int TextBottom = 0x0084;
    public const int WrapText = 0x0085;
    public const int AnchorText = 0x0087;
    public const int TextFlow = 0x0088;
    public const int WordArtText = 0x00C0;
    public const int WordArtSize = 0x00C3;
    public const int WordArtFont = 0x00C5;
    public const int WordArtBooleans = 0x00FF;
    public const int CropTop = 0x0100;
    public const int CropBottom = 0x0101;
    public const int CropLeft = 0x0102;
    public const int CropRight = 0x0103;
    public const int Pib = 0x0104;
    public const int PibName = 0x0105;
    public const int GeoLeft = 0x0140;
    public const int GeoTop = 0x0141;
    public const int GeoRight = 0x0142;
    public const int GeoBottom = 0x0143;
    public const int ShapePath = 0x0144;
    public const int Vertices = 0x0145;
    public const int SegmentInfo = 0x0146;
    public const int Adjust1 = 0x0147;
    public const int FillType = 0x0180;
    public const int FillColor = 0x0181;
    public const int FillOpacity = 0x0182;
    public const int FillBackColor = 0x0183;
    public const int FillBackOpacity = 0x0184;
    public const int FillBlip = 0x0186;
    public const int FillAngle = 0x018B;
    public const int FillFocus = 0x018C;
    public const int FillShadeColors = 0x0197;
    public const int FillBooleans = 0x01BF;
    public const int LineColor = 0x01C0;
    public const int LineOpacity = 0x01C1;
    public const int LineWidth = 0x01CB;
    public const int LineDashing = 0x01CE;
    public const int LineStartArrowhead = 0x01D0;
    public const int LineEndArrowhead = 0x01D1;
    public const int LineStartArrowWidth = 0x01D2;
    public const int LineStartArrowLength = 0x01D3;
    public const int LineEndArrowWidth = 0x01D4;
    public const int LineEndArrowLength = 0x01D5;
    public const int LineJoinStyle = 0x01D6;
    public const int LineEndCapStyle = 0x01D7;
    public const int LineBooleans = 0x01FF;
    public const int MasterShape = 0x0301;
    public const int Name = 0x0380;
    public const int Description = 0x0381;
    public const int GroupBooleans = 0x03BF;

    // Bits of the boolean property sets (the "use" bit of each is 16 higher).
    public const int FilledBit = 4;
    public const int LineBit = 3;
    public const int HiddenBit = 1;
}

/// <summary>The flags of an OfficeArt shape record (<c>OfficeArtFSP</c>).</summary>
[Flags]
internal enum ShapeFlags
{
    None = 0,
    Group = 0x1,
    Child = 0x2,
    Patriarch = 0x4,
    Deleted = 0x8,
    OleShape = 0x10,
    HaveMaster = 0x20,
    FlipH = 0x40,
    FlipV = 0x80,
    Connector = 0x100,
    HaveAnchor = 0x200,
    Background = 0x400,
}

/// <summary>A shape property: its value, and the data of a complex one.</summary>
/// <param name="Value">The value (for a complex property, the size of its data).</param>
/// <param name="Complex">The data of a complex property, or <c>null</c>.</param>
internal readonly record struct ShapeProperty(int Value, byte[]? Complex);

/// <summary>A rectangle of an anchor, in the units it was stored in.</summary>
internal readonly record struct AnchorRect(int Left, int Top, int Right, int Bottom);

/// <summary>
/// An OfficeArt shape (<c>OfficeArtSpContainer</c>, or a group's <c>OfficeArtSpgrContainer</c>): its type, id and
/// flags, its properties (primary, secondary and tertiary tables together), its anchor, the PowerPoint client data
/// and text box records, and the children of a group.
/// </summary>
internal sealed class EscherShape
{
    /// <summary>The shape type (<c>MSOSPT</c>).</summary>
    public int ShapeType { get; set; }

    /// <summary>The shape id.</summary>
    public int Id { get; set; }

    /// <summary>The flags.</summary>
    public ShapeFlags Flags { get; set; }

    /// <summary>The properties by id.</summary>
    public Dictionary<int, ShapeProperty> Properties { get; } = [];

    /// <summary>The anchor in the slide (master units, 576 per inch), if any.</summary>
    public AnchorRect? ClientAnchor { get; set; }

    /// <summary>The anchor in the parent group's coordinates, if any.</summary>
    public AnchorRect? ChildAnchor { get; set; }

    /// <summary>A group's coordinate space for its children.</summary>
    public AnchorRect? GroupBounds { get; set; }

    /// <summary>The PowerPoint client data (placeholder, links), if any.</summary>
    public Record? ClientData { get; set; }

    /// <summary>The PowerPoint client text box (text or a reference to the slide's outline text), if any.</summary>
    public Record? ClientTextbox { get; set; }

    /// <summary>The shape on the master this shape takes its properties from, if any.</summary>
    public EscherShape? Master { get; set; }

    /// <summary>Whether this is a group.</summary>
    public bool IsGroup => (Flags & ShapeFlags.Group) != 0;

    /// <summary>The children of a group, in z-order.</summary>
    public List<EscherShape> Children { get; } = [];

    /// <summary>A property of the shape or, when it has none, of its master shape.</summary>
    public ShapeProperty? Get(int id)
    {
        var shape = this;
        for (var depth = 0; shape is not null && depth < 8; depth++, shape = shape.Master)
        {
            if (shape.Properties.TryGetValue(id, out var property))
            {
                return property;
            }
        }

        return null;
    }

    /// <summary>A property's value, or a default.</summary>
    public int Value(int id, int fallback) => Get(id) is { } property ? property.Value : fallback;

    /// <summary>
    /// A bit of a boolean property set, from the shape, its master shape or the drawing's defaults (the first that sets
    /// its "use" bit, 16 higher); <c>null</c> when none does. A set without any "use" bits (old files) sets all its bits.
    /// </summary>
    public bool? Flag(int id, int bit, IReadOnlyDictionary<int, ShapeProperty>? defaults)
    {
        var shape = this;
        for (var depth = 0; shape is not null && depth < 8; depth++, shape = shape.Master)
        {
            if (shape.Properties.TryGetValue(id, out var property) && Bit(property.Value, bit) is { } value)
            {
                return value;
            }
        }

        return defaults is not null && defaults.TryGetValue(id, out var fallback) ? Bit(fallback.Value, bit) : null;
    }

    private static bool? Bit(int value, int bit) =>
        (value & (1 << (bit + 16))) != 0 || (value & unchecked((int)0xFFFF0000)) == 0 ? (value & (1 << bit)) != 0 : null;

    /// <summary>Reads the property table of an <c>OfficeArtFOPT</c> (or secondary, tertiary) record into a dictionary.</summary>
    public static void ReadProperties(ReadOnlySpan<byte> body, int count, Dictionary<int, ShapeProperty> properties)
    {
        count = Math.Clamp(count, 0, body.Length / 6);
        var complex = 6 * count;
        for (var i = 0; i < count; i++)
        {
            var id = Bytes.U16(body, 6 * i);
            var value = Bytes.I32(body, (6 * i) + 2);
            byte[]? data = null;
            if ((id & 0x8000) != 0)
            {
                // Complex data follows the table, in the order of the properties.
                var length = (int)Math.Clamp((uint)value, 0, (uint)Math.Max(body.Length - complex, 0));
                data = body.Slice(complex, length).ToArray();
                complex += length;
            }

            properties.TryAdd(id & 0x3FFF, new ShapeProperty(value, data));
        }
    }

    /// <summary>
    /// The elements of an array property (<c>IMsoArray</c>: a count, an allocated count and an element size, then
    /// the elements), as byte slices of the element size; at most as many as the data holds.
    /// </summary>
    public static IEnumerable<(byte[] Data, int Offset, int Size)> Elements(byte[]? data)
    {
        if (data is null || data.Length < 6)
        {
            yield break;
        }

        var count = Bytes.U16(data, 0);
        var size = Bytes.U16(data, 4);
        if (size == 0xFFF0)
        {
            // Truncated 2-byte coordinates.
            size = 4;
        }

        if (size is 0 or > 64)
        {
            yield break;
        }

        for (var i = 0; i < count && 6 + ((i + 1) * size) <= data.Length; i++)
        {
            yield return (data, 6 + (i * size), size);
        }
    }
}

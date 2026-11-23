using System.Buffers.Binary;

namespace RaiCore.Pdf.Documents.Doc;

/// <summary>A picture's image found in OfficeArt records: its bytes and the kind of BLIP they came from.</summary>
/// <param name="Data">The image file (PNG, JPEG, TIFF or BMP), or <c>null</c> for a format that cannot be drawn.</param>
/// <param name="Kind">The BLIP kind (PNG, JPEG, EMF, WMF, PICT, DIB, TIFF).</param>
internal readonly record struct DocBlip(byte[]? Data, string Kind);

/// <summary>A floating shape ([MS-DOC] 2.9.253 Spa with its OfficeArt shape): its place and picture or text box.</summary>
/// <param name="Left">The left edge in twips.</param>
/// <param name="Top">The top edge in twips.</param>
/// <param name="Width">The width in twips.</param>
/// <param name="Height">The height in twips.</param>
/// <param name="PageRelative">Whether the position is relative to the page (rather than the margin or column).</param>
/// <param name="Blip">The index (1-based) of its picture in the BLIP store, or 0.</param>
/// <param name="TextBox">The text box story (1-based, the high word of <c>lTxid</c>), or 0.</param>
internal readonly record struct DocShape(int Left, int Top, int Width, int Height, bool PageRelative, int Blip, int TextBox);

/// <summary>
/// Pictures and drawings: inline pictures ([MS-DOC] 2.9.192 PICFAndOfficeArtData in the Data stream) and the drawing
/// layer ([MS-ODRAW] OfficeArtContent: the BLIP store and the shapes' properties) for floating pictures and text boxes.
/// Records are walked without recursion, each step moving forward, and every length is checked.
/// </summary>
internal sealed class DocDrawings
{
    /// <summary>The most records walked in one structure; more is damage.</summary>
    private const int MaxRecords = 100_000;

    private const ushort FbseType = 0xF007;

    private readonly DocFile _file;
    private readonly List<(int Offset, int Length)> _store = [];
    private readonly Dictionary<int, (int Blip, int TextBox)> _shapes = [];

    private DocDrawings(DocFile file)
    {
        _file = file;
        var content = file.TableData(50);
        if (content.Length >= 8)
        {
            ReadContent(content);
        }
    }

    public static DocDrawings Read(DocFile file) => new(file);

    /// <summary>The floating shapes of the main document (or the headers) by the character position of their anchor.</summary>
    public Dictionary<int, DocShape> Anchors(bool headers)
    {
        var result = new Dictionary<int, DocShape>();
        var plc = _file.TableData(headers ? 41 : 40);
        var count = (plc.Length - 4) / 30;
        for (var i = 0; i < count; i++)
        {
            var cp = BinaryPrimitives.ReadInt32LittleEndian(plc[(4 * i)..]);
            var spa = plc.Slice((4 * (count + 1)) + (26 * i), 26);
            var spid = BinaryPrimitives.ReadInt32LittleEndian(spa);
            var left = BinaryPrimitives.ReadInt32LittleEndian(spa[4..]);
            var top = BinaryPrimitives.ReadInt32LittleEndian(spa[8..]);
            var right = BinaryPrimitives.ReadInt32LittleEndian(spa[12..]);
            var bottom = BinaryPrimitives.ReadInt32LittleEndian(spa[16..]);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(spa[20..]);
            var (blip, textBox) = _shapes.TryGetValue(spid, out var shape) ? shape : (0, 0);
            result.TryAdd(cp, new DocShape(
                Math.Clamp(left, -31680, 31680), Math.Clamp(top, -31680, 31680),
                (int)Math.Clamp((long)right - left, 0, 31680), (int)Math.Clamp((long)bottom - top, 0, 31680),
                ((flags >> 1) & 3) == 1, blip, textBox));
        }

        return result;
    }

    /// <summary>The picture of a BLIP store entry (1-based), or <c>null</c>.</summary>
    public DocBlip? StoredBlip(int index)
    {
        if (index < 1 || index > _store.Count)
        {
            return null;
        }

        var (offset, length) = _store[index - 1];
        return offset >= 0 ? FindBlip(_file.Table.AsSpan(offset, length), _file.Word) : null;
    }

    /// <summary>
    /// An inline picture ([MS-DOC] 2.9.192): its PICF header (goal size, scale, cropping) and the first picture among
    /// the OfficeArt records after it; <c>null</c> when the structure is damaged.
    /// </summary>
    public (DocBlip Blip, double Width, double Height)? Inline(int location)
    {
        var data = _file.Data;
        if (location < 0 || location > data.Length - 68)
        {
            return null;
        }

        var picf = data.AsSpan(location);
        var total = BinaryPrimitives.ReadInt32LittleEndian(picf);
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(picf[4..]);
        var mapping = BinaryPrimitives.ReadUInt16LittleEndian(picf[6..]);
        if (total < 68 || headerSize < 68 || headerSize > total)
        {
            return null;
        }

        picf = picf[..Math.Min(total, picf.Length)];
        double goalWidth = BinaryPrimitives.ReadInt16LittleEndian(picf[28..]);
        double goalHeight = BinaryPrimitives.ReadInt16LittleEndian(picf[30..]);
        var scaleX = BinaryPrimitives.ReadUInt16LittleEndian(picf[32..]);
        var scaleY = BinaryPrimitives.ReadUInt16LittleEndian(picf[34..]);
        var crop = (BinaryPrimitives.ReadInt16LittleEndian(picf[36..]) + BinaryPrimitives.ReadInt16LittleEndian(picf[40..]),
            BinaryPrimitives.ReadInt16LittleEndian(picf[38..]) + BinaryPrimitives.ReadInt16LittleEndian(picf[42..]));
        var width = Math.Max(0, goalWidth - crop.Item1) * (scaleX == 0 ? 1000 : scaleX) / 1000.0 / 20.0;
        var height = Math.Max(0, goalHeight - crop.Item2) * (scaleY == 0 ? 1000 : scaleY) / 1000.0 / 20.0;
        var position = (int)headerSize;
        if (mapping == 0x66 && position < picf.Length)
        {
            position += 1 + picf[position]; // The picture's name (a linked picture).
        }

        if (mapping is not (0x64 or 0x66) || position >= picf.Length)
        {
            return (new DocBlip(null, mapping is 8 or 0x64 or 0x66 ? "WMF" : "an unknown format"), width, height);
        }

        if (FindBlip(picf[position..], _file.Word) is not { } blip)
        {
            return null;
        }

        return (blip, width, height);
    }

    /// <summary>
    /// The first picture in a sequence of OfficeArt records: a BLIP record, or the BLIP an FBSE holds or points to in
    /// the WordDocument stream (<c>foDelay</c>); containers are entered.
    /// </summary>
    private static DocBlip? FindBlip(ReadOnlySpan<byte> records, byte[] delayStream)
    {
        var position = 0;
        for (var steps = 0; steps < MaxRecords && position + 8 <= records.Length; steps++)
        {
            var versionAndInstance = BinaryPrimitives.ReadUInt16LittleEndian(records[position..]);
            var type = BinaryPrimitives.ReadUInt16LittleEndian(records[(position + 2)..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(records[(position + 4)..]);
            var body = position + 8;
            var end = (int)Math.Min(body + (long)length, records.Length);
            if (type == FbseType)
            {
                if (end - body >= 36)
                {
                    var size = BinaryPrimitives.ReadInt32LittleEndian(records[(body + 20)..]);
                    var delay = BinaryPrimitives.ReadUInt32LittleEndian(records[(body + 28)..]);
                    var nameLength = records[body + 33];
                    var embedded = body + 36 + nameLength;
                    if (embedded + 8 <= end)
                    {
                        return Blip(records[embedded..end]);
                    }

                    if (size > 8 && delay < delayStream.Length)
                    {
                        var stored = delayStream.AsSpan((int)delay, (int)Math.Min(size, delayStream.Length - (long)delay));
                        return Blip(stored);
                    }
                }

                return null;
            }

            if (type is >= 0xF018 and <= 0xF117)
            {
                return Blip(records[position..end]);
            }

            position = (versionAndInstance & 0x0F) == 0x0F ? body : end;
        }

        return null;
    }

    /// <summary>A BLIP record ([MS-ODRAW] 2.2.23): a bitmap's bytes after its one or two UIDs and tag byte.</summary>
    private static DocBlip? Blip(ReadOnlySpan<byte> record)
    {
        if (record.Length < 8)
        {
            return null;
        }

        var instance = BinaryPrimitives.ReadUInt16LittleEndian(record) >> 4;
        var type = BinaryPrimitives.ReadUInt16LittleEndian(record[2..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
        record = record[8..(int)Math.Min(8 + (long)length, record.Length)];
        var kind = type switch
        {
            0xF01A => "EMF",
            0xF01B => "WMF",
            0xF01C => "PICT",
            0xF01D or 0xF02A => "JPEG",
            0xF01E => "PNG",
            0xF01F => "DIB",
            0xF029 => "TIFF",
            _ => "an unknown format",
        };
        if (kind is "EMF" or "WMF" or "PICT" or "an unknown format")
        {
            return new DocBlip(null, kind);
        }

        // The UID (two when the instance is odd), then a tag byte.
        var skip = 16 + ((instance & 1) == 1 ? 16 : 0) + 1;
        if (record.Length <= skip)
        {
            return null;
        }

        var data = record[skip..];
        return new DocBlip(kind == "DIB" ? Rtf.RtfReader.Bitmap(data) : data.ToArray(), kind);
    }

    /// <summary>
    /// The drawing layer: the BLIP store's entries (where each FBSE is), and of each shape its id, picture (<c>pib</c>)
    /// and text box (<c>lTxid</c>).
    /// </summary>
    private void ReadContent(ReadOnlySpan<byte> content)
    {
        var position = 0;
        var spid = 0;
        var tableOffset = _file.FcLcb(50).Fc;
        var topLevelEnd = -1;
        for (var steps = 0; steps < MaxRecords && position + 8 <= content.Length; steps++)
        {
            if (position >= topLevelEnd)
            {
                // A record at the top: after the first (the drawing group), each drawing follows its dgglbl byte.
                if (topLevelEnd >= 0)
                {
                    position++;
                    if (position + 8 > content.Length)
                    {
                        break;
                    }
                }

                topLevelEnd = (int)Math.Min(position + 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(content[(position + 4)..]), content.Length);
            }

            var versionAndInstance = BinaryPrimitives.ReadUInt16LittleEndian(content[position..]);
            var type = BinaryPrimitives.ReadUInt16LittleEndian(content[(position + 2)..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(content[(position + 4)..]);
            var body = position + 8;
            var end = (int)Math.Min(body + (long)length, content.Length);
            if ((versionAndInstance & 0x0F) == 0x0F)
            {
                position = body; // A container: its records follow.
                continue;
            }

            switch (type)
            {
                case FbseType:
                    _store.Add(((int)tableOffset + position, end - position));
                    break;
                case 0xF00A when end - body >= 4:
                    spid = BinaryPrimitives.ReadInt32LittleEndian(content[body..]);
                    break;
                case 0xF00B:
                    ReadProperties(content[body..end], versionAndInstance >> 4, spid);
                    break;
            }

            position = end;
        }
    }

    /// <summary>A shape's properties ([MS-ODRAW] 2.2.9 OfficeArtFOPT): <c>pib</c> (0x0104) and <c>lTxid</c> (0x0080).</summary>
    private void ReadProperties(ReadOnlySpan<byte> properties, int count, int spid)
    {
        int blip = 0, textBox = 0;
        for (var i = 0; i < count && (6 * i) + 6 <= properties.Length; i++)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(properties[(6 * i)..]) & 0x3FFF;
            var value = BinaryPrimitives.ReadInt32LittleEndian(properties[((6 * i) + 2)..]);
            if (id == 0x0104)
            {
                blip = value;
            }
            else if (id == 0x0080)
            {
                textBox = (value >> 16) & 0xFFFF;
            }
        }

        if (blip != 0 || textBox != 0)
        {
            _shapes[spid] = (blip, textBox);
        }
    }
}

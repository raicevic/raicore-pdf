using System.Buffers.Binary;
using System.Text;

namespace RaiCore.Pdf.Tests.Slides;

/// <summary>
/// Builds small PowerPoint 97-2003 files for tests: records and containers ([MS-PPT], [MS-ODRAW]), a presentation
/// of slides with OfficeArt shapes, and the compound file around its streams (version 3, every stream in regular
/// sectors).
/// </summary>
internal static class PptBuilder
{
    /// <summary>A record: its header (version, instance, type, length) and body.</summary>
    public static byte[] Atom(int type, byte[] body, int instance = 0, int version = 0, int? length = null)
    {
        var record = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)((instance << 4) | version));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), (ushort)type);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)(length ?? body.Length));
        body.CopyTo(record, 8);
        return record;
    }

    /// <summary>A container of records.</summary>
    public static byte[] Container(int type, params byte[][] children) => Container(type, 0, children);

    /// <summary>A container of records, with an instance.</summary>
    public static byte[] Container(int type, int instance, params byte[][] children) => Atom(type, [.. children.SelectMany(c => c)], instance, 0xF);

    /// <summary>Little-endian 32-bit values.</summary>
    public static byte[] Ints(params int[] values)
    {
        var bytes = new byte[4 * values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4 * i), values[i]);
        }

        return bytes;
    }

    /// <summary>Little-endian 16-bit values.</summary>
    public static byte[] Shorts(params int[] values)
    {
        var bytes = new byte[2 * values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2 * i), (short)values[i]);
        }

        return bytes;
    }

    /// <summary>An OfficeArt property table (<c>OfficeArtFOPT</c>): simple properties, then complex ones with their data.</summary>
    public static byte[] Properties(params (int Id, int Value, byte[]? Complex)[] properties)
    {
        var table = new List<byte>();
        var complex = new List<byte>();
        foreach (var (id, value, data) in properties)
        {
            table.AddRange(Shorts(data is null ? id : id | 0x8000));
            table.AddRange(Ints(data is null ? value : data.Length));
            complex.AddRange(data ?? []);
        }

        return Atom(0xF00B, [.. table, .. complex], properties.Length, 3);
    }

    /// <summary>
    /// A shape: its type and flags (<c>OfficeArtFSP</c>), properties, anchor (top, left, right, bottom in master
    /// units; a child anchor inside a group) and more records (client data, text box).
    /// </summary>
    public static byte[] Shape(int type, int id, (int Top, int Left, int Right, int Bottom) anchor, byte[]? properties = null, bool child = false, int flags = 0xA00, params byte[][] more)
    {
        var anchorRecord = child
            ? Atom(0xF00F, Ints(anchor.Left, anchor.Top, anchor.Right, anchor.Bottom))
            : Atom(0xF010, Shorts(anchor.Top, anchor.Left, anchor.Right, anchor.Bottom));
        return Container(0xF004, [Atom(0xF00A, Ints(id, flags), type, 2), properties ?? [], anchorRecord, .. more]);
    }

    /// <summary>A group: its own shape (bounds of the children's coordinates, anchor) and members.</summary>
    public static byte[] Group(int id, (int Left, int Top, int Right, int Bottom) bounds, (int Top, int Left, int Right, int Bottom) anchor, bool child, params byte[][] members)
    {
        var anchorRecord = child
            ? Atom(0xF00F, Ints(anchor.Left, anchor.Top, anchor.Right, anchor.Bottom))
            : Atom(0xF010, Shorts(anchor.Top, anchor.Left, anchor.Right, anchor.Bottom));
        var own = Container(0xF004, Atom(0xF009, Ints(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), 0, 1), Atom(0xF00A, Ints(id, 0x201), 0, 2), anchorRecord);
        return Container(0xF003, [own, .. members]);
    }

    /// <summary>A client text box with text of a text type, and optionally a style atom.</summary>
    public static byte[] TextBox(string text, int textType = 4, byte[]? style = null, params byte[][] more) =>
        Container(0xF00D, [Atom(0x0F9F, Ints(textType)), Atom(0x0FA0, Encoding.Unicode.GetBytes(text)), .. style is null ? Array.Empty<byte[]>() : [Atom(0x0FA1, style)], .. more]);

    /// <summary>A slide container with shapes (and more records), following its master or not.</summary>
    public static byte[] Slide(byte[][] shapes, int masterId = 0, int flags = 0, params byte[][] more) => Sheet(0x03EE, shapes, masterId, flags, null, more);

    /// <summary>A slide container with shapes and a background shape, following its master or not.</summary>
    public static byte[] Slide(byte[][] shapes, byte[] background, int masterId, int flags, params byte[][] more) => Sheet(0x03EE, shapes, masterId, flags, background, more);

    /// <summary>A background shape filled with a color (an <c>OfficeArtCOLORREF</c>).</summary>
    public static byte[] Background(int color) => Container(0xF004, Atom(0xF00A, Ints(1030, 0xC00), 1, 2), Properties((0x181, color, null)));

    /// <summary>A main master container with shapes and more records (color scheme, text styles).</summary>
    public static byte[] Master(byte[][] shapes, byte[]? background, params byte[][] more) => Sheet(0x03F8, shapes, 0, 0, background, more);

    /// <summary>A color scheme atom (instance 1, the sheet's own) of eight RGB colors.</summary>
    public static byte[] Scheme(params int[] colors) => Atom(0x07F0, [.. colors.SelectMany(c => new[] { (byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)0 })], 1);

    private static byte[] Sheet(int type, byte[][] shapes, int masterId, int flags, byte[]? background, byte[][] more)
    {
        var patriarch = Container(0xF004, Atom(0xF009, new byte[16], 0, 1), Atom(0xF00A, Ints(1024, 0x5), 0, 2));
        var drawing = Container(0x040C, Container(0xF002, Atom(0xF008, Ints(shapes.Length, 1024), 1), Container(0xF003, [patriarch, .. shapes]), background ?? []));
        var atom = Atom(0x03EF, [.. Ints(0), .. new byte[8], .. Ints(masterId, 0), .. Shorts(flags, 0)], 0, 2);
        return Container(type, [atom, drawing, .. more]);
    }

    /// <summary>
    /// A presentation: a document (slide size 720 x 540 points, the fonts, a slide list with each slide's persist id)
    /// and its slides, a persist directory and a user edit; with its Current User stream, in a compound file.
    /// </summary>
    /// <param name="slides">The slide containers.</param>
    /// <param name="fonts">The font collection, Arial if none.</param>
    /// <param name="documentMore">More records for the document container.</param>
    /// <param name="slideText">For each slide, records that follow its entry in the slide list (outline text).</param>
    /// <param name="selfLoop">Whether the user edit's previous edit is itself (a broken chain).</param>
    /// <param name="token">The current user atom's header token.</param>
    /// <param name="extraStreams">More streams of the compound file.</param>
    /// <param name="masters">The masters, with ids 0x80000000 and up.</param>
    public static byte[] Presentation(byte[][] slides, string[]? fonts = null, byte[][]? documentMore = null, byte[][][]? slideText = null,
        bool selfLoop = false, uint token = 0xE391C05F, byte[][]? masters = null, params (string Name, byte[] Data)[] extraStreams)
    {
        var (stream, offset) = Document(slides, fonts, documentMore, slideText, selfLoop, masters);
        return Compound([("Current User", CurrentUser(offset, token)), ("PowerPoint Document", stream), .. extraStreams]);
    }

    /// <summary>The PowerPoint Document stream of a presentation, and the offset of its user edit.</summary>
    public static (byte[] Stream, int Edit) Document(byte[][] slides, string[]? fonts = null, byte[][]? documentMore = null, byte[][][]? slideText = null, bool selfLoop = false,
        byte[][]? masters = null)
    {
        masters ??= [];
        var fontRecords = (fonts ?? ["Arial"]).Select((name, i) =>
        {
            var body = new byte[68];
            Encoding.Unicode.GetBytes(name).CopyTo(body, 0);
            return Atom(0x0FB7, body, i);
        }).ToArray();
        var list = new List<byte[]>();
        for (var i = 0; i < slides.Length; i++)
        {
            list.Add(Atom(0x03F3, Ints(2 + i, 0, 0, 256 + i, 0)));
            list.AddRange(slideText is not null && i < slideText.Length ? slideText[i] : []);
        }

        var document = Container(0x03E8, [
            Atom(0x03E9, [.. Ints(5760, 4320, 4320, 5760), .. new byte[24]], 0, 1),
            Container(0x03F2, Container(0x07D5, fontRecords)),
            Container(0x0FF0, 1, [.. masters.Select((_, i) => Atom(0x03F3, Ints(2 + slides.Length + i, 0, 0, unchecked((int)0x80000000) + i, 0)))]),
            Container(0x0FF0, 0, [.. list]),
            .. documentMore ?? []]);
        var stream = new List<byte>(document);
        var offsets = new List<int> { 0 };
        foreach (var sheet in slides.Concat(masters))
        {
            offsets.Add(stream.Count);
            stream.AddRange(sheet);
        }

        var directoryOffset = stream.Count;
        stream.AddRange(Atom(0x1772, Ints([1 | (offsets.Count << 20), .. offsets])));
        var edit = stream.Count;
        stream.AddRange(Atom(0x0FF5, [.. Ints(256), .. Shorts(0), 0, 3, .. Ints(selfLoop ? edit : 0, directoryOffset, 1, offsets.Count + 1), .. Shorts(1, 0)]));
        return ([.. stream], edit);
    }

    /// <summary>A Current User stream pointing at a user edit.</summary>
    public static byte[] CurrentUser(int edit, uint token = 0xE391C05F) =>
        Atom(0x0FF6, [.. Ints(20), .. BitConverter.GetBytes(token), .. Ints(edit), .. Shorts(0, 0x3F4), 3, 0, .. Shorts(0), .. Ints(8)]);

    /// <summary>
    /// A version 3 compound file of streams in the root storage (a name with a slash puts a stream in a storage).
    /// </summary>
    public static byte[] Compound(params (string Name, byte[] Data)[] streams)
    {
        const int Sector = 512;
        const uint End = 0xFFFFFFFE;
        const uint None = 0xFFFFFFFF;

        // Directory entries: the root, storages, then streams; children chained as right siblings.
        var entries = new List<(string Name, byte Type, byte[]? Data, int Parent)> { ("Root Entry", 5, null, -1) };
        foreach (var (name, data) in streams)
        {
            var parent = 0;
            var parts = name.Split('/');
            foreach (var storage in parts[..^1])
            {
                var index = entries.FindIndex(e => e.Name == storage && e.Type == 1 && e.Parent == parent);
                if (index < 0)
                {
                    entries.Add((storage, 1, null, parent));
                    index = entries.Count - 1;
                }

                parent = index;
            }

            entries.Add((parts[^1], 2, data, parent));
        }

        var directorySectors = (entries.Count + 3) / 4;
        var streamSectors = entries.Select(e => e.Data is null ? 0 : Math.Max(1, (e.Data.Length + Sector - 1) / Sector)).ToArray();
        var dataSectors = directorySectors + streamSectors.Sum();
        var fatSectors = 1;
        while (fatSectors * 128 < fatSectors + dataSectors)
        {
            fatSectors++;
        }

        var fat = Enumerable.Repeat(None, fatSectors * 128).ToArray();
        for (var i = 0; i < fatSectors; i++)
        {
            fat[i] = 0xFFFFFFFD;
        }

        var next = fatSectors;
        uint Chain(int count)
        {
            var first = (uint)next;
            for (var i = 0; i < count; i++, next++)
            {
                fat[next] = i == count - 1 ? End : (uint)(next + 1);
            }

            return first;
        }

        var directoryStart = Chain(directorySectors);
        var starts = streamSectors.Select(count => count == 0 ? End : Chain(count)).ToArray();
        var file = new byte[Sector * (1 + next)];
        var header = file.AsSpan();
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[24..], 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(header[26..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header[30..], 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], directoryStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], 0); // No mini stream: every stream in regular sectors.
        BinaryPrimitives.WriteUInt32LittleEndian(header[60..], End);
        BinaryPrimitives.WriteUInt32LittleEndian(header[68..], End);
        for (var i = 0; i < 109; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header[(76 + (4 * i))..], i < fatSectors ? (uint)i : None);
        }

        for (var i = 0; i < fat.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(Sector + (4 * i)), fat[i]);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = file.AsSpan(Sector * (1 + (int)directoryStart) + (128 * i), 128);
            var (name, type, data, parent) = entries[i];
            Encoding.Unicode.GetBytes(name).CopyTo(entry);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[64..], (ushort)((name.Length + 1) * 2));
            entry[66] = type;
            entry[67] = 1;
            var siblings = Enumerable.Range(0, entries.Count).Where(j => entries[j].Parent == parent).ToList();
            var children = Enumerable.Range(0, entries.Count).Where(j => entries[j].Parent == i).ToList();
            var position = siblings.IndexOf(i);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], None);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[72..], parent >= 0 && position + 1 < siblings.Count ? (uint)siblings[position + 1] : None);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], children.Count > 0 ? (uint)children[0] : None);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[116..], data is null ? End : starts[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[120..], (ulong)(data?.Length ?? 0));
            data?.CopyTo(file, Sector * (1 + (int)starts[i]));
        }

        return file;
    }
}

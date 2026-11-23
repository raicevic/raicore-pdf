using System.Buffers.Binary;
using System.Text;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>A sheet of a test workbook: its name, records, visibility (0 visible, 1 hidden) and type (0 worksheet, 2 chart).</summary>
internal sealed record BiffSheet(string Name, Action<BiffBuilder> Records, int Hidden = 0, int Type = 0);

/// <summary>Writes BIFF records ([MS-XLS]) and whole workbook streams for the tests: BIFF8 unless told otherwise.</summary>
internal sealed class BiffBuilder
{
    private readonly List<byte> _bytes = [];

    public int Length => _bytes.Count;

    /// <summary>A record: its type, its length and its data.</summary>
    public BiffBuilder Record(int id, params byte[][] parts)
    {
        var data = Join(parts);
        _bytes.AddRange(U16(id));
        _bytes.AddRange(U16(data.Length));
        _bytes.AddRange(data);
        return this;
    }

    /// <summary>Bytes as they are (a record cut short, garbage).</summary>
    public BiffBuilder Raw(params byte[] data)
    {
        _bytes.AddRange(data);
        return this;
    }

    public byte[] ToArray() => [.. _bytes];

    public static byte[] U8(int value) => [(byte)value];

    public static byte[] U16(int value) => [(byte)value, (byte)(value >> 8)];

    public static byte[] U32(long value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value);
        return bytes;
    }

    public static byte[] F64(double value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] Join(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>The characters of a string: one byte each when they all fit, else UTF-16.</summary>
    public static (byte Flags, byte[] Chars) Chars(string text) => text.All(c => c < 256)
        ? ((byte)0, [.. text.Select(c => (byte)c)])
        : ((byte)1, Encoding.Unicode.GetBytes(text));

    /// <summary>A BIFF8 string with its length (<paramref name="lengthSize"/> bytes) and flags.</summary>
    public static byte[] Text(string text, int lengthSize = 2)
    {
        var (flags, chars) = Chars(text);
        return Join(lengthSize == 1 ? U8(text.Length) : U16(text.Length), [flags], chars);
    }

    /// <summary>A BIFF5 string: its length and its bytes in a code page.</summary>
    public static byte[] Text5(string text, Encoding encoding, int lengthSize = 2)
    {
        var bytes = encoding.GetBytes(text);
        return Join(lengthSize == 1 ? U8(bytes.Length) : U16(bytes.Length), bytes);
    }

    /// <summary>The row, column and XF index that start a cell record.</summary>
    public static byte[] Cell(int row, int column, int xf = 0) => Join(U16(row), U16(column), U16(xf));

    /// <summary>A BOF record of a substream type (5 globals, 0x10 worksheet, 0x20 chart).</summary>
    public BiffBuilder Bof(int type, int version = 0x0600) =>
        version == 0x0600 ? Record(0x0809, U16(version), U16(type), U16(0x0DBB), U16(0x07CC), U32(0), U32(0x0006)) : Record(0x0809, U16(version), U16(type), U16(0), U16(0));

    public BiffBuilder Eof() => Record(0x000A);

    /// <summary>A FONT record of BIFF8.</summary>
    public BiffBuilder Font(string name, double size = 10, bool bold = false, bool italic = false, int underline = 0, bool strike = false, int color = 0x7FFF) =>
        Record(0x0031, U16((int)(size * 20)), U16((italic ? 0x02 : 0) | (strike ? 0x08 : 0)), U16(color), U16(bold ? 700 : 400), U16(0), U8(underline), U8(0), U8(0), U8(0), Text(name, 1));

    /// <summary>A FORMAT record of BIFF8.</summary>
    public BiffBuilder Format(int id, string code) => Record(0x041E, U16(id), Text(code));

    /// <summary>
    /// An XF record of BIFF8: font, number format, alignment byte (horizontal, wrap, vertical), indent, the border
    /// words and the fill word ([MS-XLS] 2.4.353).
    /// </summary>
    public BiffBuilder Xf(int font = 0, int format = 0, int alignment = 0x20, int indent = 0, long borders = 0, long more = 0, int fill = 64 | (65 << 7)) =>
        Record(0x00E0, U16(font), U16(format), U16(0x0001), U8(alignment), U8(0), U8(indent), U8(0), U32(borders), U32(more), U16(fill));

    /// <summary>
    /// A workbook stream: the globals (BOF, the given records, a BOUNDSHEET for each sheet, EOF), then each sheet's
    /// substream (BOF, its records, EOF).
    /// </summary>
    public static byte[] Workbook(Action<BiffBuilder>? globals, params BiffSheet[] sheets)
    {
        var bodies = sheets.Select(s =>
        {
            var body = new BiffBuilder().Bof(s.Type == 2 ? 0x20 : 0x10);
            s.Records(body);
            return body.Eof().ToArray();
        }).ToList();

        byte[] Globals(IReadOnlyList<long> offsets)
        {
            var builder = new BiffBuilder().Bof(0x0005);
            globals?.Invoke(builder);
            for (var i = 0; i < sheets.Length; i++)
            {
                builder.Record(0x0085, U32(offsets[i]), U8(sheets[i].Hidden), U8(sheets[i].Type), Text(sheets[i].Name, 1));
            }

            return builder.Eof().ToArray();
        }

        var length = Globals([.. sheets.Select(_ => 0L)]).Length;
        var offsets = new List<long>();
        long offset = length;
        foreach (var body in bodies)
        {
            offsets.Add(offset);
            offset += body.Length;
        }

        return Join([Globals(offsets), .. bodies]);
    }

    /// <summary>A workbook of one worksheet named Sheet1.</summary>
    public static byte[] SingleSheet(Action<BiffBuilder> sheet, Action<BiffBuilder>? globals = null) => Workbook(globals, new BiffSheet("Sheet1", sheet));
}

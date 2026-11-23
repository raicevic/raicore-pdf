using System.Buffers.Binary;
using System.Text;

namespace RaiCore.Pdf.Metafiles;

/// <summary>Reads little-endian values from one record; reading past its end is a damaged record.</summary>
internal sealed class MetaReader(byte[] data, int start, int end)
{
    private readonly byte[] _data = data;
    private readonly int _start = start;
    private readonly int _end = Math.Min(end, data.Length);

    /// <summary>The offset from the record's start.</summary>
    public int Position { get; set; }

    /// <summary>The record's length.</summary>
    public int Length => _end - _start;

    /// <summary>The bytes left.</summary>
    public int Remaining => Math.Max(0, Length - Position);

    private int Take(int count)
    {
        if (count < 0 || Position < 0 || Position > Length - count)
        {
            throw new MetafileFormatException("a record is shorter than its contents");
        }

        var at = _start + Position;
        Position += count;
        return at;
    }

    public byte U8() => _data[Take(1)];

    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(Take(2)));

    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Take(2)));

    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Take(4)));

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Take(4)));

    public float F32()
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(Take(4)));
        return float.IsFinite(value) ? value : 0;
    }

    public void Skip(int count) => Take(count);

    /// <summary>Bytes at an offset from the record's start (bounds checked), without moving.</summary>
    public ReadOnlySpan<byte> At(long offset, long count)
    {
        if (offset < 0 || count < 0 || offset > Length || count > Length - offset)
        {
            throw new MetafileFormatException("a record points outside itself");
        }

        return _data.AsSpan(_start + (int)offset, (int)count);
    }

    /// <summary>The next bytes.</summary>
    public ReadOnlySpan<byte> Bytes(int count) => _data.AsSpan(Take(count), count);

    /// <summary>UTF-16 text of <paramref name="count"/> units at an offset, cut at the first NUL when asked.</summary>
    public string Utf16At(long offset, long count, bool stopAtNul = false)
    {
        var text = Encoding.Unicode.GetString(At(offset, count * 2));
        var nul = stopAtNul ? text.IndexOf('\0', StringComparison.Ordinal) : -1;
        return nul >= 0 ? text[..nul] : text;
    }

    /// <summary>A COLORREF (red, green, blue, and a flag byte).</summary>
    public (byte R, byte G, byte B, byte Flags) ColorRef()
    {
        var at = Take(4);
        return (_data[at], _data[at + 1], _data[at + 2], _data[at + 3]);
    }
}

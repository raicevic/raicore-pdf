using System.Buffers.Binary;

namespace RaiCore.Pdf.Fonts.OpenType.Cff;

/// <summary>
/// A CFF INDEX: a counted array of variable-length byte strings (names, DICTs, charstrings, subroutines).
/// CFF uses a 16-bit count, CFF2 a 32-bit count.
/// </summary>
internal sealed class CffIndex
{
    private readonly byte[] _data;
    private readonly int[] _offsets;

    private CffIndex(byte[] data, int[] offsets)
    {
        _data = data;
        _offsets = offsets;
    }

    public static CffIndex Empty { get; } = new([], [0]);

    public int Count => _offsets.Length - 1;

    public ReadOnlySpan<byte> this[int index] => _data.AsSpan(_offsets[index], _offsets[index + 1] - _offsets[index]);

    /// <summary>Reads an INDEX at <paramref name="position"/> and moves it past the INDEX.</summary>
    public static CffIndex Read(byte[] data, ref int position, bool cff2)
    {
        var headerSize = cff2 ? 4 : 2;
        if (position < 0 || position > data.Length - headerSize)
        {
            throw new FontFormatException("A CFF INDEX lies outside the table.");
        }

        var count = cff2 ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position));
        position += headerSize;
        if (count == 0)
        {
            return Empty;
        }

        if (position >= data.Length)
        {
            throw new FontFormatException("A CFF INDEX is truncated.");
        }

        int offSize = data[position++];
        if (offSize is < 1 or > 4 || count > (uint)(data.Length - position) / (uint)offSize)
        {
            throw new FontFormatException("A CFF INDEX has an invalid header.");
        }

        var offsets = new int[count + 1];
        var dataStart = position + ((int)(count + 1) * offSize) - 1; // Offsets are 1-based.
        for (var i = 0; i <= count; i++)
        {
            uint offset = 0;
            for (var b = 0; b < offSize; b++)
            {
                offset = (offset << 8) | data[position++];
            }

            var absolute = (long)dataStart + offset;
            if (offset < 1 || absolute > data.Length || (i > 0 && absolute < offsets[i - 1]))
            {
                throw new FontFormatException("A CFF INDEX has an invalid offset.");
            }

            offsets[i] = (int)absolute;
        }

        position = offsets[count];
        return new CffIndex(data, offsets);
    }

    /// <summary>Writes a CFF (not CFF2) INDEX with the smallest offset size that fits.</summary>
    public static void Write(Stream output, IReadOnlyList<byte[]> items)
    {
        output.WriteByte((byte)(items.Count >> 8));
        output.WriteByte((byte)items.Count);
        if (items.Count == 0)
        {
            return;
        }

        var end = 1 + items.Sum(i => i.Length);
        var offSize = end <= 0xFF ? 1 : end <= 0xFFFF ? 2 : end <= 0xFFFFFF ? 3 : 4;
        output.WriteByte((byte)offSize);
        var offset = 1;
        for (var i = 0; i <= items.Count; i++)
        {
            for (var b = offSize - 1; b >= 0; b--)
            {
                output.WriteByte((byte)(offset >> (8 * b)));
            }

            if (i < items.Count)
            {
                offset += items[i].Length;
            }
        }

        foreach (var item in items)
        {
            output.Write(item);
        }
    }

    /// <summary>Size in bytes of a CFF INDEX holding <paramref name="items"/>.</summary>
    public static int SizeOf(IReadOnlyList<byte[]> items)
    {
        if (items.Count == 0)
        {
            return 2;
        }

        var end = 1 + items.Sum(i => i.Length);
        var offSize = end <= 0xFF ? 1 : end <= 0xFFFF ? 2 : end <= 0xFFFFFF ? 3 : 4;
        return 3 + ((items.Count + 1) * offSize) + end - 1;
    }

    /// <summary>The subroutine number bias for an INDEX of <paramref name="count"/> subroutines.</summary>
    public static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;
}

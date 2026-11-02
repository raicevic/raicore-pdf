using System.Buffers.Binary;

namespace RaiCore.Pdf.Fonts.OpenType;

/// <summary>
/// Assembles a standalone SFNT (TrueType/OpenType) file from tables: writes the table directory,
/// 4-byte aligns tables, computes checksums and the <c>head.checkSumAdjustment</c>.
/// </summary>
internal static class SfntBuilder
{
    public static byte[] Build(uint sfntVersion, IReadOnlyDictionary<string, byte[]> tables)
    {
        var tags = tables.Keys.OrderBy(t => t, StringComparer.Ordinal).ToArray();
        var numTables = tags.Length;
        var headerSize = 12 + (16 * numTables);
        var totalSize = headerSize + tags.Sum(t => Align4(tables[t].Length));
        var output = new byte[totalSize];
        var span = output.AsSpan();

        // Offset table.
        var entrySelector = numTables == 0 ? 0 : (int)Math.Floor(Math.Log2(numTables));
        var searchRange = (1 << entrySelector) * 16;
        BinaryPrimitives.WriteUInt32BigEndian(span, sfntVersion);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)numTables);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)((numTables * 16) - searchRange));

        var offset = headerSize;
        var headOffset = -1;
        for (var i = 0; i < numTables; i++)
        {
            var tag = tags[i];
            var data = tables[tag];
            if (tag == "head")
            {
                headOffset = offset;
            }

            data.CopyTo(span[offset..]);
            if (tag == "head" && data.Length >= 12)
            {
                BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 8)..], 0); // Cleared before checksumming.
            }

            var record = span[(12 + (16 * i))..];
            for (var c = 0; c < 4; c++)
            {
                record[c] = (byte)tag[c];
            }

            BinaryPrimitives.WriteUInt32BigEndian(record[4..], Checksum(span.Slice(offset, Align4(data.Length))));
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)data.Length);
            offset += Align4(data.Length);
        }

        if (headOffset >= 0)
        {
            var adjustment = unchecked(0xB1B0AFBA - Checksum(span));
            BinaryPrimitives.WriteUInt32BigEndian(span[(headOffset + 8)..], adjustment);
        }

        return output;
    }

    public static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
        }

        if (i < data.Length)
        {
            Span<byte> tail = stackalloc byte[4];
            data[i..].CopyTo(tail);
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(tail));
        }

        return sum;
    }

    public static int Align4(int length) => (length + 3) & ~3;
}

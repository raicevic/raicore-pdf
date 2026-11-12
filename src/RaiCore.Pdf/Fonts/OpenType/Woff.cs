using System.IO.Compression;

namespace RaiCore.Pdf.Fonts.OpenType;

/// <summary>Decodes WOFF 1.0 (zlib-compressed tables) back to a plain SFNT font.</summary>
internal static class Woff
{
    public static byte[] Decode(byte[] data)
    {
        var r = new BigEndianReader(data);
        r.Skip(4); // signature
        var flavor = r.ReadUInt32();
        r.Skip(4); // length
        var numTables = r.ReadUInt16();
        r.Position = 44;

        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var total = 0L;
        for (var i = 0; i < numTables; i++)
        {
            var tag = r.ReadTag();
            var offset = (int)r.ReadUInt32();
            var compLength = (int)r.ReadUInt32();
            var origLength = (int)r.ReadUInt32();
            r.Skip(4); // origChecksum

            if (offset < 0 || compLength < 0 || origLength < 0 || offset > data.Length - compLength)
            {
                throw new FontFormatException($"WOFF table '{tag}' lies outside the file.");
            }

            total += origLength;
            ResourceLimits.CheckFontBytes(total); // Before inflating: a tiny file can declare gigabytes.
            var compressed = data.AsSpan(offset, compLength);
            tables[tag] = compLength == origLength ? compressed.ToArray() : Inflate(compressed, origLength, tag);
        }

        return SfntBuilder.Build(flavor, tables);
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed, int expectedLength, string tag)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var output = new byte[expectedLength];
        zlib.ReadExactly(output);
        if (zlib.ReadByte() != -1)
        {
            throw new FontFormatException($"WOFF table '{tag}' decompresses to more than its declared length.");
        }

        return output;
    }
}

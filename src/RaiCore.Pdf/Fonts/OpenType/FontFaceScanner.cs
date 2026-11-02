using System.Buffers.Binary;

namespace RaiCore.Pdf.Fonts.OpenType;

/// <summary>Family and style of one face, read without loading the whole font.</summary>
internal sealed record ScannedFace(int FaceIndex, FontNames Names, int Weight, bool IsItalic);

/// <summary>
/// Reads just the table directory and the <c>name</c>, <c>OS/2</c> and <c>head</c> tables of a font file,
/// so large system font folders can be indexed quickly.
/// </summary>
internal static class FontFaceScanner
{
    private const uint TagTtcf = 0x74746366;

    public static IReadOnlyList<ScannedFace> Scan(Stream stream)
    {
        var header = ReadAt(stream, 0, 12);
        var signature = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (signature is 0x774F4646 or 0x774F4632) // WOFF / WOFF2: decode fully (rare outside web use).
        {
            using var buffer = new MemoryStream();
            stream.Position = 0;
            stream.CopyTo(buffer);
            var font = OpenTypeFont.Load(buffer.ToArray());
            return [FromFont(font)];
        }

        var faceOffsets = new List<long> { 0 };
        if (signature == TagTtcf)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
            if (count is 0 or > 10_000)
            {
                throw new FontFormatException("Invalid font collection header.");
            }

            var offsets = ReadAt(stream, 12, checked((int)count * 4));
            faceOffsets.Clear();
            for (var i = 0; i < count; i++)
            {
                faceOffsets.Add(BinaryPrimitives.ReadUInt32BigEndian(offsets.AsSpan(i * 4)));
            }
        }

        var faces = new List<ScannedFace>(faceOffsets.Count);
        for (var i = 0; i < faceOffsets.Count; i++)
        {
            faces.Add(ScanFace(stream, faceOffsets[i], i));
        }

        return faces;
    }

    private static ScannedFace ScanFace(Stream stream, long offset, int faceIndex)
    {
        var offsetTable = ReadAt(stream, offset, 12);
        var version = BinaryPrimitives.ReadUInt32BigEndian(offsetTable);
        if (version is not (0x00010000 or 0x4F54544F or 0x74727565))
        {
            throw new FontFormatException($"Not an OpenType font (signature 0x{version:X8}).");
        }

        int numTables = BinaryPrimitives.ReadUInt16BigEndian(offsetTable.AsSpan(4));
        var directory = ReadAt(stream, offset + 12, numTables * 16);
        byte[]? name = null, os2 = null, head = null;
        for (var t = 0; t < numTables; t++)
        {
            var record = directory.AsSpan(t * 16);
            var tag = System.Text.Encoding.ASCII.GetString(record[..4]);
            var tableOffset = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(record[12..]), 1 << 20);
            switch (tag)
            {
                case "name":
                    name = ReadAt(stream, tableOffset, length);
                    break;
                case "OS/2":
                    os2 = ReadAt(stream, tableOffset, Math.Min(length, 64));
                    break;
                case "head":
                    head = ReadAt(stream, tableOffset, Math.Min(length, 54));
                    break;
            }
        }

        var names = name is null ? new FontNames("", "", "", "", "") : NameTable.Read(name);
        var weight = 400;
        var italic = false;
        if (os2 is { Length: >= 64 })
        {
            weight = Math.Clamp((int)BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(4)), 1, 1000);
            italic = (BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(62)) & 1) != 0;
        }

        if (head is { Length: >= 46 })
        {
            var macStyle = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(44));
            italic |= (macStyle & 2) != 0;
            if (os2 is null && (macStyle & 1) != 0)
            {
                weight = 700;
            }
        }

        italic |= names.Subfamily.Contains("Italic", StringComparison.OrdinalIgnoreCase)
            || names.Subfamily.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        return new ScannedFace(faceIndex, names, weight, italic);
    }

    private static ScannedFace FromFont(OpenTypeFont font) =>
        new(0, new FontNames(font.FamilyName, font.FamilyName, font.SubfamilyName, font.FullName, font.PostScriptName), font.WeightClass, font.IsItalic);

    private static byte[] ReadAt(Stream stream, long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > stream.Length)
        {
            throw new FontFormatException("Font table lies outside the file.");
        }

        var buffer = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }
}

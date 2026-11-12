using System.Buffers.Binary;
using System.IO.Compression;

namespace RaiCore.Pdf.Fonts.OpenType;

/// <summary>
/// Decodes WOFF 2.0 (W3C Recommendation) back to a plain SFNT font: Brotli decompression plus
/// reconstruction of the transformed <c>glyf</c>, <c>loca</c> and <c>hmtx</c> tables.
/// </summary>
internal static class Woff2
{
    private const uint TagTtcf = 0x74746366;

    private static readonly string[] KnownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep",
        "CFF ", "VORG", "EBDT", "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE",
        "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH", "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt",
        "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty", "just", "lcar",
        "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    ];

    public static byte[] Decode(byte[] data)
    {
        try
        {
            return DecodeChecked(data);
        }
        catch (OverflowException ex)
        {
            // Sizes and coordinates are computed with checked arithmetic: values out of range are malformed data.
            throw new FontFormatException("WOFF2 data has values out of range.", ex);
        }
    }

    private static byte[] DecodeChecked(byte[] data)
    {
        var r = new BigEndianReader(data);
        r.Skip(4); // signature
        var flavor = r.ReadUInt32();
        r.Skip(4); // length
        var numTables = r.ReadUInt16();
        r.Skip(6); // reserved, totalSfntSize
        var totalCompressedSize = (int)r.ReadUInt32();
        r.Position = 48;

        if (flavor == TagTtcf)
        {
            throw new FontFormatException("WOFF2 font collections are not supported.");
        }

        var entries = new List<(string Tag, bool Transformed, int OrigLength, int StreamLength)>(numTables);
        for (var i = 0; i < numTables; i++)
        {
            var flags = r.ReadByte();
            var tagIndex = flags & 0x3F;
            var tag = tagIndex == 63 ? r.ReadTag() : KnownTags[tagIndex];
            var version = (flags >> 6) & 3;
            var origLength = checked((int)r.ReadUIntBase128());

            // glyf/loca: version 0 means transformed, 3 means not. Other tables: 0 means not transformed.
            var transformed = tag is "glyf" or "loca" ? version == 0 : version != 0;
            var streamLength = transformed ? checked((int)r.ReadUIntBase128()) : origLength;
            entries.Add((tag, transformed, origLength, streamLength));
        }

        if (totalCompressedSize < 0 || r.Position > data.Length - totalCompressedSize)
        {
            throw new FontFormatException("WOFF2 compressed data lies outside the file.");
        }

        var stream = Decompress(data.AsSpan(r.Position, totalCompressedSize), entries.Sum(e => (long)e.StreamLength));

        var raw = new Dictionary<string, (byte[] Data, bool Transformed)>(StringComparer.Ordinal);
        var position = 0;
        foreach (var (tag, transformed, _, length) in entries)
        {
            raw[tag] = (stream.AsSpan(position, length).ToArray(), transformed);
            position += length;
        }

        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        short[]? xMins = null;
        foreach (var (tag, (bytes, transformed)) in raw)
        {
            if (tag == "glyf" && transformed)
            {
                var (glyf, loca, mins) = ReconstructGlyf(bytes);
                tables["glyf"] = glyf;
                tables["loca"] = loca;
                xMins = mins;
            }
            else if (tag == "loca" && transformed)
            {
                // Rebuilt together with glyf.
            }
            else if (!transformed)
            {
                tables[tag] = bytes;
            }
        }

        if (raw.TryGetValue("hmtx", out var hmtx) && hmtx.Transformed)
        {
            tables["hmtx"] = ReconstructHmtx(hmtx.Data, tables, xMins);
        }

        return SfntBuilder.Build(flavor, tables);
    }

    private static byte[] Decompress(ReadOnlySpan<byte> compressed, long expectedLength)
    {
        ResourceLimits.CheckFontBytes(expectedLength); // Before decompressing: a tiny file can declare gigabytes.

        var output = new byte[expectedLength];
        using var input = new MemoryStream(compressed.ToArray());
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        try
        {
            brotli.ReadExactly(output);
        }
        catch (EndOfStreamException ex)
        {
            throw new FontFormatException("WOFF2 data decompresses to less than the declared table sizes.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new FontFormatException("WOFF2 data is not valid Brotli.", ex); // BrotliStream's error for corrupt data.
        }

        return output;
    }

    private static (byte[] Glyf, byte[] Loca, short[] XMins) ReconstructGlyf(byte[] data)
    {
        var header = new BigEndianReader(data);
        header.Skip(2); // reserved
        var optionFlags = header.ReadUInt16();
        var numGlyphs = header.ReadUInt16();
        var indexFormat = header.ReadUInt16();
        Span<int> sizes = stackalloc int[7];
        for (var i = 0; i < 7; i++)
        {
            sizes[i] = checked((int)header.ReadUInt32());
        }

        var offset = header.Position;
        BigEndianReader Next(int size)
        {
            if (size < 0 || offset > data.Length - size)
            {
                throw new FontFormatException("WOFF2 glyf stream lies outside the table.");
            }

            var sub = new BigEndianReader(data.AsSpan(offset, size).ToArray());
            offset += size;
            return sub;
        }

        var nContourStream = Next(sizes[0]);
        var nPointsStream = Next(sizes[1]);
        var flagStream = Next(sizes[2]);
        var glyphStream = Next(sizes[3]);
        var compositeStream = Next(sizes[4]);
        var bboxStream = Next(sizes[5]);
        var instructionStream = Next(sizes[6]);
        _ = optionFlags; // Bit 0 adds an overlapSimpleBitmap after the streams; it only affects rendering hints.

        var bitmapLength = 4 * ((numGlyphs + 31) / 32);
        var bboxBitmap = bboxStream.ReadBytes(bitmapLength).ToArray();
        bool HasExplicitBbox(int glyph) => (bboxBitmap[glyph >> 3] & (0x80 >> (glyph & 7))) != 0;

        using var glyf = new MemoryStream();
        var loca = new int[numGlyphs + 1];
        var xMins = new short[numGlyphs];

        for (var g = 0; g < numGlyphs; g++)
        {
            loca[g] = (int)glyf.Position;
            var nContours = nContourStream.ReadInt16();
            byte[] glyph;
            if (nContours == 0)
            {
                if (HasExplicitBbox(g))
                {
                    throw new FontFormatException("WOFF2 empty glyph has a bounding box.");
                }

                glyph = [];
            }
            else if (nContours < 0)
            {
                if (!HasExplicitBbox(g))
                {
                    throw new FontFormatException("WOFF2 composite glyph lacks a bounding box.");
                }

                glyph = BuildCompositeGlyph(compositeStream, glyphStream, instructionStream, bboxStream, out xMins[g]);
            }
            else
            {
                glyph = BuildSimpleGlyph(
                    nContours, nPointsStream, flagStream, glyphStream, instructionStream,
                    HasExplicitBbox(g) ? bboxStream : null, out xMins[g]);
            }

            glyf.Write(glyph);
            while (glyf.Position % 4 != 0)
            {
                glyf.WriteByte(0);
            }
        }

        loca[numGlyphs] = (int)glyf.Position;
        return (glyf.ToArray(), EncodeLoca(loca, indexFormat), xMins);
    }

    private static byte[] BuildSimpleGlyph(
        int nContours,
        BigEndianReader nPointsStream,
        BigEndianReader flagStream,
        BigEndianReader glyphStream,
        BigEndianReader instructionStream,
        BigEndianReader? bboxStream,
        out short xMin)
    {
        var endPoints = new ushort[nContours];
        var totalPoints = 0;
        for (var c = 0; c < nContours; c++)
        {
            totalPoints += nPointsStream.Read255UInt16();
            endPoints[c] = checked((ushort)(totalPoints - 1));
        }

        var dx = new int[totalPoints];
        var dy = new int[totalPoints];
        var onCurve = new bool[totalPoints];
        int x = 0, y = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (var p = 0; p < totalPoints; p++)
        {
            var flag = flagStream.ReadByte();
            onCurve[p] = (flag & 0x80) == 0;
            (dx[p], dy[p]) = DecodeTriplet(flag & 0x7F, glyphStream);
            x += dx[p];
            y += dy[p];
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        var instructionLength = glyphStream.Read255UInt16();
        var instructions = instructionStream.ReadBytes(instructionLength);

        if (bboxStream is not null)
        {
            minX = bboxStream.ReadInt16();
            minY = bboxStream.ReadInt16();
            maxX = bboxStream.ReadInt16();
            maxY = bboxStream.ReadInt16();
        }
        else if (totalPoints == 0)
        {
            minX = minY = maxX = maxY = 0;
        }

        xMin = (short)minX;

        using var output = new MemoryStream();
        WriteInt16(output, nContours);
        WriteInt16(output, minX);
        WriteInt16(output, minY);
        WriteInt16(output, maxX);
        WriteInt16(output, maxY);
        foreach (var end in endPoints)
        {
            WriteInt16(output, end);
        }

        WriteInt16(output, instructionLength);
        output.Write(instructions);

        // Flags: ON_CURVE 0x01, X_SHORT 0x02, Y_SHORT 0x04, X_SAME_OR_POSITIVE 0x10, Y_SAME_OR_POSITIVE 0x20.
        using var xs = new MemoryStream();
        using var ys = new MemoryStream();
        for (var p = 0; p < totalPoints; p++)
        {
            var flag = onCurve[p] ? 0x01 : 0x00;
            flag |= EncodeCoordinate(dx[p], xs, 0x02, 0x10);
            flag |= EncodeCoordinate(dy[p], ys, 0x04, 0x20);
            output.WriteByte((byte)flag);
        }

        xs.WriteTo(output);
        ys.WriteTo(output);
        return output.ToArray();
    }

    private static int EncodeCoordinate(int delta, MemoryStream stream, int shortFlag, int sameOrPositiveFlag)
    {
        if (delta == 0)
        {
            return sameOrPositiveFlag;
        }

        if (delta is > -256 and < 256)
        {
            stream.WriteByte((byte)Math.Abs(delta));
            return shortFlag | (delta > 0 ? sameOrPositiveFlag : 0);
        }

        WriteInt16(stream, delta);
        return 0;
    }

    private static byte[] BuildCompositeGlyph(
        BigEndianReader compositeStream,
        BigEndianReader glyphStream,
        BigEndianReader instructionStream,
        BigEndianReader bboxStream,
        out short xMin)
    {
        using var output = new MemoryStream();
        WriteInt16(output, -1);
        xMin = bboxStream.ReadInt16();
        WriteInt16(output, xMin);
        output.Write(bboxStream.ReadBytes(6));

        var haveInstructions = false;
        ushort flags;
        do
        {
            var start = compositeStream.Position;
            flags = compositeStream.ReadUInt16();
            compositeStream.Skip(2); // glyph index
            var size = (flags & 0x0001) != 0 ? 4 : 2;
            size += (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
            compositeStream.Skip(size);
            haveInstructions |= (flags & 0x0100) != 0;
            output.Write(compositeStream.Data.AsSpan(start, compositeStream.Position - start));
        }
        while ((flags & 0x0020) != 0);

        if (haveInstructions)
        {
            var length = glyphStream.Read255UInt16();
            WriteInt16(output, length);
            output.Write(instructionStream.ReadBytes(length));
        }

        return output.ToArray();
    }

    /// <summary>Decodes a WOFF2 point triplet (spec section 5.2) into a coordinate delta.</summary>
    private static (int Dx, int Dy) DecodeTriplet(int flag, BigEndianReader r)
    {
        static int WithSign(int flag, int value) => (flag & 1) != 0 ? value : -value;

        if (flag < 10)
        {
            return (0, WithSign(flag, ((flag & 14) << 7) + r.ReadByte()));
        }

        if (flag < 20)
        {
            return (WithSign(flag, (((flag - 10) & 14) << 7) + r.ReadByte()), 0);
        }

        if (flag < 84)
        {
            var b0 = flag - 20;
            var b1 = r.ReadByte();
            return (WithSign(flag, 1 + (b0 & 0x30) + (b1 >> 4)), WithSign(flag >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F)));
        }

        if (flag < 120)
        {
            var b0 = flag - 84;
            var x = r.ReadByte();
            var y = r.ReadByte();
            return (WithSign(flag, 1 + ((b0 / 12) << 8) + x), WithSign(flag >> 1, 1 + (((b0 % 12) >> 2) << 8) + y));
        }

        if (flag < 124)
        {
            var a = r.ReadByte();
            var b = r.ReadByte();
            var c = r.ReadByte();
            return (WithSign(flag, (a << 4) + (b >> 4)), WithSign(flag >> 1, ((b & 0x0F) << 8) + c));
        }

        var x0 = r.ReadByte();
        var x1 = r.ReadByte();
        var y0 = r.ReadByte();
        var y1 = r.ReadByte();
        return (WithSign(flag, (x0 << 8) + x1), WithSign(flag >> 1, (y0 << 8) + y1));
    }

    private static byte[] ReconstructHmtx(byte[] data, Dictionary<string, byte[]> tables, short[]? xMins)
    {
        if (xMins is null || !tables.TryGetValue("hhea", out var hhea) || hhea.Length < 36)
        {
            throw new FontFormatException("WOFF2 transformed hmtx requires a transformed glyf table and hhea.");
        }

        var numGlyphs = xMins.Length;
        var numberOfHMetrics = BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34));
        var r = new BigEndianReader(data);
        var flags = r.ReadByte();

        var advances = new ushort[numberOfHMetrics];
        for (var i = 0; i < numberOfHMetrics; i++)
        {
            advances[i] = r.ReadUInt16();
        }

        var lsbs = new short[numGlyphs];
        for (var i = 0; i < numberOfHMetrics; i++)
        {
            lsbs[i] = (flags & 1) == 0 ? r.ReadInt16() : xMins[i];
        }

        for (var i = numberOfHMetrics; i < numGlyphs; i++)
        {
            lsbs[i] = (flags & 2) == 0 ? r.ReadInt16() : xMins[i];
        }

        using var output = new MemoryStream();
        for (var i = 0; i < numGlyphs; i++)
        {
            if (i < numberOfHMetrics)
            {
                WriteInt16(output, advances[i]);
            }

            WriteInt16(output, lsbs[i]);
        }

        return output.ToArray();
    }

    private static byte[] EncodeLoca(int[] offsets, int indexFormat)
    {
        var output = new byte[offsets.Length * (indexFormat == 0 ? 2 : 4)];
        for (var i = 0; i < offsets.Length; i++)
        {
            if (indexFormat == 0)
            {
                BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(i * 2), checked((ushort)(offsets[i] / 2)));
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(i * 4), (uint)offsets[i]);
            }
        }

        return output;
    }

    private static void WriteInt16(MemoryStream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}

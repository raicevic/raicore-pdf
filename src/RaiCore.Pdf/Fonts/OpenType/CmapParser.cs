namespace RaiCore.Pdf.Fonts.OpenType;

/// <summary>Reads the <c>cmap</c> table: Unicode code point → glyph ID. Supports formats 0, 4, 6, 12 and 13.</summary>
internal static class CmapParser
{
    public static Dictionary<int, ushort> Parse(OpenTypeFont font, out bool isSymbol)
    {
        isSymbol = false;
        var map = new Dictionary<int, ushort>();
        var table = font.GetTable("cmap");
        if (table.Length < 4)
        {
            return map;
        }

        var data = table.ToArray();
        var r = new BigEndianReader(data);
        r.Skip(2);
        var count = r.ReadUInt16();

        // Pick the richest Unicode subtable; fall back to the Windows symbol table.
        var bestScore = 0;
        var bestOffset = -1;
        for (var i = 0; i < count && r.Remaining >= 8; i++)
        {
            var platform = r.ReadUInt16();
            var encoding = r.ReadUInt16();
            var offset = (int)r.ReadUInt32();
            var score = (platform, encoding) switch
            {
                (3, 10) => 6,
                (0, 4) or (0, 6) => 5,
                (3, 1) => 4,
                (0, _) => 3,
                (3, 0) => 2,
                (1, 0) => 1,
                _ => 0,
            };

            if (score > bestScore && offset >= 0 && offset < data.Length - 2)
            {
                bestScore = score;
                bestOffset = offset;
                isSymbol = (platform, encoding) == (3, 0);
            }
        }

        if (bestOffset < 0)
        {
            return map;
        }

        r.Position = bestOffset;
        var format = r.ReadUInt16();
        switch (format)
        {
            case 0:
                ReadFormat0(r, map);
                break;
            case 4:
                ReadFormat4(r, map);
                break;
            case 6:
                ReadFormat6(r, map);
                break;
            case 12:
                ReadFormat12(r, font.NumGlyphs, map, manyToOne: false);
                break;
            case 13:
                ReadFormat12(r, font.NumGlyphs, map, manyToOne: true);
                break;
            default:
                throw new FontFormatException($"Unsupported cmap subtable format {format}.");
        }

        return map;
    }

    private static void ReadFormat0(BigEndianReader r, Dictionary<int, ushort> map)
    {
        r.Skip(4);
        for (var code = 0; code < 256; code++)
        {
            Add(map, code, r.ReadByte());
        }
    }

    private static void ReadFormat4(BigEndianReader r, Dictionary<int, ushort> map)
    {
        r.Skip(4); // length, language
        var segCount = r.ReadUInt16() / 2;
        r.Skip(6);
        var endCodesAt = r.Position;
        var startCodesAt = endCodesAt + (segCount * 2) + 2;
        var deltasAt = startCodesAt + (segCount * 2);
        var rangeOffsetsAt = deltasAt + (segCount * 2);

        for (var s = 0; s < segCount; s++)
        {
            r.Position = endCodesAt + (s * 2);
            int end = r.ReadUInt16();
            r.Position = startCodesAt + (s * 2);
            int start = r.ReadUInt16();
            r.Position = deltasAt + (s * 2);
            var delta = r.ReadInt16();
            var rangeOffsetPosition = rangeOffsetsAt + (s * 2);
            r.Position = rangeOffsetPosition;
            int rangeOffset = r.ReadUInt16();

            if (start > end)
            {
                continue;
            }

            for (var code = start; code <= end && code != 0xFFFF; code++)
            {
                int gid;
                if (rangeOffset == 0)
                {
                    gid = (code + delta) & 0xFFFF;
                }
                else
                {
                    var glyphAt = rangeOffsetPosition + rangeOffset + ((code - start) * 2);
                    if (glyphAt < 0 || glyphAt > r.Data.Length - 2)
                    {
                        continue;
                    }

                    r.Position = glyphAt;
                    gid = r.ReadUInt16();
                    if (gid != 0)
                    {
                        gid = (gid + delta) & 0xFFFF;
                    }
                }

                Add(map, code, gid);
            }
        }
    }

    private static void ReadFormat6(BigEndianReader r, Dictionary<int, ushort> map)
    {
        r.Skip(4);
        int first = r.ReadUInt16();
        int count = r.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            Add(map, first + i, r.ReadUInt16());
        }
    }

    /// <summary>Format 12 (segmented coverage) and 13 (many-to-one: every code in a group maps to one glyph).</summary>
    private static void ReadFormat12(BigEndianReader r, int numGlyphs, Dictionary<int, ushort> map, bool manyToOne)
    {
        r.Skip(10); // reserved, length, language
        var groups = r.ReadUInt32();
        for (var g = 0; g < groups && r.Remaining >= 12; g++)
        {
            var start = r.ReadUInt32();
            var end = Math.Min(r.ReadUInt32(), 0x10FFFFu);
            var startGlyph = r.ReadUInt32();
            for (var code = start; code <= end && code >= start; code++)
            {
                var gid = manyToOne ? startGlyph : startGlyph + (code - start);
                if (gid >= numGlyphs)
                {
                    break;
                }

                Add(map, (int)code, (int)gid);
            }
        }
    }

    private static void Add(Dictionary<int, ushort> map, int code, int gid)
    {
        if (gid != 0)
        {
            map.TryAdd(code, (ushort)gid);
        }
    }
}

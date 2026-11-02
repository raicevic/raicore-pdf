using System.Buffers.Binary;

namespace RaiCore.Pdf.Fonts.OpenType;

/// <summary>
/// Produces a minimal TrueType font containing only the requested glyphs, for embedding as
/// <c>/FontFile2</c>. Glyph IDs are kept unchanged (unused glyphs become empty), so content
/// streams can use the original glyph IDs and composite glyphs need no rewriting.
/// </summary>
internal static class TrueTypeSubsetter
{
    // Tables a PDF consumer needs to rasterize a CIDFontType2 with an identity CIDToGIDMap.
    private static readonly string[] CopiedTables = ["cvt ", "fpgm", "prep", "gasp"];

    public static byte[] Subset(OpenTypeFont font, IEnumerable<int> glyphIds)
    {
        if (font.Outlines != OutlineFormat.TrueType)
        {
            throw new NotSupportedException("Only fonts with TrueType outlines can be subset.");
        }

        var glyf = font.GetTable("glyf");
        var locaOffsets = ReadLoca(font);
        var keep = CloseOverComposites(glyf, locaOffsets, glyphIds.Append(0), font.NumGlyphs);
        var numGlyphs = keep.Max + 1;

        // glyf + loca (always long format).
        using var newGlyf = new MemoryStream();
        var newLoca = new byte[(numGlyphs + 1) * 4];
        for (var gid = 0; gid < numGlyphs; gid++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(gid * 4), (uint)newGlyf.Position);
            if (keep.Contains(gid))
            {
                newGlyf.Write(glyf[locaOffsets[gid]..locaOffsets[gid + 1]]);
                while (newGlyf.Position % 4 != 0)
                {
                    newGlyf.WriteByte(0);
                }
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(numGlyphs * 4), (uint)newGlyf.Position);

        // hmtx with a full metric per glyph; unused glyphs get zero width.
        var hmtx = new byte[numGlyphs * 4];
        for (var gid = 0; gid < numGlyphs; gid++)
        {
            if (keep.Contains(gid))
            {
                BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(gid * 4), (ushort)font.GetAdvanceWidth(gid));
                BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan((gid * 4) + 2), font.GetLeftSideBearing(gid));
            }
        }

        var head = font.GetTable("head").ToArray();
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1); // indexToLocFormat = long

        var hhea = font.GetTable("hhea").ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)numGlyphs);

        var maxp = font.GetTable("maxp").ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)numGlyphs);

        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = head,
            ["hhea"] = hhea,
            ["maxp"] = maxp,
            ["hmtx"] = hmtx,
            ["loca"] = newLoca,
            ["glyf"] = newGlyf.ToArray(),
        };

        foreach (var tag in CopiedTables)
        {
            if (font.HasTable(tag))
            {
                tables[tag] = font.GetTable(tag).ToArray();
            }
        }

        return SfntBuilder.Build(0x00010000, tables);
    }

    /// <summary>Glyph data offsets, one per glyph plus the end offset.</summary>
    internal static int[] ReadLoca(OpenTypeFont font)
    {
        var loca = font.GetTable("loca");
        var glyfLength = font.GetTable("glyf").Length;
        var count = font.NumGlyphs + 1;
        var offsets = new int[count];
        var isShort = font.IndexToLocFormat == 0;
        if (loca.Length < count * (isShort ? 2 : 4))
        {
            throw new FontFormatException("The loca table is truncated.");
        }

        for (var i = 0; i < count; i++)
        {
            var offset = isShort
                ? BinaryPrimitives.ReadUInt16BigEndian(loca[(i * 2)..]) * 2
                : (int)BinaryPrimitives.ReadUInt32BigEndian(loca[(i * 4)..]);
            offsets[i] = Math.Clamp(offset, i > 0 ? offsets[i - 1] : 0, glyfLength);
        }

        return offsets;
    }

    /// <summary>Adds every component glyph referenced by composite glyphs, recursively.</summary>
    private static SortedSet<int> CloseOverComposites(ReadOnlySpan<byte> glyf, int[] loca, IEnumerable<int> glyphIds, int numGlyphs)
    {
        var keep = new SortedSet<int>();
        var pending = new Stack<int>(glyphIds.Where(g => g >= 0 && g < numGlyphs));
        while (pending.Count > 0)
        {
            var gid = pending.Pop();
            if (!keep.Add(gid))
            {
                continue;
            }

            var glyph = glyf[loca[gid]..loca[gid + 1]];
            if (glyph.Length < 10 || BinaryPrimitives.ReadInt16BigEndian(glyph) >= 0)
            {
                continue;
            }

            var pos = 10;
            ushort flags;
            do
            {
                if (pos + 4 > glyph.Length)
                {
                    break;
                }

                flags = BinaryPrimitives.ReadUInt16BigEndian(glyph[pos..]);
                int component = BinaryPrimitives.ReadUInt16BigEndian(glyph[(pos + 2)..]);
                if (component < numGlyphs)
                {
                    pending.Push(component);
                }

                pos += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
                pos += (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
            }
            while ((flags & 0x0020) != 0);
        }

        return keep;
    }
}

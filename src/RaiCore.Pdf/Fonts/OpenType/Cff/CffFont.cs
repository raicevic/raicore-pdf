using System.Buffers.Binary;

namespace RaiCore.Pdf.Fonts.OpenType.Cff;

/// <summary>A Font DICT with its Private DICT and local subroutines. Name-keyed fonts have one, made from the Top DICT.</summary>
internal sealed record CffFontDict(CffDict? FontDict, CffDict Private, CffIndex LocalSubrs);

/// <summary>
/// The parts of a <c>CFF </c> or <c>CFF2</c> table needed to subset it: charstrings, global and local
/// subroutines, the Font DICTs with their Private DICTs, the glyph-to-Font-DICT mapping and, for CFF2, the
/// number of variation regions per item variation data (to skip <c>blend</c> deltas).
/// </summary>
internal sealed class CffFont
{
    private readonly byte[] _fdSelect;

    private CffFont(
        bool isCff2, CffDict topDict, CffIndex charStrings, CffIndex globalSubrs, CffFontDict[] fontDicts, byte[] fdSelect,
        int[] regionCounts, int[]? cids = null)
    {
        Cids = cids;
        IsCff2 = isCff2;
        TopDict = topDict;
        CharStrings = charStrings;
        GlobalSubrs = globalSubrs;
        FontDicts = fontDicts;
        _fdSelect = fdSelect;
        RegionCounts = regionCounts;
    }

    public bool IsCff2 { get; }

    /// <summary>CID-keyed (has an FDArray); CFF2 fonts always are.</summary>
    public bool IsCidKeyed => TopDict.Has(CffDict.Ros) || IsCff2;

    public CffDict TopDict { get; }

    public CffIndex CharStrings { get; }

    public int NumGlyphs => CharStrings.Count;

    public CffIndex GlobalSubrs { get; }

    public IReadOnlyList<CffFontDict> FontDicts { get; }

    /// <summary>CID-keyed CFF: the CID of each glyph, from the charset (null for name-keyed and CFF2 fonts).</summary>
    public int[]? Cids { get; }

    /// <summary>CFF2: regions per item variation data, indexed by <c>vsindex</c>.</summary>
    public int[] RegionCounts { get; }

    /// <summary>Index of the Font DICT that glyph <paramref name="glyphId"/> uses.</summary>
    public int FontDictIndex(int glyphId) => _fdSelect.Length == 0 ? 0 : _fdSelect[glyphId];

    /// <summary>Parses the font's <c>CFF </c> or <c>CFF2</c> table.</summary>
    public static CffFont Load(OpenTypeFont font) =>
        font.HasTable("CFF ") ? Parse(font.GetTable("CFF ").ToArray()) : ParseCff2(font.GetTable("CFF2").ToArray());

    /// <summary>Parses a bare CFF (version 1) font program.</summary>
    public static CffFont Parse(byte[] data)
    {
        if (data.Length < 4 || data[0] != 1)
        {
            throw new FontFormatException("Not a CFF version 1 table.");
        }

        var pos = (int)data[2];
        CffIndex.Read(data, ref pos, cff2: false); // Name INDEX
        var topDicts = CffIndex.Read(data, ref pos, cff2: false);
        CffIndex.Read(data, ref pos, cff2: false); // String INDEX
        var globalSubrs = CffIndex.Read(data, ref pos, cff2: false);
        if (topDicts.Count == 0)
        {
            throw new FontFormatException("The CFF table has no Top DICT.");
        }

        var top = CffDict.Parse(topDicts[0]);
        if (top.GetInt(CffDict.CharstringType, 2) != 2)
        {
            throw new NotSupportedException("Only Type 2 charstrings are supported.");
        }

        var charStrings = ReadIndexAt(data, top, CffDict.CharStrings, cff2: false);
        if (charStrings.Count == 0)
        {
            throw new FontFormatException("The CFF table has no charstrings.");
        }

        if (!top.Has(CffDict.Ros))
        {
            var privateDict = ReadPrivate(data, top, cff2: false, null);
            return new CffFont(false, top, charStrings, globalSubrs, [privateDict], [], []);
        }

        var fontDicts = ReadFdArray(data, top, cff2: false, null);
        var fdSelect = ReadFdSelect(data, top.GetInt(CffDict.FdSelect, -1), charStrings.Count, fontDicts.Length);
        var cids = ReadCharset(data, top.GetInt(CffDict.Charset, -1), charStrings.Count);
        return new CffFont(false, top, charStrings, globalSubrs, fontDicts, fdSelect, [], cids);
    }

    private static CffFont ParseCff2(byte[] data)
    {
        if (data.Length < 5 || data[0] != 2)
        {
            throw new FontFormatException("Not a CFF2 table.");
        }

        int headerSize = data[2];
        int topDictLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(3));
        if (headerSize + topDictLength > data.Length)
        {
            throw new FontFormatException("The CFF2 Top DICT is truncated.");
        }

        var top = CffDict.Parse(data.AsSpan(headerSize, topDictLength));
        var pos = headerSize + topDictLength;
        var globalSubrs = CffIndex.Read(data, ref pos, cff2: true);
        var charStrings = ReadIndexAt(data, top, CffDict.CharStrings, cff2: true);
        if (charStrings.Count is 0 or > 65535)
        {
            throw new FontFormatException("The CFF2 table has an invalid number of charstrings.");
        }

        var regionCounts = top.Has(CffDict.VStore) ? ReadRegionCounts(data, top.GetInt(CffDict.VStore, 0)) : [];
        var fontDicts = ReadFdArray(data, top, cff2: true, regionCounts);
        var fdSelect = top.Has(CffDict.FdSelect)
            ? ReadFdSelect(data, top.GetInt(CffDict.FdSelect, -1), charStrings.Count, fontDicts.Length)
            : fontDicts.Length == 1 ? [] : throw new FontFormatException("The CFF2 table has several Font DICTs but no FDSelect.");
        return new CffFont(true, top, charStrings, globalSubrs, fontDicts, fdSelect, regionCounts);
    }

    private static CffIndex ReadIndexAt(byte[] data, CffDict dict, int op, bool cff2)
    {
        var offset = dict.GetInt(op, -1);
        if (offset < 0)
        {
            throw new FontFormatException("A required CFF structure is missing.");
        }

        return CffIndex.Read(data, ref offset, cff2);
    }

    private static CffFontDict[] ReadFdArray(byte[] data, CffDict top, bool cff2, int[]? regionCounts)
    {
        var fdArray = ReadIndexAt(data, top, CffDict.FdArray, cff2);
        if (fdArray.Count is 0 or > 256)
        {
            throw new FontFormatException("The CFF FDArray has an invalid size.");
        }

        var result = new CffFontDict[fdArray.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var fontDict = CffDict.Parse(fdArray[i]);
            result[i] = ReadPrivate(data, fontDict, cff2, regionCounts) with { FontDict = fontDict };
        }

        return result;
    }

    /// <summary>The Private DICT that <paramref name="dict"/> points to, with its local subroutines.</summary>
    private static CffFontDict ReadPrivate(byte[] data, CffDict dict, bool cff2, int[]? regionCounts)
    {
        var entry = dict.Get(CffDict.Private);
        if (entry is not { Length: 2 })
        {
            throw new FontFormatException("A CFF Font DICT has no Private DICT.");
        }

        var size = (int)entry[0].Value;
        var offset = (int)entry[1].Value;
        if (size < 0 || offset < 0 || offset > data.Length - size)
        {
            throw new FontFormatException("A CFF Private DICT lies outside the table.");
        }

        var privateDict = CffDict.Parse(data.AsSpan(offset, size), cff2 ? regionCounts ?? [] : null);
        var subrs = CffIndex.Empty;
        var subrsOffset = privateDict.GetInt(CffDict.Subrs, -1);
        if (subrsOffset >= 0)
        {
            var position = offset + subrsOffset;
            subrs = CffIndex.Read(data, ref position, cff2);
        }

        return new CffFontDict(null, privateDict, subrs);
    }

    private static byte[] ReadFdSelect(byte[] data, int offset, int numGlyphs, int numFontDicts)
    {
        if (offset < 0 || offset >= data.Length)
        {
            throw new FontFormatException("The CFF FDSelect is missing.");
        }

        var reader = new BigEndianReader(data, offset);
        var result = new byte[numGlyphs];
        var format = reader.ReadByte();
        switch (format)
        {
            case 0:
                for (var i = 0; i < numGlyphs; i++)
                {
                    result[i] = reader.ReadByte();
                }

                break;
            case 3:
            case 4:
                var nRanges = format == 3 ? reader.ReadUInt16() : (int)reader.ReadUInt32();
                var first = format == 3 ? reader.ReadUInt16() : (int)reader.ReadUInt32();
                for (var r = 0; r < nRanges; r++)
                {
                    var fd = format == 3 ? reader.ReadByte() : reader.ReadUInt16();
                    var next = format == 3 ? reader.ReadUInt16() : (int)reader.ReadUInt32();
                    if (next < first || fd > 255)
                    {
                        throw new FontFormatException("The CFF FDSelect has an invalid range.");
                    }

                    for (var g = first; g < Math.Min(next, numGlyphs); g++)
                    {
                        result[g] = (byte)fd;
                    }

                    first = next;
                }

                break;
            default:
                throw new FontFormatException($"Unknown CFF FDSelect format {format}.");
        }

        if (result.Any(fd => fd >= numFontDicts))
        {
            throw new FontFormatException("The CFF FDSelect refers to a missing Font DICT.");
        }

        return result;
    }

    /// <summary>The charset of a CID-keyed font: the CID of each glyph (glyph 0 is CID 0).</summary>
    private static int[] ReadCharset(byte[] data, int offset, int numGlyphs)
    {
        if (offset <= 2)
        {
            throw new FontFormatException("A CID-keyed CFF font needs a custom charset.");
        }

        var reader = new BigEndianReader(data, offset);
        var cids = new int[numGlyphs];
        var format = reader.ReadByte();
        for (var gid = 1; gid < numGlyphs;)
        {
            switch (format)
            {
                case 0:
                    cids[gid++] = reader.ReadUInt16();
                    break;
                case 1:
                case 2:
                    var first = reader.ReadUInt16();
                    var left = format == 1 ? reader.ReadByte() : reader.ReadUInt16();
                    for (var i = 0; i <= left && gid < numGlyphs; i++)
                    {
                        cids[gid++] = first + i;
                    }

                    break;
                default:
                    throw new FontFormatException($"Unknown CFF charset format {format}.");
            }
        }

        return cids;
    }

    /// <summary>CFF2 VariationStore: the region count of each item variation data.</summary>
    private static int[] ReadRegionCounts(byte[] data, int offset)
    {
        var reader = new BigEndianReader(data, offset + 2); // Skip the 16-bit length.
        var store = reader.Position;
        reader.ReadUInt16(); // format
        reader.ReadUInt32(); // region list offset
        var count = reader.ReadUInt16();
        var offsets = new uint[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = reader.ReadUInt32();
        }

        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            reader.Position = store + (int)offsets[i];
            reader.Skip(4); // item count, word delta count
            result[i] = reader.ReadUInt16();
        }

        return result;
    }
}

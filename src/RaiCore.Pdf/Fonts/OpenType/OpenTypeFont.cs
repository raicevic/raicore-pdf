using System.Text;

namespace RaiCore.Pdf.Fonts.OpenType;

/// <summary>Outline format of an OpenType font.</summary>
internal enum OutlineFormat
{
    /// <summary>Quadratic TrueType outlines (<c>glyf</c>/<c>loca</c>).</summary>
    TrueType,

    /// <summary>Cubic PostScript outlines (<c>CFF </c>).</summary>
    Cff,
}

/// <summary>
/// A parsed OpenType/TrueType font face. Holds the raw SFNT data and the metrics and character
/// mapping needed for measuring, embedding and subsetting.
/// </summary>
internal sealed class OpenTypeFont
{
    private const uint TagTtcf = 0x74746366; // 'ttcf'
    private const uint TagWoff = 0x774F4646; // 'wOFF'
    private const uint TagWoff2 = 0x774F4632; // 'wOF2'
    private const uint TagOtto = 0x4F54544F; // 'OTTO'
    private const uint TagTrue = 0x74727565; // 'true'
    private const uint SfntVersion1 = 0x00010000;

    private readonly Dictionary<string, (int Offset, int Length)> _tables;
    private readonly ushort[] _advanceWidths;
    private Dictionary<int, ushort>? _cmap;

    private OpenTypeFont(byte[] data, int faceOffset, int faceIndex)
    {
        Data = data;
        FaceIndex = faceIndex;
        var reader = new BigEndianReader(data, faceOffset);
        SfntVersion = reader.ReadUInt32();
        if (SfntVersion is not (SfntVersion1 or TagOtto or TagTrue))
        {
            throw new FontFormatException($"Not an OpenType font (signature 0x{SfntVersion:X8}).");
        }

        var numTables = reader.ReadUInt16();
        reader.Skip(6);
        _tables = new Dictionary<string, (int, int)>(numTables, StringComparer.Ordinal);
        for (var i = 0; i < numTables; i++)
        {
            var tag = reader.ReadTag();
            reader.Skip(4); // checksum
            var offset = (int)reader.ReadUInt32();
            var length = (int)reader.ReadUInt32();
            if (offset < 0 || length < 0 || offset > data.Length - length)
            {
                throw new FontFormatException($"Table '{tag}' lies outside the font data.");
            }

            _tables[tag] = (offset, length);
        }

        Outlines = HasTable("CFF ") || HasTable("CFF2") ? OutlineFormat.Cff : OutlineFormat.TrueType;
        if (Outlines == OutlineFormat.TrueType && (!HasTable("glyf") || !HasTable("loca")))
        {
            throw new FontFormatException("The font has no glyph outlines (glyf/loca or CFF table).");
        }

        ReadHead();
        ReadMaxp();
        _advanceWidths = ReadHorizontalMetrics();
        ReadPost();
        ReadOs2();
        ReadNames();
    }

    /// <summary>The raw font data. For collections this is the whole collection file.</summary>
    public byte[] Data { get; }

    /// <summary>Index of this face within <see cref="Data"/> (non-zero only for collections).</summary>
    public int FaceIndex { get; }

    public uint SfntVersion { get; }

    public OutlineFormat Outlines { get; }

    public int UnitsPerEm { get; private set; }

    public short XMin { get; private set; }

    public short YMin { get; private set; }

    public short XMax { get; private set; }

    public short YMax { get; private set; }

    /// <summary><c>head.indexToLocFormat</c>: 0 = short offsets, 1 = long offsets.</summary>
    public short IndexToLocFormat { get; private set; }

    public int NumGlyphs { get; private set; }

    public int NumberOfHMetrics { get; private set; }

    public short Ascender { get; private set; }

    public short Descender { get; private set; }

    public short LineGap { get; private set; }

    public short CapHeight { get; private set; }

    public short XHeight { get; private set; }

    public int WeightClass { get; private set; } = 400;

    /// <summary>OS/2 <c>fsType</c> embedding permissions.</summary>
    public ushort FsType { get; private set; }

    public double ItalicAngle { get; private set; }

    public bool IsFixedPitch { get; private set; }

    public bool IsItalic { get; private set; }

    public bool IsBold { get; private set; }

    public string FamilyName { get; private set; } = string.Empty;

    public string SubfamilyName { get; private set; } = string.Empty;

    public string FullName { get; private set; } = string.Empty;

    public string PostScriptName { get; private set; } = string.Empty;

    /// <summary>Whether the cmap is a Windows "symbol" table (codes in U+F000–U+F0FF).</summary>
    public bool IsSymbolCmap { get; private set; }

    /// <summary>Embedding is forbidden: fsType "Restricted License" without any permission bits.</summary>
    public bool EmbeddingRestricted => (FsType & 0x000F) == 0x0002;

    /// <summary>The font asks not to be subset (fsType bit 8).</summary>
    public bool NoSubsetting => (FsType & 0x0100) != 0;

    /// <summary>Parses a font from TrueType, OpenType, a collection face, WOFF or WOFF2 data.</summary>
    public static OpenTypeFont Load(byte[] data, int faceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 12)
        {
            throw new FontFormatException("The data is too short to be a font.");
        }

        var signature = new BigEndianReader(data).ReadUInt32();
        switch (signature)
        {
            case TagWoff:
                return Load(Woff.Decode(data), faceIndex);
            case TagWoff2:
                return Load(Woff2.Decode(data), faceIndex);
            case TagTtcf:
                var offsets = GetCollectionOffsets(data);
                if (faceIndex < 0 || faceIndex >= offsets.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(faceIndex), faceIndex, $"The collection has {offsets.Length} faces.");
                }

                return new OpenTypeFont(data, offsets[faceIndex], faceIndex);
            default:
                if (faceIndex != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(faceIndex), faceIndex, "The font is not a collection; only face 0 exists.");
                }

                return new OpenTypeFont(data, 0, 0);
        }
    }

    /// <summary>Number of faces in the data: the collection size for <c>.ttc</c>, otherwise 1.</summary>
    public static int GetFaceCount(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Length >= 12 && new BigEndianReader(data).ReadUInt32() == TagTtcf ? GetCollectionOffsets(data).Length : 1;
    }

    public IEnumerable<string> TableTags => _tables.Keys;

    public bool HasTable(string tag) => _tables.ContainsKey(tag);

    public ReadOnlySpan<byte> GetTable(string tag) =>
        _tables.TryGetValue(tag, out var t) ? Data.AsSpan(t.Offset, t.Length) : ReadOnlySpan<byte>.Empty;

    private BigEndianReader TableReader(string tag, int minimumLength)
    {
        if (!_tables.TryGetValue(tag, out var t))
        {
            throw new FontFormatException($"Required table '{tag}' is missing.");
        }

        if (t.Length < minimumLength)
        {
            throw new FontFormatException($"Table '{tag}' is truncated.");
        }

        return new BigEndianReader(Data, t.Offset);
    }

    /// <summary>Returns the glyph for a Unicode code point, or 0 (.notdef) if the font has none.</summary>
    public ushort GetGlyphId(int codePoint)
    {
        var cmap = EnsureCmap();
        if (cmap.TryGetValue(codePoint, out var gid))
        {
            return gid;
        }

        // Symbol fonts map their glyphs to U+F000–U+F0FF; accept plain 8-bit codes too.
        if (IsSymbolCmap && codePoint <= 0xFF && cmap.TryGetValue(0xF000 | codePoint, out gid))
        {
            return gid;
        }

        return 0;
    }

    /// <summary>All mapped code points (for diagnostics and tests).</summary>
    public IReadOnlyCollection<int> GetMappedCodePoints() => EnsureCmap().Keys;

    private Dictionary<int, ushort> EnsureCmap()
    {
        if (_cmap is null)
        {
            _cmap = CmapParser.Parse(this, out var isSymbol);
            IsSymbolCmap = isSymbol;
        }

        return _cmap;
    }

    /// <summary>Advance width of a glyph in font units.</summary>
    public int GetAdvanceWidth(int glyphId)
    {
        if (_advanceWidths.Length == 0)
        {
            return 0;
        }

        return _advanceWidths[Math.Clamp(glyphId, 0, _advanceWidths.Length - 1)];
    }

    /// <summary>Left side bearing of a glyph in font units (from <c>hmtx</c>).</summary>
    public short GetLeftSideBearing(int glyphId)
    {
        var hmtx = TableReader("hmtx", 0);
        var start = hmtx.Position;
        if (glyphId < NumberOfHMetrics)
        {
            hmtx.Position = start + (glyphId * 4) + 2;
        }
        else
        {
            hmtx.Position = start + (NumberOfHMetrics * 4) + ((glyphId - NumberOfHMetrics) * 2);
        }

        return hmtx.Remaining >= 2 ? hmtx.ReadInt16() : (short)0;
    }

    private static int[] GetCollectionOffsets(byte[] data)
    {
        var reader = new BigEndianReader(data, 8);
        var count = reader.ReadUInt32();
        if (count == 0 || count > 10_000)
        {
            throw new FontFormatException("Invalid font collection header.");
        }

        var offsets = new int[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = (int)reader.ReadUInt32();
        }

        return offsets;
    }

    private void ReadHead()
    {
        var r = TableReader("head", 54);
        var start = r.Position;
        r.Position = start + 18;
        UnitsPerEm = r.ReadUInt16();
        if (UnitsPerEm is < 16 or > 16384)
        {
            throw new FontFormatException($"Invalid unitsPerEm {UnitsPerEm}.");
        }

        r.Position = start + 36;
        XMin = r.ReadInt16();
        YMin = r.ReadInt16();
        XMax = r.ReadInt16();
        YMax = r.ReadInt16();
        var macStyle = r.ReadUInt16();
        IsBold = (macStyle & 1) != 0;
        IsItalic = (macStyle & 2) != 0;
        r.Position = start + 50;
        IndexToLocFormat = r.ReadInt16();
    }

    private void ReadMaxp()
    {
        var r = TableReader("maxp", 6);
        r.Skip(4);
        NumGlyphs = r.ReadUInt16();
    }

    private ushort[] ReadHorizontalMetrics()
    {
        var hhea = TableReader("hhea", 36);
        var start = hhea.Position;
        hhea.Position = start + 4;
        Ascender = hhea.ReadInt16();
        Descender = hhea.ReadInt16();
        LineGap = hhea.ReadInt16();
        hhea.Position = start + 34;
        NumberOfHMetrics = Math.Min(hhea.ReadUInt16(), (ushort)NumGlyphs);
        if (NumberOfHMetrics == 0)
        {
            throw new FontFormatException("The font has no horizontal metrics.");
        }

        var hmtx = TableReader("hmtx", NumberOfHMetrics * 4);
        var widths = new ushort[NumGlyphs];
        for (var i = 0; i < NumberOfHMetrics; i++)
        {
            widths[i] = hmtx.ReadUInt16();
            hmtx.Skip(2);
        }

        // Glyphs past numberOfHMetrics repeat the last advance width (monospaced tails).
        for (var i = NumberOfHMetrics; i < NumGlyphs; i++)
        {
            widths[i] = widths[NumberOfHMetrics - 1];
        }

        return widths;
    }

    private void ReadPost()
    {
        if (!HasTable("post"))
        {
            return;
        }

        var r = TableReader("post", 16);
        r.Skip(4);
        ItalicAngle = r.ReadFixed();
        r.Skip(4);
        IsFixedPitch = r.ReadUInt32() != 0;
    }

    private void ReadOs2()
    {
        CapHeight = YMax;
        XHeight = (short)(UnitsPerEm / 2);
        if (!HasTable("OS/2"))
        {
            return;
        }

        var r = TableReader("OS/2", 78);
        var start = r.Position;
        var version = r.ReadUInt16();
        r.Skip(2);
        WeightClass = Math.Clamp((int)r.ReadUInt16(), 1, 1000);
        r.Skip(2);
        FsType = r.ReadUInt16();
        r.Position = start + 62;
        var fsSelection = r.ReadUInt16();
        IsItalic |= (fsSelection & 0x0001) != 0;
        IsBold |= (fsSelection & 0x0020) != 0;
        r.Position = start + 68;
        var typoAscender = r.ReadInt16();
        var typoDescender = r.ReadInt16();
        var typoLineGap = r.ReadInt16();

        // Browsers honor USE_TYPO_METRICS (bit 7); otherwise hhea metrics are used.
        if ((fsSelection & 0x0080) != 0)
        {
            Ascender = typoAscender;
            Descender = typoDescender;
            LineGap = typoLineGap;
        }

        if (version >= 2 && GetTable("OS/2").Length >= 92)
        {
            r.Position = start + 86;
            var xHeight = r.ReadInt16();
            var capHeight = r.ReadInt16();
            if (xHeight > 0)
            {
                XHeight = xHeight;
            }

            if (capHeight > 0)
            {
                CapHeight = capHeight;
            }
        }
    }

    private void ReadNames()
    {
        if (!_tables.TryGetValue("name", out var t))
        {
            return;
        }

        var names = NameTable.Read(Data.AsSpan(t.Offset, t.Length));
        FamilyName = names.Family;
        SubfamilyName = names.Subfamily;
        FullName = names.FullName;
        PostScriptName = SanitizePostScriptName(names.PostScriptName is { Length: > 0 } ps ? ps : FullName);
    }

    /// <summary>PostScript names may only contain printable ASCII without delimiters or spaces.</summary>
    private static string SanitizePostScriptName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is > ' ' and < (char)0x7F and not ('[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '/' or '%'))
            {
                builder.Append(c);
            }
        }

        return builder.Length > 0 ? builder.ToString() : "Font";
    }
}

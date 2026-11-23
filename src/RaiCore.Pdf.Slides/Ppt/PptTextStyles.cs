namespace RaiCore.Pdf.Slides.Ppt;

/// <summary>A color of text properties (<c>ColorIndexStruct</c>): red, green, blue, or a color scheme index.</summary>
/// <param name="Red">The red component.</param>
/// <param name="Green">The green component.</param>
/// <param name="Blue">The blue component.</param>
/// <param name="Index">0xFE for the RGB components, 0 to 7 for a color of the scheme.</param>
internal readonly record struct ColorIndex(byte Red, byte Green, byte Blue, byte Index);

/// <summary>The paragraph properties of a <c>TextPFException</c>; <c>null</c> for those it does not set.</summary>
internal sealed record ParagraphProps
{
    public bool? HasBullet { get; init; }

    public bool? BulletHasFont { get; init; }

    public bool? BulletHasColor { get; init; }

    public bool? BulletHasSize { get; init; }

    public char? BulletChar { get; init; }

    public int? BulletFont { get; init; }

    /// <summary>25 to 400: a percentage of the text size; negative: the size in points, negated.</summary>
    public int? BulletSize { get; init; }

    public ColorIndex? BulletColor { get; init; }

    /// <summary>0 left, 1 center, 2 right, 3 justify (4 to 6 justify variants).</summary>
    public int? Align { get; init; }

    /// <summary>Non-negative: a percentage of single spacing; negative: master units, negated.</summary>
    public int? LineSpacing { get; init; }

    public int? SpaceBefore { get; init; }

    public int? SpaceAfter { get; init; }

    /// <summary>Where the text starts, in master units (the spec's <c>leftMargin</c>).</summary>
    public int? TextOffset { get; init; }

    /// <summary>Where the first line (its bullet) starts, in master units (the spec's <c>indent</c>).</summary>
    public int? BulletOffset { get; init; }

    /// <summary>These properties with those another set sets taking their place.</summary>
    public ParagraphProps Merge(ParagraphProps? over) => over is null ? this : new ParagraphProps
    {
        HasBullet = over.HasBullet ?? HasBullet,
        BulletHasFont = over.BulletHasFont ?? BulletHasFont,
        BulletHasColor = over.BulletHasColor ?? BulletHasColor,
        BulletHasSize = over.BulletHasSize ?? BulletHasSize,
        BulletChar = over.BulletChar ?? BulletChar,
        BulletFont = over.BulletFont ?? BulletFont,
        BulletSize = over.BulletSize ?? BulletSize,
        BulletColor = over.BulletColor ?? BulletColor,
        Align = over.Align ?? Align,
        LineSpacing = over.LineSpacing ?? LineSpacing,
        SpaceBefore = over.SpaceBefore ?? SpaceBefore,
        SpaceAfter = over.SpaceAfter ?? SpaceAfter,
        TextOffset = over.TextOffset ?? TextOffset,
        BulletOffset = over.BulletOffset ?? BulletOffset,
    };
}

/// <summary>The character properties of a <c>TextCFException</c>; <c>null</c> for those it does not set.</summary>
internal sealed record CharacterProps
{
    public bool? Bold { get; init; }

    public bool? Italic { get; init; }

    public bool? Underline { get; init; }

    /// <summary>An index into the font collection.</summary>
    public int? Font { get; init; }

    /// <summary>An index into the font collection, for East Asian text.</summary>
    public int? EastAsianFont { get; init; }

    /// <summary>An index into the font collection, for symbols.</summary>
    public int? SymbolFont { get; init; }

    /// <summary>The size in points.</summary>
    public int? Size { get; init; }

    public ColorIndex? Color { get; init; }

    /// <summary>The baseline shift, a percentage of the size (positive: superscript).</summary>
    public int? Position { get; init; }

    /// <summary>These properties with those another set sets taking their place.</summary>
    public CharacterProps Merge(CharacterProps? over) => over is null ? this : new CharacterProps
    {
        Bold = over.Bold ?? Bold,
        Italic = over.Italic ?? Italic,
        Underline = over.Underline ?? Underline,
        Font = over.Font ?? Font,
        EastAsianFont = over.EastAsianFont ?? EastAsianFont,
        SymbolFont = over.SymbolFont ?? SymbolFont,
        Size = over.Size ?? Size,
        Color = over.Color ?? Color,
        Position = over.Position ?? Position,
    };
}

/// <summary>A run of paragraph properties: how many characters it covers, its indent level and its properties.</summary>
internal readonly record struct ParagraphRun(int Count, int Level, ParagraphProps Props);

/// <summary>A run of character properties: how many characters it covers and its properties.</summary>
internal readonly record struct CharacterRun(int Count, CharacterProps Props);

/// <summary>The indents of a text ruler (<c>TextRuler</c>), per level, in master units.</summary>
internal sealed class TextRuler
{
    public int?[] TextOffsets { get; } = new int?[5];

    public int?[] BulletOffsets { get; } = new int?[5];
}

/// <summary>
/// Reads the text property structures of [MS-PPT] 2.9: <c>TextPFException</c>, <c>TextCFException</c>, the runs of a
/// <c>StyleTextPropAtom</c>, the levels of a <c>TextMasterStyleAtom</c> and a <c>TextRuler</c>. A structure cut short
/// by the end of its record keeps what was read.
/// </summary>
internal static class PptTextStyles
{
    /// <summary>The most indent levels of a master style.</summary>
    public const int Levels = 5;

    /// <summary>Reads a <c>TextPFException</c>.</summary>
    public static ParagraphProps Paragraph(ref Cursor c)
    {
        var masks = c.U32();
        bool Has(uint bit) => (masks & bit) != 0;
        int? flags = (masks & 0xF) != 0 ? c.U16() : null;
        var props = new ParagraphProps
        {
            HasBullet = Has(0x1) && flags is { } f1 ? (f1 & 0x1) != 0 : null,
            BulletHasFont = Has(0x2) && flags is { } f2 ? (f2 & 0x2) != 0 : null,
            BulletHasColor = Has(0x4) && flags is { } f3 ? (f3 & 0x4) != 0 : null,
            BulletHasSize = Has(0x8) && flags is { } f4 ? (f4 & 0x8) != 0 : null,
            BulletChar = Has(0x80) ? (char)c.U16() : null,
            BulletFont = Has(0x10) ? c.U16() : null,
            BulletSize = Has(0x40) ? c.I16() : null,
            BulletColor = Has(0x20) ? c.Color() : null,
            Align = Has(0x800) ? c.U16() : null,
            LineSpacing = Has(0x1000) ? c.I16() : null,
            SpaceBefore = Has(0x2000) ? c.I16() : null,
            SpaceAfter = Has(0x4000) ? c.I16() : null,
            TextOffset = Has(0x100) ? c.I16() : null,
            BulletOffset = Has(0x400) ? c.I16() : null,
        };

        // What follows is read only to get past it.
        if (Has(0x8000))
        {
            c.Skip(2); // defaultTabSize
        }

        if (Has(0x100000))
        {
            c.Skip(4 * Math.Min(c.U16(), 4096)); // tabStops
        }

        if (Has(0x10000))
        {
            c.Skip(2); // fontAlign
        }

        if ((masks & 0xE0000) != 0)
        {
            c.Skip(2); // wrapFlags
        }

        if (Has(0x200000))
        {
            c.Skip(2); // textDirection
        }

        return props;
    }

    /// <summary>Reads a <c>TextCFException</c>.</summary>
    public static CharacterProps Character(ref Cursor c)
    {
        var masks = c.U32();
        bool Has(uint bit) => (masks & bit) != 0;
        int? style = (masks & 0xFFFF) != 0 ? c.U16() : null;
        var props = new CharacterProps
        {
            Bold = Has(0x1) && style is { } s1 ? (s1 & 0x1) != 0 : null,
            Italic = Has(0x2) && style is { } s2 ? (s2 & 0x2) != 0 : null,
            Underline = Has(0x4) && style is { } s3 ? (s3 & 0x4) != 0 : null,
            Font = Has(0x10000) ? c.U16() : null,
            EastAsianFont = Has(0x200000) ? c.U16() : null,
        };
        if (Has(0x400000))
        {
            c.Skip(2); // ansiFontRef
        }

        props = props with
        {
            SymbolFont = Has(0x800000) ? c.U16() : null,
            Size = Has(0x20000) ? c.U16() : null,
            Color = Has(0x40000) ? c.Color() : null,
            Position = Has(0x80000) ? c.I16() : null,
        };

        // The PowerPoint 2002+ extensions (pp10runid, newEAFontRef, csFontRef, pp11ext) are not read: like Apache POI
        // and the files PowerPoint writes, they take no room here.
        return props;
    }

    /// <summary>
    /// Reads the runs of a <c>StyleTextPropAtom</c> for text of a length: paragraph runs, then character runs, each
    /// covering the text and its final paragraph mark.
    /// </summary>
    public static (List<ParagraphRun> Paragraphs, List<CharacterRun> Characters) Runs(ReadOnlySpan<byte> data, int textLength)
    {
        var c = new Cursor(data);
        var paragraphs = new List<ParagraphRun>();
        var characters = new List<CharacterRun>();
        var total = textLength + 1;
        for (var covered = 0; covered < total && !c.Failed;)
        {
            var count = (int)Math.Min(c.U32(), (uint)(total - covered));
            var level = c.U16();
            var props = Paragraph(ref c);
            if (c.Failed || count == 0)
            {
                break;
            }

            paragraphs.Add(new ParagraphRun(count, Math.Clamp(level, 0, Levels - 1), props));
            covered += count;
        }

        for (var covered = 0; covered < total && !c.Failed;)
        {
            var count = (int)Math.Min(c.U32(), (uint)(total - covered));
            var props = Character(ref c);
            if (c.Failed || count == 0)
            {
                break;
            }

            characters.Add(new CharacterRun(count, props));
            covered += count;
        }

        return (paragraphs, characters);
    }

    /// <summary>Reads the levels of a <c>TextMasterStyleAtom</c> of an instance (a text type).</summary>
    public static (ParagraphProps? Paragraph, CharacterProps? Character)[] Master(ReadOnlySpan<byte> data, int instance)
    {
        var levels = new (ParagraphProps?, CharacterProps?)[Levels];
        var c = new Cursor(data);
        var count = Math.Min(c.U16(), Levels);
        for (var i = 0; i < count && !c.Failed; i++)
        {
            var level = instance >= 5 ? c.U16() : i;
            var paragraph = Paragraph(ref c);
            var character = Character(ref c);
            if (c.Failed)
            {
                break;
            }

            levels[level is >= 0 and < Levels ? level : i] = (paragraph, character);
        }

        return levels;
    }

    /// <summary>Reads a <c>TextRuler</c>.</summary>
    public static TextRuler Ruler(ReadOnlySpan<byte> data)
    {
        var ruler = new TextRuler();
        var c = new Cursor(data);
        var masks = c.U32();
        if ((masks & 0x2) != 0)
        {
            c.Skip(2); // cLevels
        }

        if ((masks & 0x1) != 0)
        {
            c.Skip(2); // defaultTabSize
        }

        if ((masks & 0x4) != 0)
        {
            c.Skip(4 * Math.Min(c.U16(), 4096)); // tabs
        }

        for (var level = 0; level < Levels && !c.Failed; level++)
        {
            if ((masks & (1u << (3 + level))) != 0)
            {
                var value = c.I16();
                ruler.TextOffsets[level] = c.Failed ? null : value;
            }

            if ((masks & (1u << (8 + level))) != 0)
            {
                var value = c.I16();
                ruler.BulletOffsets[level] = c.Failed ? null : value;
            }
        }

        return ruler;
    }
}

/// <summary>Reads little-endian values in order; past the end it reads zeros and is <see cref="Failed"/>.</summary>
internal ref struct Cursor(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _at;

    /// <summary>Whether a read went past the end.</summary>
    public bool Failed { get; private set; }

    public int U16() => Take(2) is var at and >= 0 ? Bytes.U16(_data, at) : 0;

    public int I16() => Take(2) is var at and >= 0 ? Bytes.I16(_data, at) : 0;

    public uint U32() => Take(4) is var at and >= 0 ? Bytes.U32(_data, at) : 0;

    public ColorIndex Color() => Take(4) is var at and >= 0 ? new ColorIndex(_data[at], _data[at + 1], _data[at + 2], _data[at + 3]) : default;

    public void Skip(int count) => Take(count);

    private int Take(int count)
    {
        if (Failed || count < 0 || _at + count > _data.Length)
        {
            Failed = true;
            return -1;
        }

        var at = _at;
        _at += count;
        return at;
    }
}

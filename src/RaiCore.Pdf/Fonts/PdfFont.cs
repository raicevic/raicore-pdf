using System.Text;
using RaiCore.Pdf.Fonts.OpenType;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Fonts;

/// <summary>
/// A font that can be used to draw and measure text: either one of the 14 standard PDF fonts or a
/// TrueType/OpenType font loaded from a file (TTF, OTF, TTC, WOFF or WOFF2), which is embedded and subset.
/// </summary>
/// <remarks>
/// Vertical metrics (<see cref="Ascent"/>, <see cref="Descent"/>, ...) are fractions of the font size,
/// so multiply by the size in points. Instances are immutable and may be shared across documents and threads.
/// </remarks>
public abstract class PdfFont
{
    private static readonly StandardPdfFont[] StandardFonts =
        [.. Enum.GetValues<StandardFont>().Select(f => new StandardPdfFont(f))];

    private protected PdfFont()
    {
    }

    /// <summary>Family name, e.g. <c>Liberation Sans</c>.</summary>
    public abstract string FamilyName { get; }

    /// <summary>PostScript name, e.g. <c>LiberationSans-Bold</c>.</summary>
    public abstract string PostScriptName { get; }

    /// <summary>Weight on the CSS scale (100–900; 400 = normal, 700 = bold).</summary>
    public abstract int Weight { get; }

    /// <summary>Whether the font is italic or oblique.</summary>
    public abstract bool IsItalic { get; }

    /// <summary>Whether every glyph has the same advance width.</summary>
    public abstract bool IsMonospaced { get; }

    /// <summary>Distance from the baseline to the top of the line box, as a fraction of the font size.</summary>
    public abstract double Ascent { get; }

    /// <summary>Distance from the baseline to the bottom of the line box (negative), as a fraction of the font size.</summary>
    public abstract double Descent { get; }

    /// <summary>Recommended extra space between lines, as a fraction of the font size.</summary>
    public abstract double LineGap { get; }

    /// <summary>Height of capital letters, as a fraction of the font size.</summary>
    public abstract double CapHeight { get; }

    /// <summary>Height of lowercase letters such as "x", as a fraction of the font size.</summary>
    public abstract double XHeight { get; }

    /// <summary>Returns one of the 14 standard PDF fonts. These are not embedded and only cover WinAnsi (Western European) text.</summary>
    public static PdfFont Standard(StandardFont font) => StandardFonts[(int)font];

    /// <summary>Loads a font file (TTF, OTF, TTC, WOFF or WOFF2). For collections, <paramref name="faceIndex"/> selects the face.</summary>
    public static PdfFont Load(string path, int faceIndex = 0) => Load(File.ReadAllBytes(path), faceIndex);

    /// <summary>Loads a font from a stream. The stream is read to the end and left open.</summary>
    public static PdfFont Load(Stream stream, int faceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Load(buffer.ToArray(), faceIndex);
    }

    /// <summary>Loads a font from bytes. The array must not be modified afterwards.</summary>
    public static PdfFont Load(byte[] data, int faceIndex = 0) => new OpenTypePdfFont(OpenType.OpenTypeFont.Load(data, faceIndex));

    /// <summary>Number of faces in font data: more than 1 for TrueType collections (<c>.ttc</c>).</summary>
    public static int GetFaceCount(byte[] data) => OpenType.OpenTypeFont.GetFaceCount(data);

    /// <summary>Line height for "normal" line spacing: (ascent − descent + line gap) × size.</summary>
    public double GetLineHeight(double fontSize) => (Ascent - Descent + LineGap) * fontSize;

    /// <summary>Whether the font has a glyph for the Unicode code point.</summary>
    public abstract bool HasGlyph(int codePoint);

    /// <summary>Whether the font has glyphs for every character in <paramref name="text"/>.</summary>
    public bool HasGlyphs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!HasGlyph(rune.Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Width of <paramref name="text"/> in points at <paramref name="fontSize"/>, without kerning.</summary>
    public double MeasureText(string text, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(text);
        var width = 0.0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += GetAdvance(rune.Value);
        }

        return width * fontSize;
    }

    /// <inheritdoc />
    public override string ToString() => PostScriptName;

    /// <summary>Advance width of a code point as a fraction of the font size.</summary>
    internal abstract double GetAdvance(int codePoint);

    /// <summary>Encodes text for a <c>Tj</c> operator and records the glyphs used.</summary>
    internal abstract PdfString EncodeText(string text, FontUsage usage);

    /// <summary>Writes the font's PDF objects, embedding only the glyphs in <paramref name="usage"/>.</summary>
    internal abstract PdfReference WriteTo(PdfFileWriter writer, FontUsage usage);
}

/// <summary>Glyphs used from one font in one document, with the text each glyph represents.</summary>
internal sealed class FontUsage
{
    /// <summary>Glyph ID → the text it represents, or <c>null</c> for glyphs with no own text (e.g. extra glyphs of a cluster).</summary>
    public SortedDictionary<int, string?> Glyphs { get; } = [];

    /// <summary>Records a glyph. The first non-empty text wins, since ToUnicode maps each glyph to one string.</summary>
    public void Add(int glyphId, string? text)
    {
        if (!Glyphs.TryGetValue(glyphId, out var existing) || (string.IsNullOrEmpty(existing) && !string.IsNullOrEmpty(text)))
        {
            Glyphs[glyphId] = text;
        }
    }

    public void MergeFrom(FontUsage other)
    {
        foreach (var (gid, text) in other.Glyphs)
        {
            Add(gid, text);
        }
    }
}

/// <summary>One of the 14 standard fonts, measured with metric-compatible Liberation widths.</summary>
internal sealed class StandardPdfFont(StandardFont font) : PdfFont
{
    private readonly StandardFontMetrics.Metrics? _metrics = StandardFontMetrics.Get(font);

    public StandardFont Font { get; } = font;

    public override string FamilyName => Font switch
    {
        <= StandardFont.HelveticaBoldOblique => "Helvetica",
        <= StandardFont.TimesBoldItalic => "Times",
        <= StandardFont.CourierBoldOblique => "Courier",
        StandardFont.Symbol => "Symbol",
        _ => "ZapfDingbats",
    };

    public override string PostScriptName => StandardFontInfo.PostScriptName(Font);

    public override int Weight => Font is StandardFont.HelveticaBold or StandardFont.HelveticaBoldOblique
        or StandardFont.TimesBold or StandardFont.TimesBoldItalic
        or StandardFont.CourierBold or StandardFont.CourierBoldOblique ? 700 : 400;

    public override bool IsItalic => Font is StandardFont.HelveticaOblique or StandardFont.HelveticaBoldOblique
        or StandardFont.TimesItalic or StandardFont.TimesBoldItalic
        or StandardFont.CourierOblique or StandardFont.CourierBoldOblique;

    public override bool IsMonospaced => Font is >= StandardFont.Courier and <= StandardFont.CourierBoldOblique;

    public override double Ascent => (_metrics?.Ascent ?? 800) / 1000.0;

    public override double Descent => (_metrics?.Descent ?? -200) / 1000.0;

    public override double LineGap => (_metrics?.LineGap ?? 0) / 1000.0;

    public override double CapHeight => (_metrics?.CapHeight ?? 700) / 1000.0;

    public override double XHeight => (_metrics?.XHeight ?? 500) / 1000.0;

    public override bool HasGlyph(int codePoint) =>
        StandardFontInfo.UsesWinAnsi(Font)
            ? codePoint <= 0xFFFF && WinAnsiEncoding.CanEncode((char)codePoint)
            : codePoint is >= 0x20 and <= 0xFF;

    internal override double GetAdvance(int codePoint)
    {
        if (_metrics is null)
        {
            throw new NotSupportedException($"Measuring text is not supported for {PostScriptName}.");
        }

        var code = codePoint <= 0xFFFF ? WinAnsiEncoding.Encode((char)codePoint) : (byte)'?';
        return _metrics.Widths[code] / 1000.0;
    }

    internal override PdfString EncodeText(string text, FontUsage usage) =>
        new(StandardFontInfo.UsesWinAnsi(Font) ? WinAnsiEncoding.Encode(text) : Encoding.Latin1.GetBytes(text));

    internal override PdfReference WriteTo(PdfFileWriter writer, FontUsage usage)
    {
        var dict = new PdfDictionary
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("Type1"),
            ["BaseFont"] = new PdfName(PostScriptName),
        };
        if (StandardFontInfo.UsesWinAnsi(Font))
        {
            dict["Encoding"] = new PdfName("WinAnsiEncoding");
        }

        return writer.Add(dict);
    }
}

/// <summary>A TrueType/OpenType font embedded as a CIDFont with Identity-H encoding.</summary>
internal sealed class OpenTypePdfFont(OpenTypeFont font) : PdfFont
{
    public OpenTypeFont Font { get; } = font;

    public override string FamilyName => Font.FamilyName;

    public override string PostScriptName => Font.PostScriptName;

    public override int Weight => Math.Clamp((Font.WeightClass + 50) / 100 * 100, 100, 900);

    public override bool IsItalic => Font.IsItalic;

    public override bool IsMonospaced => Font.IsFixedPitch;

    public override double Ascent => Font.Ascender / (double)Font.UnitsPerEm;

    public override double Descent => Font.Descender / (double)Font.UnitsPerEm;

    public override double LineGap => Font.LineGap / (double)Font.UnitsPerEm;

    public override double CapHeight => Font.CapHeight / (double)Font.UnitsPerEm;

    public override double XHeight => Font.XHeight / (double)Font.UnitsPerEm;

    public override bool HasGlyph(int codePoint) => Font.GetGlyphId(codePoint) != 0;

    internal override double GetAdvance(int codePoint) =>
        Font.GetAdvanceWidth(Font.GetGlyphId(codePoint)) / (double)Font.UnitsPerEm;

    /// <summary>Advance width of a glyph as a fraction of the font size.</summary>
    internal double GetGlyphAdvance(int glyphId) => Font.GetAdvanceWidth(glyphId) / (double)Font.UnitsPerEm;

    internal void EnsureEmbeddable()
    {
        if (Font.EmbeddingRestricted)
        {
            throw new InvalidOperationException(
                $"The license of font '{Font.FullName}' does not permit embedding (OS/2 fsType = 0x{Font.FsType:X4}).");
        }
    }

    internal override PdfString EncodeText(string text, FontUsage usage)
    {
        if (Font.EmbeddingRestricted)
        {
            throw new InvalidOperationException(
                $"The license of font '{Font.FullName}' does not permit embedding (OS/2 fsType = 0x{Font.FsType:X4}).");
        }

        var bytes = new List<byte>(text.Length * 2);
        foreach (var rune in text.EnumerateRunes())
        {
            var gid = Font.GetGlyphId(rune.Value);
            usage.Add(gid, rune.ToString());
            bytes.Add((byte)(gid >> 8));
            bytes.Add((byte)gid);
        }

        return new PdfString([.. bytes], hex: true);
    }

    internal override PdfReference WriteTo(PdfFileWriter writer, FontUsage usage) =>
        CidFontWriter.Write(writer, Font, usage);
}

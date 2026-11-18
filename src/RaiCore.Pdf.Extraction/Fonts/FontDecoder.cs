using RaiCore.Pdf.Extraction.Content;

namespace RaiCore.Pdf.Extraction.Fonts;

/// <summary>The kinds of PDF font (ISO 32000-2, 9.2).</summary>
internal enum FontKind
{
    Type1,
    TrueType,
    Type3,
    Type0,
}

/// <summary>The text of a glyph.</summary>
/// <param name="Text">The characters, possibly several (a ligature) or none.</param>
/// <param name="IsGuess">
/// Whether nothing in the file maps the code to text and the code was taken as a Latin-1 character (simple fonts
/// only): often right for symbolic fonts that use ASCII codes, but not reliable.
/// </param>
internal readonly record struct GlyphText(string Text, bool IsGuess);

/// <summary>A glyph's vertical metrics (ISO 32000-2, 9.7.4.3), in text space units for a font size of 1.</summary>
/// <param name="Advance">The vertical displacement (negative: downwards).</param>
/// <param name="OriginX">The horizontal position of the vertical origin, from the horizontal one.</param>
/// <param name="OriginY">The vertical position of the vertical origin, from the horizontal one.</param>
internal readonly record struct VerticalMetrics(double Advance, double OriginX, double OriginY);

/// <summary>
/// What a font's string bytes mean: how they split into character codes, the text of each code, and its width. All
/// measures are in text space units for a font size of 1 (a width of 0.5 is half an em), Type 3 fonts included.
/// </summary>
internal sealed partial class FontDecoder
{
    private readonly ExtractionDiagnostics _diagnostics;

    // Simple fonts: one byte per code.
    private string?[]? _simpleText;
    private double[]? _simpleWidths;
    private double _missingWidth;

    // Composite fonts: codes from the encoding CMap, CIDs, widths by CID.
    private CMap? _encoding;
    private bool _unicodeEncoding;
    private Dictionary<int, double>? _cidWidths;
    private List<(int First, int Last, double Width)>? _cidWidthRanges;
    private double _defaultWidth = 1;
    private Dictionary<int, VerticalMetrics>? _verticalMetrics;
    private List<(int First, int Last, VerticalMetrics Metrics)>? _verticalRanges;
    private (double OriginY, double Advance) _defaultVertical = (0.88, -1);
    private Func<string?[]?>? _loadCidText;
    private string?[]? _cidText;
    private bool _cidTextLoaded;

    private CMap? _toUnicode;

    private FontDecoder(string name, FontKind kind, ExtractionDiagnostics diagnostics)
    {
        Name = name;
        Kind = kind;
        _diagnostics = diagnostics;
    }

    /// <summary>The font's name without its subset tag (<c>/BaseFont</c>; for Type 3 fonts, <c>/Name</c> if any).</summary>
    public string Name { get; }

    public FontKind Kind { get; }

    /// <summary>Whether the font program is embedded in the file.</summary>
    public bool IsEmbedded { get; private set; }

    /// <summary>Whether glyphs advance downwards (a composite font with a vertical CMap, such as Identity-V).</summary>
    public bool IsVertical => _encoding?.IsVertical ?? false;

    /// <summary>The top of the glyphs above the baseline (positive).</summary>
    public double Ascent { get; private set; } = 0.8;

    /// <summary>The bottom of the glyphs below the baseline (negative).</summary>
    public double Descent { get; private set; } = -0.2;

    /// <summary>For a Type 3 font: its font matrix, which maps glyph space to text space.</summary>
    public Matrix FontMatrix { get; private set; } = new(0.001, 0, 0, 0.001, 0, 0);

    /// <summary>Reads the code at the start of <paramref name="bytes"/> (which must not be empty); returns its length in bytes.</summary>
    public int NextCode(ReadOnlySpan<byte> bytes, out uint code)
    {
        if (_encoding is null)
        {
            code = bytes[0];
            return 1;
        }

        var length = _encoding.CodeLength(bytes, 2);
        code = 0;
        for (var i = 0; i < length; i++)
        {
            code = (code << 8) | bytes[i];
        }

        return length;
    }

    /// <summary>The text of a code, or <c>null</c> when nothing maps it to text.</summary>
    public GlyphText? Text(uint code, int length)
    {
        if (_toUnicode?.Text(code, length) is { } mapped)
        {
            return new GlyphText(mapped, false);
        }

        if (_simpleText is not null)
        {
            if (code < 256 && _simpleText[code] is { } text)
            {
                return new GlyphText(text, false);
            }

            return code is >= 0x21 and <= 0x7E or >= 0xA1 and <= 0xFF or 0x20
                ? new GlyphText(((char)code).ToString(), true)
                : null;
        }

        if (_unicodeEncoding)
        {
            return length switch
            {
                2 when code is not (>= 0xD800 and <= 0xDFFF) && code != 0 => new GlyphText(((char)code).ToString(), false),
                4 when char.IsSurrogatePair((char)(code >> 16), (char)(code & 0xFFFF)) =>
                    new GlyphText(new string([(char)(code >> 16), (char)(code & 0xFFFF)]), false),
                _ => null,
            };
        }

        if (CidText() is { } cidText && Cid(code, length) is var cid && cid < cidText.Length && cidText[cid] is { } byGlyph)
        {
            return new GlyphText(byGlyph, false);
        }

        return null;
    }

    /// <summary>The horizontal displacement of a code (w0).</summary>
    public double Width(uint code, int length)
    {
        if (_simpleWidths is not null)
        {
            return code < 256 && !double.IsNaN(_simpleWidths[code]) ? _simpleWidths[code] : _missingWidth;
        }

        var cid = Cid(code, length);
        if (_cidWidths is not null && _cidWidths.TryGetValue(cid, out var width))
        {
            return width;
        }

        if (_cidWidthRanges is not null)
        {
            foreach (var (first, last, rangeWidth) in _cidWidthRanges)
            {
                if (cid >= first && cid <= last)
                {
                    return rangeWidth;
                }
            }
        }

        return _defaultWidth;
    }

    /// <summary>The vertical metrics of a code, for vertical writing.</summary>
    public VerticalMetrics Vertical(uint code, int length)
    {
        var cid = Cid(code, length);
        if (_verticalMetrics is not null && _verticalMetrics.TryGetValue(cid, out var metrics))
        {
            return metrics;
        }

        if (_verticalRanges is not null)
        {
            foreach (var (first, last, rangeMetrics) in _verticalRanges)
            {
                if (cid >= first && cid <= last)
                {
                    return rangeMetrics;
                }
            }
        }

        return new VerticalMetrics(_defaultVertical.Advance, Width(code, length) / 2, _defaultVertical.OriginY);
    }

    /// <summary>
    /// Whether word spacing (<c>Tw</c>) applies to a code: a single-byte code 32 (ISO 32000-2, 9.3.3), in simple fonts
    /// and in composite fonts whose CMap has one-byte codes.
    /// </summary>
    public static bool IsWordSpace(uint code, int length) => length == 1 && code == 32;

    private int Cid(uint code, int length) => _encoding?.Cid(code, length) is { } cid ? cid : (int)Math.Min(code, int.MaxValue);

    private string?[]? CidText()
    {
        if (!_cidTextLoaded)
        {
            _cidTextLoaded = true;
            _cidText = _loadCidText?.Invoke();
        }

        return _cidText;
    }
}

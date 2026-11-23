using System.Text;

namespace RaiCore.Pdf.Metafiles;

/// <summary>
/// A logical pen ([MS-WMF] PenStyle): the style bits (dash pattern in the low 4, end cap 0x0F00, join 0xF000,
/// geometric 0x10000), width in logical units (0 is one device pixel), color and the user dash pattern.
/// </summary>
internal sealed record GdiPen(int Style, double Width, PdfColor Color, double[]? Dashes = null, bool Cosmetic = false)
{
    public const int Null = 5, InsideFrame = 6, UserStyle = 7, Alternate = 8;

    public bool IsNull => (Style & 0xF) == Null;

    public static GdiPen Black { get; } = new(0, 0, PdfColor.Black);

    public static GdiPen White { get; } = new(0, 0, PdfColor.White);

    public static GdiPen None { get; } = new(Null, 0, PdfColor.Black);
}

/// <summary>
/// A logical brush ([MS-WMF] BrushStyle 0 solid, 1 null, 2 hatched, 3 and 5/6 bitmap patterns) with its color, hatch
/// style and, for patterns, the bitmap's pixels; a monochrome pattern keeps its bits, drawn in the text and background
/// colors of the moment.
/// </summary>
internal sealed record GdiBrush(int Style, PdfColor Color, int Hatch = 0, PatternBrush? Pattern = null, MonoBits? Mono = null)
{
    public const int Solid = 0, Null = 1, Hatched = 2;

    public bool IsNull => Style == Null || (Style is 3 or 5 or 6 && Pattern is null && Mono is null);

    public static GdiBrush White { get; } = new(Solid, PdfColor.White);

    public static GdiBrush Black { get; } = new(Solid, PdfColor.Black);

    public static GdiBrush None { get; } = new(Null, PdfColor.White);

    public static GdiBrush Gray(double level) => new(Solid, new PdfColor(level, level, level));
}

/// <summary>A logical font ([MS-WMF] Font): height in logical units (negative for the em height), escapement in tenths of a degree.</summary>
internal sealed record GdiFont(string Face, double Height, double Width, double Escapement, int Weight, bool Italic, bool Underline, bool StrikeOut, int Charset)
{
    public const int SymbolCharset = 2;

    public static GdiFont Default { get; } = new("Arial", 0, 0, 0, 400, false, false, false, 0);

    public bool Bold => Weight >= 600;

    /// <summary>The code page of 8-bit text in the font's character set.</summary>
    public Encoding Encoding => Charset switch
    {
        128 => Get(932),
        129 => Get(949),
        130 => Get(1361),
        134 => Get(936),
        136 => Get(950),
        161 => Get(1253),
        162 => Get(1254),
        163 => Get(1258),
        177 => Get(1255),
        178 => Get(1256),
        186 => Get(1257),
        204 => Get(1251),
        222 => Get(874),
        238 => Get(1250),
        77 => Get(10000),
        255 => Get(437),
        _ => Get(1252),
    };

    private static Encoding Get(int codePage) => CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.Latin1;
}

/// <summary>The pixels of a monochrome pattern: which are 1 (drawn in the background color; 0 is the text color).</summary>
internal sealed record MonoBits(int Width, int Height, bool[] Ones)
{
    /// <summary>The bits of a pattern decoded with black for 0 and white for 1.</summary>
    public static MonoBits Of(PatternBrush pattern)
    {
        var ones = new bool[pattern.Width * pattern.Height];
        for (var i = 0; i < ones.Length; i++)
        {
            ones[i] = pattern.Rgb[i * 3] > 127;
        }

        return new MonoBits(pattern.Width, pattern.Height, ones);
    }

    /// <summary>The pattern in two colors.</summary>
    public PatternBrush Colorize(PdfColor zero, PdfColor one)
    {
        var rgb = new byte[Ones.Length * 3];
        var count = 0;
        for (var i = 0; i < Ones.Length; i++)
        {
            var c = Ones[i] ? one : zero;
            (rgb[i * 3], rgb[(i * 3) + 1], rgb[(i * 3) + 2]) = ((byte)Math.Round(c.Red * 255), (byte)Math.Round(c.Green * 255), (byte)Math.Round(c.Blue * 255));
            count += Ones[i] ? 1 : 0;
        }

        var t = Ones.Length == 0 ? 0 : count / (double)Ones.Length;
        var average = new PdfColor(zero.Red + ((one.Red - zero.Red) * t), zero.Green + ((one.Green - zero.Green) * t), zero.Blue + ((one.Blue - zero.Blue) * t));
        return new PatternBrush(Width, Height, rgb, 1, average);
    }
}

/// <summary>A logical palette's colors.</summary>
internal sealed record GdiPalette(PdfColor[] Colors);

/// <summary>A region object (in form space).</summary>
internal sealed record GdiRegion(ClipRegion? Region);

/// <summary>Text in symbol fonts: the Symbol font's characters as Unicode.</summary>
internal static class SymbolFont
{
    /// <summary>Unicode for the Symbol font's codes 0x20 to 0xFF; 0 where there is none.</summary>
    private static readonly ushort[] Codes =
    [
        0x0020, 0x0021, 0x2200, 0x0023, 0x2203, 0x0025, 0x0026, 0x220B, 0x0028, 0x0029, 0x2217, 0x002B, 0x002C, 0x2212, 0x002E, 0x002F,
        0x0030, 0x0031, 0x0032, 0x0033, 0x0034, 0x0035, 0x0036, 0x0037, 0x0038, 0x0039, 0x003A, 0x003B, 0x003C, 0x003D, 0x003E, 0x003F,
        0x2245, 0x0391, 0x0392, 0x03A7, 0x0394, 0x0395, 0x03A6, 0x0393, 0x0397, 0x0399, 0x03D1, 0x039A, 0x039B, 0x039C, 0x039D, 0x039F,
        0x03A0, 0x0398, 0x03A1, 0x03A3, 0x03A4, 0x03A5, 0x03C2, 0x03A9, 0x039E, 0x03A8, 0x0396, 0x005B, 0x2234, 0x005D, 0x22A5, 0x005F,
        0x203E, 0x03B1, 0x03B2, 0x03C7, 0x03B4, 0x03B5, 0x03C6, 0x03B3, 0x03B7, 0x03B9, 0x03D5, 0x03BA, 0x03BB, 0x03BC, 0x03BD, 0x03BF,
        0x03C0, 0x03B8, 0x03C1, 0x03C3, 0x03C4, 0x03C5, 0x03D6, 0x03C9, 0x03BE, 0x03C8, 0x03B6, 0x007B, 0x007C, 0x007D, 0x223C, 0x0000,
        0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
        0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
        0x20AC, 0x03D2, 0x2032, 0x2264, 0x2044, 0x221E, 0x0192, 0x2663, 0x2666, 0x2665, 0x2660, 0x2194, 0x2190, 0x2191, 0x2192, 0x2193,
        0x00B0, 0x00B1, 0x2033, 0x2265, 0x00D7, 0x221D, 0x2202, 0x2022, 0x00F7, 0x2260, 0x2261, 0x2248, 0x2026, 0x23D0, 0x23AF, 0x21B5,
        0x2135, 0x2111, 0x211C, 0x2118, 0x2297, 0x2295, 0x2205, 0x2229, 0x222A, 0x2283, 0x2287, 0x2284, 0x2282, 0x2286, 0x2208, 0x2209,
        0x2220, 0x2207, 0x00AE, 0x00A9, 0x2122, 0x220F, 0x221A, 0x22C5, 0x00AC, 0x2227, 0x2228, 0x21D4, 0x21D0, 0x21D1, 0x21D2, 0x21D3,
        0x25CA, 0x2329, 0x00AE, 0x00A9, 0x2122, 0x2211, 0x239B, 0x239C, 0x239D, 0x23A1, 0x23A2, 0x23A3, 0x23A7, 0x23A8, 0x23A9, 0x23AA,
        0x0000, 0x232A, 0x222B, 0x2320, 0x23AE, 0x2321, 0x239E, 0x239F, 0x23A0, 0x23A4, 0x23A5, 0x23A6, 0x23AB, 0x23AC, 0x23AD, 0x0000,
    ];

    /// <summary>
    /// The text of a symbol font as Unicode: the Symbol font's codes mapped to the characters they show; other symbol
    /// fonts (Wingdings and the like) keep their private-use code points (U+F020 to U+F0FF), which a font with the
    /// same name draws.
    /// </summary>
    public static string Map(string text, string face)
    {
        var symbol = face.Equals("Symbol", StringComparison.OrdinalIgnoreCase);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var code = c is >= (char)0xF020 and <= (char)0xF0FF ? c - 0xF000 : c;
            if (code is < 0x20 or > 0xFF)
            {
                builder.Append(c);
            }
            else if (symbol && Codes[code - 0x20] != 0)
            {
                builder.Append((char)Codes[code - 0x20]);
            }
            else
            {
                builder.Append((char)(0xF000 + code));
            }
        }

        return builder.ToString();
    }
}

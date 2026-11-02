using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Layout;

/// <summary>How tall each line is (CSS <c>line-height</c>).</summary>
public readonly record struct LineHeight
{
    private LineHeight(LineHeightKind kind, double value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>The font's own line spacing: ascent + descent + line gap (CSS <c>normal</c>).</summary>
    public static LineHeight Normal { get; } = new(LineHeightKind.Normal, 0);

    /// <summary>What kind of line height this is.</summary>
    public LineHeightKind Kind { get; }

    /// <summary>The multiplier or the height in points, depending on <see cref="Kind"/>.</summary>
    public double Value { get; }

    /// <summary>A multiple of the font size, e.g. 1.5 (CSS <c>line-height: 1.5</c>).</summary>
    public static LineHeight Multiple(double factor) =>
        factor >= 0 ? new(LineHeightKind.Multiple, factor) : throw new ArgumentOutOfRangeException(nameof(factor));

    /// <summary>A fixed height in points (CSS <c>line-height: 18pt</c>).</summary>
    public static LineHeight Points(double points) =>
        points >= 0 ? new(LineHeightKind.Points, points) : throw new ArgumentOutOfRangeException(nameof(points));

    /// <summary>The line height in points for a font at a size.</summary>
    public double Resolve(PdfFont font, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(font);
        return Kind switch
        {
            LineHeightKind.Multiple => Value * fontSize,
            LineHeightKind.Points => Value,
            _ => font.GetLineHeight(fontSize),
        };
    }
}

/// <summary>Kinds of <see cref="LineHeight"/>.</summary>
public enum LineHeightKind
{
    /// <summary>The font's own spacing.</summary>
    Normal,

    /// <summary>A multiple of the font size.</summary>
    Multiple,

    /// <summary>A fixed number of points.</summary>
    Points,
}

/// <summary>Horizontal alignment of lines in a paragraph.</summary>
public enum TextAlignment
{
    /// <summary>Lines start at the left edge.</summary>
    Left,

    /// <summary>Lines end at the right edge.</summary>
    Right,

    /// <summary>Lines are centered.</summary>
    Center,

    /// <summary>Lines fill the width by widening spaces, except the last line and lines ending in a hard break.</summary>
    Justify,
}

/// <summary>The appearance of a run of text. Immutable; use <c>with</c> to derive variations.</summary>
public sealed record TextStyle
{
    /// <summary>Creates a style with a font fallback list (like CSS <c>font-family</c>) and a size in points.</summary>
    public TextStyle(IReadOnlyList<PdfFont> fonts, double fontSize = 12)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        if (fonts.Count == 0)
        {
            throw new ArgumentException("At least one font is required.", nameof(fonts));
        }

        if (fontSize <= 0 || double.IsNaN(fontSize))
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize), fontSize, "The font size must be positive.");
        }

        Fonts = fonts;
        FontSize = fontSize;
    }

    /// <summary>Creates a style with a single font.</summary>
    public TextStyle(PdfFont font, double fontSize = 12)
        : this([font ?? throw new ArgumentNullException(nameof(font))], fontSize)
    {
    }

    /// <summary>Fonts to try for each character, in order.</summary>
    public IReadOnlyList<PdfFont> Fonts { get; init; }

    /// <summary>Font size in points.</summary>
    public double FontSize { get; init; }

    /// <summary>Text color.</summary>
    public PdfColor Color { get; init; } = PdfColor.Black;

    /// <summary>Line height.</summary>
    public LineHeight LineHeight { get; init; } = LineHeight.Normal;

    /// <summary>OpenType features and language for shaping.</summary>
    public ShapingOptions Shaping { get; init; } = ShapingOptions.Default;
}

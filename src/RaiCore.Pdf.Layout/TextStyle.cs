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

    /// <summary>Vertical position of this text in its line (CSS <c>vertical-align</c>), e.g. raised for superscripts.</summary>
    public InlineAlignment VerticalAlign { get; init; }

    /// <summary>
    /// Lines drawn with this text (CSS <c>text-decoration</c>): the decorations of every element the text is in.
    /// Adjacent text sharing a decoration gets one continuous line.
    /// </summary>
    public IReadOnlyList<TextDecoration> Decorations { get; init; } = [];
}

/// <summary>Which lines a <see cref="TextDecoration"/> draws (CSS <c>text-decoration-line</c>).</summary>
[Flags]
public enum TextDecorationLines
{
    /// <summary>No line.</summary>
    None = 0,

    /// <summary>Below the baseline.</summary>
    Underline = 1,

    /// <summary>Above the text.</summary>
    Overline = 2,

    /// <summary>Through the middle of the lowercase letters.</summary>
    LineThrough = 4,
}

/// <summary>How a <see cref="TextDecoration"/> line is drawn (CSS <c>text-decoration-style</c>).</summary>
public enum TextDecorationStyle
{
    /// <summary>A single solid line.</summary>
    Solid,

    /// <summary>Two parallel lines.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "The CSS keyword.")]
    Double,

    /// <summary>Round dots.</summary>
    Dotted,

    /// <summary>Dashes.</summary>
    Dashed,

    /// <summary>A wavy line.</summary>
    Wavy,
}

/// <summary>
/// Lines drawn under, over or through text (CSS <c>text-decoration</c>). As in CSS, the position and thickness come
/// from the font of the element that declared the decoration (<see cref="Font"/>, <see cref="FontSize"/>), so one
/// straight line runs under text of mixed sizes.
/// </summary>
/// <param name="Lines">Which lines to draw.</param>
/// <param name="Color">Their color.</param>
public sealed record TextDecoration(TextDecorationLines Lines, PdfColor Color)
{
    /// <summary>How the lines are drawn.</summary>
    public TextDecorationStyle Style { get; init; }

    /// <summary>Line thickness in points, or <c>null</c> for the font's (CSS <c>text-decoration-thickness: auto</c>).</summary>
    public double? Thickness { get; init; }

    /// <summary>
    /// Distance from the baseline down to the underline in points, or <c>null</c> for the font's
    /// (CSS <c>text-underline-offset</c>).
    /// </summary>
    public double? UnderlineOffset { get; init; }

    /// <summary>The font whose metrics place the lines, or <c>null</c> for the decorated text's own.</summary>
    public PdfFont? Font { get; init; }

    /// <summary>The font size for those metrics, or 0 for the decorated text's own.</summary>
    public double FontSize { get; init; }

    /// <summary>
    /// How far the declaring element's baseline is above the line's (its <c>vertical-align</c> shift): lines stay at that
    /// baseline even under raised or lowered descendants.
    /// </summary>
    public double BaselineShift { get; init; }
}

/// <summary>What an inline item is aligned to in its line (the kinds of CSS <c>vertical-align</c>).</summary>
public enum InlineAlignmentKind
{
    /// <summary>Its baseline on the line's baseline, raised by <see cref="InlineAlignment.Shift"/>.</summary>
    Baseline,

    /// <summary>Its middle at the line's baseline plus half the paragraph font's x-height.</summary>
    Middle,

    /// <summary>Its top at the top of the paragraph font (its ascent above the baseline).</summary>
    TextTop,

    /// <summary>Its bottom at the bottom of the paragraph font (its descent below the baseline).</summary>
    TextBottom,

    /// <summary>Its top at the top of the line box.</summary>
    Top,

    /// <summary>Its bottom at the bottom of the line box.</summary>
    Bottom,
}

/// <summary>
/// The vertical position of an inline item (text, image or box) in its line, like CSS <c>vertical-align</c>.
/// The default is the baseline. Sub- and superscripts, lengths and percentages are baselines raised or lowered by a
/// <see cref="Shift"/>; the other kinds align the item's edges or middle and add the shift.
/// </summary>
/// <param name="Kind">What the item is aligned to.</param>
/// <param name="Shift">Points to raise the item by (negative lowers it).</param>
public readonly record struct InlineAlignment(InlineAlignmentKind Kind, double Shift = 0)
{
    /// <summary>On the baseline (CSS <c>vertical-align: baseline</c>).</summary>
    public static InlineAlignment Baseline { get; }

    /// <summary>The baseline raised by <paramref name="points"/> (lowered when negative), e.g. for <c>sub</c>/<c>super</c>.</summary>
    public static InlineAlignment Raised(double points) => new(InlineAlignmentKind.Baseline, points);
}

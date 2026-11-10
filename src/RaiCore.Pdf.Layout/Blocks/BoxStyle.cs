namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>Widths of the four sides of a box, in points (CSS order: top, right, bottom, left).</summary>
public readonly record struct Thickness(double Top, double Right, double Bottom, double Left)
{
    /// <summary>No thickness.</summary>
    public static Thickness Zero { get; }

    /// <summary>The same value on all four sides.</summary>
    public static Thickness All(double value) => new(value, value, value, value);

    /// <summary>One value for top and bottom, another for left and right.</summary>
    public static Thickness Symmetric(double vertical, double horizontal) => new(vertical, horizontal, vertical, horizontal);

    /// <summary>Left + right.</summary>
    public double Horizontal => Left + Right;

    /// <summary>Top + bottom.</summary>
    public double Vertical => Top + Bottom;
}

/// <summary>How a border side is drawn (CSS <c>border-style</c>). CSS <c>none</c> is a side of width 0.</summary>
public enum BorderStyle
{
    /// <summary>A single solid line.</summary>
    Solid,

    /// <summary>Dashes about three times as long as the line is wide, spaced so the side starts and ends with a dash.</summary>
    Dashed,

    /// <summary>Dots about a dot apart: round, or square for lines thinner than 3 px (2.25 pt), as browsers draw them.</summary>
    Dotted,

    /// <summary>Two lines with a gap between, each about a third of the width. Widths below 3 px (2.25 pt) draw solid.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "The CSS keyword.")]
    Double,

    /// <summary>Carved into the page: the outer half shaded like <see cref="Inset"/>, the inner half like <see cref="Outset"/>.</summary>
    Groove,

    /// <summary>Raised from the page: the outer half shaded like <see cref="Outset"/>, the inner half like <see cref="Inset"/>.</summary>
    Ridge,

    /// <summary>The box looks embedded: top and left sides darker, bottom and right sides lighter.</summary>
    Inset,

    /// <summary>The box looks raised: top and left sides lighter, bottom and right sides darker.</summary>
    Outset,

    /// <summary>
    /// No border, like a side of width 0 (give it width 0, as CSS computes it). With collapsed table borders it also
    /// suppresses every other border at that position (CSS 2.1 section 17.6.2.1).
    /// </summary>
    Hidden,
}

/// <summary>One side of a border: a line of a width, color and style (solid unless <see cref="Style"/> says otherwise).</summary>
public readonly record struct BorderSide(double Width, PdfColor Color)
{
    /// <summary>A side of a width, color and style.</summary>
    public BorderSide(double width, PdfColor color, BorderStyle style)
        : this(width, color) => Style = style;

    /// <summary>No border.</summary>
    public static BorderSide None { get; }

    /// <summary>How the side is drawn.</summary>
    public BorderStyle Style { get; init; }
}

/// <summary>The four sides of a border.</summary>
public readonly record struct Borders(BorderSide Top, BorderSide Right, BorderSide Bottom, BorderSide Left)
{
    /// <summary>No border.</summary>
    public static Borders None { get; }

    /// <summary>The same border on all four sides.</summary>
    public static Borders All(double width, PdfColor color) => All(width, color, BorderStyle.Solid);

    /// <summary>The same border, in a style, on all four sides.</summary>
    public static Borders All(double width, PdfColor color, BorderStyle style) =>
        new(new(width, color, style), new(width, color, style), new(width, color, style), new(width, color, style));

    /// <summary>The border widths as a <see cref="Thickness"/>.</summary>
    public Thickness Widths => new(Top.Width, Right.Width, Bottom.Width, Left.Width);
}

/// <summary>The area a box's background color covers (CSS <c>background-clip</c>).</summary>
public enum BackgroundClip
{
    /// <summary>The border box: behind the border and the padding (the default).</summary>
    BorderBox,

    /// <summary>The padding box: inside the border.</summary>
    PaddingBox,

    /// <summary>The content box: inside the padding.</summary>
    ContentBox,
}

/// <summary>Page break behavior before or after a block (CSS <c>break-before</c>/<c>break-after</c>).</summary>
public enum PageBreak
{
    /// <summary>Break only if needed.</summary>
    Auto,

    /// <summary>Always start a new page.</summary>
    Page,

    /// <summary>Avoid a break here, e.g. to keep a heading with the paragraph that follows.</summary>
    Avoid,
}

/// <summary>Page break behavior inside a block (CSS <c>break-inside</c>).</summary>
public enum PageBreakInside
{
    /// <summary>The block may be split across pages.</summary>
    Auto,

    /// <summary>Keep the block on one page if it fits on one.</summary>
    Avoid,
}

/// <summary>
/// Box properties of a block, following the CSS box model: margin outside the border, padding inside it,
/// and a background behind the border and padding. Immutable; use <c>with</c> to derive variations.
/// </summary>
public sealed record BoxStyle
{
    /// <summary>The default: no margin, border, padding or background.</summary>
    public static BoxStyle Default { get; } = new();

    /// <summary>Space outside the border. Vertical margins of adjacent blocks collapse into the larger one, as in CSS.</summary>
    public Thickness Margin { get; init; }

    /// <summary>Space between the border and the content.</summary>
    public Thickness Padding { get; init; }

    /// <summary>Border lines.</summary>
    public Borders Border { get; init; }

    /// <summary>Background color behind the padding and border, or <c>null</c> for none.</summary>
    public PdfColor? Background { get; init; }

    /// <summary>
    /// The area the background covers: the border box (the default), the padding box or the content box, with the
    /// rounded corners of <see cref="BorderRadius"/> reduced accordingly.
    /// </summary>
    public BackgroundClip BackgroundClip { get; init; }

    /// <summary>
    /// Rounded corners of the border box (CSS <c>border-radius</c>): the background and the border follow them, and an
    /// image block's image is clipped to its content box with the radii reduced by the border and padding. They do not
    /// clip a container's children. Ignored by tables with collapsed borders, as in CSS. A box split across pages has
    /// its top corners rounded on the first page and its bottom corners on the last.
    /// </summary>
    public CornerRadii BorderRadius { get; init; }

    /// <summary>Width of the border box in points, or <c>null</c> to fill the available width.</summary>
    public double? Width { get; init; }

    /// <summary>
    /// Minimum border-box width in points. <c>null</c> is CSS <c>min-width: auto</c>: no minimum, except for flex
    /// items in a row, which do not shrink below their min-content width (e.g. their longest word). Applies to fixed,
    /// filling and shrink-to-fit widths alike, and wins over <see cref="MaxWidth"/> (as in CSS).
    /// </summary>
    public double? MinWidth { get; init; }

    /// <summary>
    /// Maximum border-box width in points, or <c>null</c> for none. A block without a <see cref="Width"/> fills the
    /// available width up to this; images scale to it (keeping their aspect ratio unless given both a width and a
    /// height) and tables shrink to fit within it, but a table without a width never gets narrower than its content's
    /// minimum width.
    /// </summary>
    public double? MaxWidth { get; init; }

    /// <summary>
    /// Height of the border box in points, or <c>null</c> for the content's height. Content that does not fit
    /// overflows (it is drawn, and what follows is placed after the box), as with CSS <c>overflow: visible</c>.
    /// A box with a fixed height moves to the next page as a whole when it fits on one. For tables it is a minimum,
    /// and images ignore it (they have their own size).
    /// </summary>
    public double? Height { get; init; }

    /// <summary>
    /// Minimum border-box height in points. <c>null</c> is CSS <c>min-height: auto</c>: no minimum, except for flex
    /// items in a column, which do not shrink below their content.
    /// </summary>
    public double? MinHeight { get; init; }

    /// <summary>Maximum border-box height in points (taller content overflows), or <c>null</c> for none.</summary>
    public double? MaxHeight { get; init; }

    /// <summary>Page break before the block.</summary>
    public PageBreak BreakBefore { get; init; }

    /// <summary>Page break after the block.</summary>
    public PageBreak BreakAfter { get; init; }

    /// <summary>Page breaks inside the block.</summary>
    public PageBreakInside BreakInside { get; init; }

    /// <summary>Minimum number of a paragraph's lines left at the bottom of a page (CSS <c>orphans</c>).</summary>
    public int Orphans { get; init; } = 2;

    /// <summary>Minimum number of a paragraph's lines carried to the top of the next page (CSS <c>widows</c>).</summary>
    public int Widows { get; init; } = 2;

    /// <summary>
    /// The opacity of the whole block, from 0 (invisible, but still taking space) to 1 (opaque, the default), as CSS
    /// <c>opacity</c>: below 1, everything the block paints (background, borders, content and descendants) is drawn
    /// as one transparency group, then composited with this opacity. A block split across pages gets a group per page.
    /// </summary>
    public double Opacity { get; init; } = 1;

    /// <summary>A border-box width limited by <see cref="MaxWidth"/> and then <see cref="MinWidth"/>.</summary>
    internal double ClampWidth(double width) =>
        Math.Max(Math.Min(width, MaxWidth ?? double.PositiveInfinity), MinWidth ?? 0);
}

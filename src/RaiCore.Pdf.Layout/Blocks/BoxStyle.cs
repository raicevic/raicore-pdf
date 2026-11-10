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

/// <summary>One side of a border: a solid line of a width and color.</summary>
public readonly record struct BorderSide(double Width, PdfColor Color)
{
    /// <summary>No border.</summary>
    public static BorderSide None { get; }
}

/// <summary>The four sides of a border.</summary>
public readonly record struct Borders(BorderSide Top, BorderSide Right, BorderSide Bottom, BorderSide Left)
{
    /// <summary>No border.</summary>
    public static Borders None { get; }

    /// <summary>The same border on all four sides.</summary>
    public static Borders All(double width, PdfColor color) =>
        new(new(width, color), new(width, color), new(width, color), new(width, color));

    /// <summary>The border widths as a <see cref="Thickness"/>.</summary>
    public Thickness Widths => new(Top.Width, Right.Width, Bottom.Width, Left.Width);
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

    /// <summary>Width of the border box in points, or <c>null</c> to fill the available width.</summary>
    public double? Width { get; init; }

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
}

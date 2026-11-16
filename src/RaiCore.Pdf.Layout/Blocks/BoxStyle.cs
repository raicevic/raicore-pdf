namespace RaiCore.Pdf.Layout;

/// <summary>
/// The four sides of a box (CSS order: top, right, bottom, left) as <see cref="Length"/>s. Numbers convert to points,
/// so <c>new Thickness(10, 20, 10, 20)</c> works. A percentage in a <see cref="BoxStyle.Margin"/> or
/// <see cref="BoxStyle.Padding"/> is of the containing block's width, on all four sides (as in CSS); in
/// <see cref="PageSetup.Margins"/> it is of the page's width (left and right) or height (top and bottom).
/// </summary>
public readonly record struct Thickness(Length Top, Length Right, Length Bottom, Length Left)
{
    /// <summary>No thickness.</summary>
    public static Thickness Zero { get; }

    /// <summary>The same value on all four sides.</summary>
    public static Thickness All(Length value) => new(value, value, value, value);

    /// <summary>One value for top and bottom, another for left and right.</summary>
    public static Thickness Symmetric(Length vertical, Length horizontal) => new(vertical, horizontal, vertical, horizontal);

    /// <summary>Left + right.</summary>
    public Length Horizontal => Left + Right;

    /// <summary>Top + bottom.</summary>
    public Length Vertical => Top + Bottom;

    /// <summary>Whether a side has a percentage part.</summary>
    public bool HasPercent => Top.HasPercent || Right.HasPercent || Bottom.HasPercent || Left.HasPercent;

    /// <summary>The sides in points, with percentages of <paramref name="reference"/>.</summary>
    public Thickness Resolve(double reference) =>
        new(Top.Resolve(reference), Right.Resolve(reference), Bottom.Resolve(reference), Left.Resolve(reference));

    /// <summary>The sides without their percentages (as for intrinsic sizes).</summary>
    internal Thickness PointsOnly => new(Top.Points, Right.Points, Bottom.Points, Left.Points);

    /// <summary>The sides in points, for a thickness known to have no percentage (see <see cref="Length.Fixed"/>).</summary>
    internal Sides Fixed => new(Top.Fixed, Right.Fixed, Bottom.Fixed, Left.Fixed);
}

/// <summary>The four sides of a box in points: a <see cref="Thickness"/> without percentages, as the layout uses it.</summary>
internal readonly record struct Sides(double Top, double Right, double Bottom, double Left)
{
    /// <summary>Left + right.</summary>
    public double Horizontal => Left + Right;

    /// <summary>Top + bottom.</summary>
    public double Vertical => Top + Bottom;

    public static implicit operator Thickness(Sides sides) => new(sides.Top, sides.Right, sides.Bottom, sides.Left);
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

    /// <summary>
    /// Start a new page that is a left page, inserting a blank page if needed (CSS <c>left</c>; <c>verso</c> in
    /// left-to-right documents). Before the first content of the document, it makes the first page a left page.
    /// </summary>
    Left,

    /// <summary>Start a new page that is a right page, inserting a blank page if needed (CSS <c>right</c>; <c>recto</c>).</summary>
    Right,
}

/// <summary>Which side a block floats to (CSS <c>float</c>).</summary>
public enum FloatSide
{
    /// <summary>Not floated: the block is in the normal flow.</summary>
    None,

    /// <summary>Floated to the left: following text flows on its right.</summary>
    Left,

    /// <summary>Floated to the right: following text flows on its left.</summary>
    Right,
}

/// <summary>Which earlier floats a block is moved below (CSS <c>clear</c>).</summary>
public enum ClearSide
{
    /// <summary>None: the block may sit beside floats.</summary>
    None,

    /// <summary>Below left floats.</summary>
    Left,

    /// <summary>Below right floats.</summary>
    Right,

    /// <summary>Below all floats.</summary>
    Both,
}

/// <summary>How a block is positioned (CSS <c>position</c>).</summary>
public enum Positioning
{
    /// <summary>In the normal flow (the default).</summary>
    Static,

    /// <summary>In the flow, then drawn moved by its <see cref="BoxStyle.Insets"/>; it positions its absolute descendants.</summary>
    Relative,

    /// <summary>
    /// Out of the flow, placed by its <see cref="BoxStyle.Insets"/> in its containing block: the padding box of the
    /// nearest positioned ancestor, or else the page area of the page it is on.
    /// </summary>
    Absolute,

    /// <summary>Like <see cref="Absolute"/> in the page area, and repeated on every page (as browsers print it).</summary>
    Fixed,
}

/// <summary>The offsets of a positioned block from its containing block's edges (CSS <c>top</c>, <c>right</c>, ...); <c>null</c> is auto.</summary>
/// <remarks>Percentages are of the containing block's width (left, right) or height (top, bottom).</remarks>
public readonly record struct BoxInsets(Length? Top = null, Length? Right = null, Length? Bottom = null, Length? Left = null);

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

    /// <summary>
    /// Space outside the border. Vertical margins of adjacent blocks collapse into the larger one, as in CSS.
    /// Percentages are of the containing block's width, also for the top and bottom.
    /// </summary>
    public Thickness Margin { get; init; }

    /// <summary>Space between the border and the content. Percentages are of the containing block's width, also for the top and bottom.</summary>
    public Thickness Padding { get; init; }

    /// <summary>Border lines.</summary>
    public Borders Border { get; init; }

    /// <summary>Background color behind the padding and border, or <c>null</c> for none.</summary>
    public PdfColor? Background { get; init; }

    /// <summary>
    /// Background images and gradients over the <see cref="Background"/> color, the first on top (CSS
    /// <c>background-image</c> and its companions). Each is sized and positioned in its origin box and painted in its clip
    /// box; a box split across pages is painted as if whole, each fragment showing its slice.
    /// </summary>
    public IReadOnlyList<BackgroundLayer> BackgroundLayers { get; init; } = [];

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

    /// <summary>Width of the border box, or <c>null</c> to fill the available width.</summary>
    public Length? Width { get; init; }

    /// <summary>
    /// Minimum border-box width. <c>null</c> is CSS <c>min-width: auto</c>: no minimum, except for flex
    /// items in a row, which do not shrink below their min-content width (e.g. their longest word). Applies to fixed,
    /// filling and shrink-to-fit widths alike, and wins over <see cref="MaxWidth"/> (as in CSS).
    /// </summary>
    public Length? MinWidth { get; init; }

    /// <summary>
    /// Maximum border-box width, or <c>null</c> for none. A block without a <see cref="Width"/> fills the
    /// available width up to this; images scale to it (keeping their aspect ratio unless given both a width and a
    /// height) and tables shrink to fit within it, but a table without a width never gets narrower than its content's
    /// minimum width.
    /// </summary>
    public Length? MaxWidth { get; init; }

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

    /// <summary>
    /// Takes the block out of the flow and floats it to a side (CSS 2.1 section 9.5): it goes as high as it can at its
    /// place in the flow, as far to that side as other floats allow (moving down until it fits beside them), and
    /// following text in the same formatting context flows around it: lines beside it are shortened, while block boxes
    /// stay where they are (their text avoids the float). Tables, flex containers, images and blocks with
    /// <see cref="ContainsFloats"/> are narrowed to fit beside floats, or moved below them. Without a
    /// <see cref="Width"/> it shrinks to fit its content. Its margins do not collapse. A float that does not fit on the
    /// rest of the page moves to the next one together with what follows it.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "The CSS property's name.")]
    public FloatSide Float { get; init; }

    /// <summary>Moves the block below earlier floats of its formatting context on those sides (CSS <c>clear</c>).</summary>
    public ClearSide Clear { get; init; }

    /// <summary>
    /// The block's content is clipped to its padding box (CSS <c>overflow</c> other than <c>visible</c>: on paper
    /// <c>hidden</c>, <c>clip</c>, <c>auto</c> and <c>scroll</c> all clip), with the inner rounded corners. A box with a fixed
    /// <see cref="Height"/> that fits on the page is laid out as one piece, so content it hides does not start new pages.
    /// </summary>
    public bool ClipOverflow { get; init; }

    /// <summary>
    /// The block starts a new formatting context for floats (CSS <c>display: flow-root</c>, or <c>overflow</c> other than
    /// <c>visible</c>): it grows to contain the floats inside it, floats outside do not affect its content, and it is
    /// itself placed beside earlier floats like a table. Floats, flex items and table cells always do this.
    /// </summary>
    public bool ContainsFloats { get; init; }

    /// <summary>
    /// How the block is positioned (CSS <c>position</c>). A relative block is drawn moved by its <see cref="Insets"/>
    /// (<c>left</c> before <c>right</c>, <c>top</c> before <c>bottom</c>; percentages of the containing width, and none
    /// vertically), leaving its place in the flow. An absolute block leaves the flow: without a <see cref="Width"/> it
    /// fills the room between a left and a right inset, or shrinks to fit; between a top and a bottom inset it gets that
    /// height. An auto inset keeps it where it would have been in the flow. It is laid out as one piece, on the page its
    /// containing block starts on (a top inset) or ends on (a bottom inset only), and does not break across pages.
    /// </summary>
    public Positioning Position { get; init; }

    /// <summary>The offsets of a positioned block (CSS <c>top</c>, <c>right</c>, <c>bottom</c>, <c>left</c>).</summary>
    public BoxInsets Insets { get; init; }

    /// <summary>
    /// The painting order of a positioned block (CSS <c>z-index</c>); <c>null</c> is auto. Positioned blocks are painted
    /// over the flow and floats, a higher z-index over a lower one, document order breaking ties; a negative z-index
    /// goes under the flow. One stacking order per page: z-indexes are not nested.
    /// </summary>
    public int? ZIndex { get; init; }

    /// <summary>
    /// Whether a width, margin or padding has a percentage (of the containing block's width), which the layout resolves
    /// against the width the containing block actually gets: a flex item after flexing, a table column, a shrink-to-fit
    /// float or inline box, a box narrowed by its own <see cref="MaxWidth"/>.
    /// </summary>
    /// <remarks>
    /// For min-content and max-content sizes (the shrink-to-fit width of a float, a table column's width, a flex item's
    /// content size), a width or maximum width with a percentage counts as auto and a minimum width as 0, and margins and
    /// padding count without their percentages, as CSS Sizing 3 section 5.2.1 specifies.
    /// </remarks>
    public bool HasPercent =>
        Width is { HasPercent: true } || MinWidth is { HasPercent: true } || MaxWidth is { HasPercent: true }
        || Margin.HasPercent || Padding.HasPercent;

    /// <summary>
    /// The style with its percentages resolved against a containing block's width: the sizes of the box laid out in
    /// it. A border-box width is at least the horizontal padding and border.
    /// </summary>
    internal BoxStyle Resolve(double containingWidth)
    {
        if (!HasPercent)
        {
            return this;
        }

        // Padding cannot be negative, nor can a border box be narrower than its padding and border.
        var resolved = Padding.Resolve(containingWidth);
        var padding = new Thickness(
            Math.Max(0, resolved.Top.Points), Math.Max(0, resolved.Right.Points), Math.Max(0, resolved.Bottom.Points), Math.Max(0, resolved.Left.Points));
        var decoration = padding.Horizontal.Points + Border.Widths.Horizontal.Fixed;
        Length? BorderBox(Length? length) => length is { } l ? Math.Max(l.Resolve(containingWidth), decoration) : null;
        return this with
        {
            Width = BorderBox(Width),
            MinWidth = BorderBox(MinWidth),
            MaxWidth = BorderBox(MaxWidth),
            Margin = Margin.Resolve(containingWidth),
            Padding = padding,
        };
    }

    /// <summary>
    /// The style for min-content and max-content sizes: widths with a percentage are auto (a minimum is 0), and margins
    /// and padding lose their percentages (CSS Sizing 3 section 5.2.1).
    /// </summary>
    internal BoxStyle Intrinsic()
    {
        if (!HasPercent)
        {
            return this;
        }

        var padding = Padding.PointsOnly;
        var decoration = padding.Horizontal.Points + Border.Widths.Horizontal.Fixed;
        Length? BorderBox(Length? length) => length is { } l ? l.HasPercent ? null : Math.Max(l.Points, decoration) : null;
        return this with
        {
            Width = BorderBox(Width),
            MinWidth = BorderBox(MinWidth),
            MaxWidth = BorderBox(MaxWidth),
            Margin = Margin.PointsOnly,
            Padding = padding,
        };
    }

    /// <summary>A border-box width limited by <see cref="MaxWidth"/> and then <see cref="MinWidth"/>.</summary>
    internal double ClampWidth(double width) =>
        Math.Max(Math.Min(width, MaxWidth?.Fixed ?? double.PositiveInfinity), MinWidth?.Fixed ?? 0);
}

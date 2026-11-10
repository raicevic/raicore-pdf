namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>The main axis of a <see cref="FlexBlock"/> (CSS <c>flex-direction</c>).</summary>
public enum FlexDirection
{
    /// <summary>Items left to right.</summary>
    Row,

    /// <summary>Items right to left.</summary>
    RowReverse,

    /// <summary>Items top to bottom.</summary>
    Column,

    /// <summary>Items in reverse order, top to bottom (the container's height is its content's, so this reverses the order).</summary>
    ColumnReverse,
}

/// <summary>Whether a row of flex items wraps onto more lines (CSS <c>flex-wrap</c>).</summary>
public enum FlexWrap
{
    /// <summary>All items on one line; they shrink (down to their minimum size) to fit.</summary>
    NoWrap,

    /// <summary>Items that do not fit move to the next line, below.</summary>
    Wrap,

    /// <summary>Items wrap, and lines stack from the bottom up (the first line is the lowest).</summary>
    WrapReverse,
}

/// <summary>How free space on the main axis is distributed (CSS <c>justify-content</c>).</summary>
public enum JustifyContent
{
    /// <summary>Items packed at the start.</summary>
    FlexStart,

    /// <summary>Items packed at the end.</summary>
    FlexEnd,

    /// <summary>Items centered.</summary>
    Center,

    /// <summary>The first item at the start, the last at the end, equal space between.</summary>
    SpaceBetween,

    /// <summary>Equal space around each item (half-size spaces at the edges).</summary>
    SpaceAround,

    /// <summary>Equal space between items and at the edges.</summary>
    SpaceEvenly,
}

/// <summary>How an item is placed on the cross axis of its line (CSS <c>align-items</c>/<c>align-self</c>).</summary>
public enum FlexAlignment
{
    /// <summary>The item fills the line's cross size (unless it has a fixed cross size).</summary>
    Stretch,

    /// <summary>At the cross start (top of the line in a row).</summary>
    FlexStart,

    /// <summary>At the cross end.</summary>
    FlexEnd,

    /// <summary>Centered.</summary>
    Center,

    /// <summary>First text baselines lined up (rows only; in columns it acts as <see cref="FlexStart"/>).</summary>
    Baseline,
}

/// <summary>Margins that are CSS <c>auto</c> on a flex item: they absorb free space, e.g. to push an item to the end.</summary>
[Flags]
public enum AutoMargins
{
    /// <summary>No auto margins.</summary>
    None = 0,

    /// <summary>Top margin.</summary>
    Top = 1,

    /// <summary>Right margin.</summary>
    Right = 2,

    /// <summary>Bottom margin.</summary>
    Bottom = 4,

    /// <summary>Left margin.</summary>
    Left = 8,
}

/// <summary>The initial main size of a flex item before free space is distributed (CSS <c>flex-basis</c>).</summary>
public readonly record struct FlexBasis
{
    private FlexBasis(FlexBasisKind kind, double value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>The item's width (for rows) or height (for columns) if set, otherwise its content size.</summary>
    public static FlexBasis Auto { get; } = new(FlexBasisKind.Auto, 0);

    /// <summary>The item's content size (its max-content width in a row).</summary>
    public static FlexBasis Content { get; } = new(FlexBasisKind.Content, 0);

    internal FlexBasisKind Kind { get; }

    internal double Value { get; }

    /// <summary>A border-box size in points.</summary>
    public static FlexBasis Points(double points) => new(FlexBasisKind.Points, Math.Max(0, points));

    /// <summary>A percentage (0-100) of the container's content width; in columns it acts as <see cref="Content"/>.</summary>
    public static FlexBasis Percent(double percent) => new(FlexBasisKind.Percent, Math.Max(0, percent));
}

internal enum FlexBasisKind
{
    Auto,
    Content,
    Points,
    Percent,
}

/// <summary>
/// A flex item: a block placed by its <see cref="FlexBlock"/>. The block's own <see cref="Block.Style"/> supplies its
/// margin, border, padding, background and width (<see cref="BoxStyle.Width"/>, used by <see cref="FlexBasis.Auto"/>);
/// this class adds how it grows, shrinks and aligns.
/// </summary>
public sealed class FlexItem
{
    /// <summary>Creates a flex item.</summary>
    public FlexItem(Block content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
    }

    /// <summary>The item's box and content.</summary>
    public Block Content { get; }

    /// <summary>Share of positive free space the item receives (CSS <c>flex-grow</c>).</summary>
    public double Grow { get; init; }

    /// <summary>How much the item shrinks when the line overflows, weighted by its base size (CSS <c>flex-shrink</c>).</summary>
    public double Shrink { get; init; } = 1;

    /// <summary>The initial main size.</summary>
    public FlexBasis Basis { get; init; } = FlexBasis.Auto;

    /// <summary>Overrides the container's <see cref="FlexBlock.AlignItems"/> for this item; <c>null</c> to inherit it.</summary>
    public FlexAlignment? AlignSelf { get; init; }

    /// <summary>Items are placed in ascending order (CSS <c>order</c>); equal values keep their original order.</summary>
    public int Order { get; init; }

    /// <summary>
    /// Minimum border-box width in points. <c>null</c> is CSS <c>min-width: auto</c>: a row item does not shrink below
    /// its min-content width (e.g. its longest word).
    /// </summary>
    public double? MinWidth { get; init; }

    /// <summary>Maximum border-box width in points, or <c>null</c> for none.</summary>
    public double? MaxWidth { get; init; }

    /// <summary>Border-box height in points, or <c>null</c> for the content's height (or the line's, when stretched).</summary>
    public double? Height { get; init; }

    /// <summary>Minimum border-box height in points.</summary>
    public double? MinHeight { get; init; }

    /// <summary>Maximum border-box height in points (limits stretching; taller content overflows).</summary>
    public double? MaxHeight { get; init; }

    /// <summary>Which of the content's margins are <c>auto</c> (their values in <see cref="BoxStyle.Margin"/> are ignored).</summary>
    public AutoMargins AutoMargins { get; init; }
}

/// <summary>
/// A flex container (CSS <c>display: flex</c>): lays its items out in rows or columns, distributing free space with
/// <see cref="FlexItem.Grow"/> and <see cref="FlexItem.Shrink"/> and aligning them on both axes.
/// </summary>
/// <remarks>
/// The container's height is always its content's. Rows that do not fit on a page are split like the browsers do:
/// every item continues on the next page in its own column. Use <see cref="PageBreakInside.Avoid"/> on the container
/// or an item to keep it on one page.
/// </remarks>
/// <example>
/// <code>
/// var header = new FlexBlock { JustifyContent = JustifyContent.SpaceBetween, AlignItems = FlexAlignment.Center }
///     .Add(new FlexItem(new ImageBlock(logo, width: 120)))
///     .Add(new FlexItem(new ParagraphBlock(address)));
/// </code>
/// </example>
public sealed class FlexBlock : Block
{
    private readonly List<FlexItem> _items = [];

    /// <summary>Creates a flex container.</summary>
    public FlexBlock(BoxStyle? style = null)
        : base(style)
    {
    }

    /// <summary>Creates a flex container with items.</summary>
    public FlexBlock(BoxStyle? style, params IEnumerable<FlexItem> items)
        : base(style)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.AddRange(items);
    }

    /// <summary>The items in document order (see <see cref="FlexItem.Order"/>).</summary>
    public IReadOnlyList<FlexItem> Items => _items;

    /// <summary>The main axis.</summary>
    public FlexDirection Direction { get; init; }

    /// <summary>Line wrapping (rows only: the container's height is its content's, so columns never wrap).</summary>
    public FlexWrap Wrap { get; init; }

    /// <summary>Distribution of free space along the main axis (rows only, for the same reason).</summary>
    public JustifyContent JustifyContent { get; init; }

    /// <summary>Default cross-axis alignment of the items.</summary>
    public FlexAlignment AlignItems { get; init; } = FlexAlignment.Stretch;

    /// <summary>Space between lines of a row container, or between items of a column container (CSS <c>row-gap</c>).</summary>
    public double RowGap { get; init; }

    /// <summary>Space between items of a row (CSS <c>column-gap</c>).</summary>
    public double ColumnGap { get; init; }

    internal bool IsRow => Direction is FlexDirection.Row or FlexDirection.RowReverse;

    /// <summary>Appends an item.</summary>
    public FlexBlock Add(FlexItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
        return this;
    }

    /// <summary>Appends a block as an item with default flex properties.</summary>
    public FlexBlock Add(Block block) => Add(new FlexItem(block));

    /// <summary>The items sorted by <see cref="FlexItem.Order"/> (stable).</summary>
    internal List<FlexItem> OrderedItems() => [.. _items.Select((item, index) => (item, index)).OrderBy(p => p.item.Order).ThenBy(p => p.index).Select(p => p.item)];
}

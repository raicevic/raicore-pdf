namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>A width in points or as a percentage of the table's content width.</summary>
public readonly record struct TableWidth(double Value, bool IsPercent = false)
{
    /// <summary>A width in points.</summary>
    public static TableWidth Points(double points) => new(points);

    /// <summary>A percentage (0-100) of the table's content width.</summary>
    public static TableWidth Percent(double percent) => new(percent, IsPercent: true);

    internal double Resolve(double reference) => IsPercent ? Value / 100 * reference : Value;
}

/// <summary>How cell borders are drawn (CSS <c>border-collapse</c>).</summary>
public enum BorderCollapse
{
    /// <summary>Each cell has its own border, separated by <see cref="TableBlock.HorizontalSpacing"/>/<see cref="TableBlock.VerticalSpacing"/>.</summary>
    Separate,

    /// <summary>Adjacent borders are merged into one line; the widest border wins (CSS 2.1 section 17.6.2).</summary>
    Collapse,
}

/// <summary>How column widths are chosen (CSS <c>table-layout</c>).</summary>
public enum TableLayoutMode
{
    /// <summary>From the content: columns get between their minimum and preferred widths.</summary>
    Auto,

    /// <summary>From the column specifications and the first row only; the rest share the remaining width equally.</summary>
    Fixed,
}

/// <summary>Vertical position of a cell's content (CSS <c>vertical-align</c> on cells).</summary>
public enum VerticalAlignment
{
    /// <summary>At the top.</summary>
    Top,

    /// <summary>Centered.</summary>
    Middle,

    /// <summary>At the bottom.</summary>
    Bottom,
}

/// <summary>
/// A table: rows of cells laid out on a grid, with column widths computed from the content (CSS automatic table
/// layout) or fixed. Header rows repeat at the top of every page the table continues on. A row that does not fit on
/// the page is split, as browsers do: each cell's content continues on the next page (paragraph orphans and widows
/// still apply, so rows of a few lines move whole). Rows joined by row spans may also break between them; a cell
/// spanning rows then continues alongside the rows on the next page. Rows with <see cref="TableRow.BreakInside"/> set
/// to <see cref="PageBreakInside.Avoid"/> (and, for joined rows, all rows of the group) move to the next page whole.
/// </summary>
/// <remarks>
/// With <see cref="BorderCollapse.Collapse"/>, the table's own <see cref="BoxStyle.Border"/> takes part in border
/// collapsing at the table's edges instead of being drawn around it.
/// </remarks>
public sealed class TableBlock : Block
{
    private readonly List<TableRow> _rows = [];

    /// <summary>Creates a table. A <see cref="BoxStyle.Width"/> fixes its width; otherwise it shrinks to fit its content.</summary>
    public TableBlock(BoxStyle? style = null)
        : base(style)
    {
    }

    /// <summary>The rows, top to bottom.</summary>
    public IReadOnlyList<TableRow> Rows => _rows;

    /// <summary>Optional width specifications per column (CSS <c>col</c> widths); <c>null</c> entries are automatic.</summary>
    public IList<TableWidth?> ColumnWidths { get; } = [];

    /// <summary>Border model.</summary>
    public BorderCollapse BorderCollapse { get; init; }

    /// <summary>Horizontal space between cells and around them in the separate border model (CSS <c>border-spacing</c>).</summary>
    public double HorizontalSpacing { get; init; }

    /// <summary>Vertical space between cells and around them in the separate border model.</summary>
    public double VerticalSpacing { get; init; }

    /// <summary>Column width algorithm.</summary>
    public TableLayoutMode LayoutMode { get; init; }

    /// <summary>Position of a table narrower than the available width (e.g. CSS <c>margin: 0 auto</c> centers it).</summary>
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;

    /// <summary>Appends a row.</summary>
    public TableBlock Add(TableRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _rows.Add(row);
        return this;
    }
}

/// <summary>A table row.</summary>
public sealed class TableRow
{
    private readonly List<TableCell> _cells = [];

    /// <summary>Creates a row with cells.</summary>
    public TableRow(params IEnumerable<TableCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells.AddRange(cells);
    }

    /// <summary>The cells, left to right (cells spanning from rows above are not repeated).</summary>
    public IReadOnlyList<TableCell> Cells => _cells;

    /// <summary>A header row repeats at the top of every page the table continues on. Header rows must come first.</summary>
    public bool IsHeader { get; init; }

    /// <summary>Background behind the row's cells (a cell's own background is drawn over it).</summary>
    public PdfColor? Background { get; init; }

    /// <summary>Minimum row height in points.</summary>
    public double MinHeight { get; init; }

    /// <summary>
    /// <see cref="PageBreakInside.Avoid"/> keeps the row on one page when it fits on one (CSS <c>break-inside</c> on
    /// <c>tr</c>); a row taller than a page is still split.
    /// </summary>
    public PageBreakInside BreakInside { get; init; }

    /// <summary>Appends a cell.</summary>
    public TableRow Add(TableCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        _cells.Add(cell);
        return this;
    }
}

/// <summary>A table cell: blocks laid out inside the cell's border and padding.</summary>
public sealed class TableCell
{
    /// <summary>Creates a cell with content blocks.</summary>
    public TableCell(params IEnumerable<Block> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = [.. content];
    }

    /// <summary>Creates a cell containing one paragraph.</summary>
    public TableCell(Paragraph paragraph)
        : this(new ParagraphBlock(paragraph))
    {
    }

    /// <summary>The cell's content.</summary>
    public IReadOnlyList<Block> Content { get; }

    /// <summary>Padding, border and background of the cell (margins and break properties are ignored).</summary>
    public BoxStyle Style { get; init; } = BoxStyle.Default;

    /// <summary>Number of columns the cell spans.</summary>
    public int ColumnSpan { get; init; } = 1;

    /// <summary>Number of rows the cell spans.</summary>
    public int RowSpan { get; init; } = 1;

    /// <summary>Vertical position of the content.</summary>
    public VerticalAlignment VerticalAlignment { get; init; }

    /// <summary>Requested width (CSS <c>width</c> on a cell), or <c>null</c>.</summary>
    public TableWidth? Width { get; init; }
}

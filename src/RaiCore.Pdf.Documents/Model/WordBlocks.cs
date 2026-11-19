namespace RaiCore.Pdf.Documents.Model;

/// <summary>A block of a document: a <see cref="WordParagraph"/> or a <see cref="WordTable"/>.</summary>
internal abstract class WordBlock
{
}

/// <summary>Paragraph alignment.</summary>
internal enum WordAlignment
{
    /// <summary>To the start of the line (left in left-to-right text).</summary>
    Start,

    /// <summary>Centered.</summary>
    Center,

    /// <summary>To the end of the line.</summary>
    End,

    /// <summary>Justified: lines but the last fill the width.</summary>
    Justify,
}

/// <summary>How a paragraph's line spacing is measured.</summary>
internal enum LineSpacingRule
{
    /// <summary>A multiple of the font's line height (1 is single spacing).</summary>
    Multiple,

    /// <summary>Exactly the value, in points.</summary>
    Exact,

    /// <summary>At least the value, in points.</summary>
    AtLeast,
}

/// <summary>A line of a border.</summary>
/// <param name="Width">The width in points.</param>
/// <param name="Color">The color.</param>
/// <param name="Style">The style.</param>
internal readonly record struct BorderLine(double Width, PdfColor Color, BorderLineStyle Style = BorderLineStyle.Single)
{
    /// <summary>No line (it also removes a line inherited from a table).</summary>
    public static BorderLine None => new(0, PdfColor.Black, BorderLineStyle.None);

    /// <summary>Whether a line is drawn.</summary>
    public bool IsVisible => Style != BorderLineStyle.None && Width > 0;
}

/// <summary>Border line styles.</summary>
internal enum BorderLineStyle
{
    /// <summary>No line.</summary>
    None,

    /// <summary>A single solid line.</summary>
    Single,

    /// <summary>Two lines.</summary>
    Double,

    /// <summary>Dots.</summary>
    Dotted,

    /// <summary>Dashes.</summary>
    Dashed,
}

/// <summary>The four sides of a box, any of them unset (<c>null</c>).</summary>
internal readonly record struct BoxSides(BorderLine? Top, BorderLine? Right, BorderLine? Bottom, BorderLine? Left)
{
    /// <summary>Whether any side is set.</summary>
    public bool Any => Top is not null || Right is not null || Bottom is not null || Left is not null;
}

/// <summary>Tab stop alignment.</summary>
internal enum TabAlignment
{
    /// <summary>Text starts at the stop.</summary>
    Left,

    /// <summary>Text is centered on the stop.</summary>
    Center,

    /// <summary>Text ends at the stop.</summary>
    Right,

    /// <summary>The decimal separator is at the stop.</summary>
    Decimal,
}

/// <summary>A tab stop.</summary>
/// <param name="Position">Its distance from the start edge of the text area (the page margin or the cell padding).</param>
/// <param name="Alignment">How text aligns to it.</param>
/// <param name="Leader">The character the space before it is filled with, or <c>null</c>.</param>
internal readonly record struct TabStop(double Position, TabAlignment Alignment, char? Leader = null);

/// <summary>The formatting of a paragraph, resolved from its style and its own properties.</summary>
internal sealed record ParagraphFormat
{
    /// <summary>The default format.</summary>
    public static ParagraphFormat Default { get; } = new();

    /// <summary>The alignment.</summary>
    public WordAlignment Alignment { get; init; }

    /// <summary>Whether the paragraph is right-to-left.</summary>
    public bool RightToLeft { get; init; }

    /// <summary>The indent from the start edge.</summary>
    public double IndentStart { get; init; }

    /// <summary>The indent from the end edge.</summary>
    public double IndentEnd { get; init; }

    /// <summary>The extra indent of the first line (negative for a hanging indent).</summary>
    public double FirstLineIndent { get; init; }

    /// <summary>The space above the paragraph.</summary>
    public double SpaceBefore { get; init; }

    /// <summary>The space below the paragraph.</summary>
    public double SpaceAfter { get; init; }

    /// <summary>The line spacing: a multiple or points, as <see cref="LineRule"/> says.</summary>
    public double LineSpacing { get; init; } = 1;

    /// <summary>How <see cref="LineSpacing"/> is measured.</summary>
    public LineSpacingRule LineRule { get; init; }

    /// <summary>Whether the space above and below is left out between paragraphs of the same style.</summary>
    public bool ContextualSpacing { get; init; }

    /// <summary>Whether the paragraph stays on the page of the next one.</summary>
    public bool KeepWithNext { get; init; }

    /// <summary>Whether the paragraph's lines stay on one page.</summary>
    public bool KeepLines { get; init; }

    /// <summary>Whether the paragraph starts a new page.</summary>
    public bool PageBreakBefore { get; init; }

    /// <summary>Whether a page never starts or ends with a single line of the paragraph.</summary>
    public bool WidowControl { get; init; } = true;

    /// <summary>The outline level (0 for a top-level heading), or <c>null</c> for body text.</summary>
    public int? OutlineLevel { get; init; }

    /// <summary>The style's id, which <see cref="ContextualSpacing"/> compares.</summary>
    public string? StyleId { get; init; }

    /// <summary>The background color, or <c>null</c>.</summary>
    public PdfColor? Shading { get; init; }

    /// <summary>The borders.</summary>
    public BoxSides Borders { get; init; }

    /// <summary>The border between paragraphs with the same borders, or <c>null</c>.</summary>
    public BorderLine? BetweenBorder { get; init; }

    /// <summary>The tab stops, by position.</summary>
    public IReadOnlyList<TabStop> TabStops { get; init; } = [];
}

/// <summary>A paragraph: inline content with a format and, in a list, a label.</summary>
internal sealed class WordParagraph : WordBlock
{
    /// <summary>The format.</summary>
    public ParagraphFormat Format { get; set; } = ParagraphFormat.Default;

    /// <summary>The list label (number or bullet), or <c>null</c>.</summary>
    public ListLabel? Label { get; set; }

    /// <summary>The format of the paragraph mark: the text style of an empty paragraph's line.</summary>
    public RunFormat MarkFormat { get; set; } = RunFormat.Default;

    /// <summary>The content.</summary>
    public List<WordInline> Inlines { get; } = [];
}

/// <summary>What follows a list label.</summary>
internal enum LabelSuffix
{
    /// <summary>A tab (to the indent or the next tab stop).</summary>
    Tab,

    /// <summary>A space.</summary>
    Space,

    /// <summary>Nothing.</summary>
    Nothing,
}

/// <summary>The label of a list paragraph.</summary>
/// <param name="Text">The number or bullet text.</param>
/// <param name="Format">Its text format.</param>
/// <param name="Suffix">What follows it.</param>
internal sealed record ListLabel(string Text, RunFormat Format, LabelSuffix Suffix);

/// <summary>A table.</summary>
internal sealed class WordTable : WordBlock
{
    /// <summary>The widths of the grid columns, which cells span.</summary>
    public List<double> Grid { get; } = [];

    /// <summary>The preferred width, or <c>null</c> for the grid's.</summary>
    public double? Width { get; set; }

    /// <summary>The preferred width as a fraction of the text width (0.5 for half), or <c>null</c>.</summary>
    public double? WidthPercent { get; set; }

    /// <summary>The alignment of the table between the margins.</summary>
    public WordAlignment Alignment { get; set; }

    /// <summary>The indent from the start margin.</summary>
    public double Indent { get; set; }

    /// <summary>Whether the table is right-to-left (its first column at the right).</summary>
    public bool RightToLeft { get; set; }

    /// <summary>The outer borders (and, as Inside..., those between cells).</summary>
    public BoxSides Borders { get; set; }

    /// <summary>The borders between rows, or <c>null</c>.</summary>
    public BorderLine? InsideHorizontal { get; set; }

    /// <summary>The borders between columns, or <c>null</c>.</summary>
    public BorderLine? InsideVertical { get; set; }

    /// <summary>The cell padding cells do not set themselves.</summary>
    public CellPadding Padding { get; set; } = new(0, 5.4, 0, 5.4);

    /// <summary>The rows.</summary>
    public List<WordRow> Rows { get; } = [];
}

/// <summary>Cell padding.</summary>
internal readonly record struct CellPadding(double Top, double Right, double Bottom, double Left);

/// <summary>A table row.</summary>
internal sealed class WordRow
{
    /// <summary>Whether the row repeats at the top of every page the table continues on.</summary>
    public bool IsHeader { get; set; }

    /// <summary>Whether the row may not break across pages.</summary>
    public bool CantSplit { get; set; }

    /// <summary>The least height of the row (its exact height when <see cref="ExactHeight"/>), or 0.</summary>
    public double Height { get; set; }

    /// <summary>Whether <see cref="Height"/> is exact.</summary>
    public bool ExactHeight { get; set; }

    /// <summary>The grid columns skipped before the first cell.</summary>
    public int GridBefore { get; set; }

    /// <summary>The cells, in order; cells covered by a cell spanning rows above are left out.</summary>
    public List<WordCell> Cells { get; } = [];
}

/// <summary>Vertical alignment of a cell's content.</summary>
internal enum CellVerticalAlignment
{
    /// <summary>At the top.</summary>
    Top,

    /// <summary>In the middle.</summary>
    Center,

    /// <summary>At the bottom.</summary>
    Bottom,
}

/// <summary>A table cell.</summary>
internal sealed class WordCell
{
    /// <summary>The grid column the cell starts in.</summary>
    public int Column { get; set; }

    /// <summary>The grid columns the cell spans.</summary>
    public int ColumnSpan { get; set; } = 1;

    /// <summary>The rows the cell spans.</summary>
    public int RowSpan { get; set; } = 1;

    /// <summary>The cell's own borders (unset sides come from the table).</summary>
    public BoxSides Borders { get; set; }

    /// <summary>The background color, or <c>null</c>.</summary>
    public PdfColor? Shading { get; set; }

    /// <summary>The padding, or <c>null</c> for the table's; a negative side is the table's too.</summary>
    public CellPadding? Padding { get; set; }

    /// <summary>The vertical alignment.</summary>
    public CellVerticalAlignment VerticalAlignment { get; set; }

    /// <summary>The content.</summary>
    public List<WordBlock> Blocks { get; } = [];
}

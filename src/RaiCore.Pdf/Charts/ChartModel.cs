namespace RaiCore.Pdf.Charts;

/// <summary>
/// A DrawingML chart (<c>c:chartSpace</c>) ready to draw: its title, legend and plot area with chart groups, series
/// (cached values) and axes, with every color resolved (theme colors and the chart style's automatic colors). The
/// reader (<see cref="ChartReader"/>) fills it; the renderer (<see cref="ChartRenderer"/>) draws it.
/// </summary>
internal sealed class Chart
{
    /// <summary>The title, or <c>null</c> for none.</summary>
    public ChartTitle? Title { get; set; }

    /// <summary>The legend, or <c>null</c> for none.</summary>
    public ChartLegend? Legend { get; set; }

    /// <summary>The plot area.</summary>
    public ChartPlotArea PlotArea { get; } = new();

    /// <summary>The fill and outline of the chart area, or <c>null</c> for the default.</summary>
    public ChartShape? Area { get; set; }

    /// <summary>Whether the chart area has rounded corners.</summary>
    public bool RoundedCorners { get; set; }

    /// <summary>The text format all text inherits.</summary>
    public ChartTextFormat Text { get; set; } = new();

    /// <summary>How blank cells are shown in line and scatter charts.</summary>
    public BlankDisplay Blanks { get; set; } = BlankDisplay.Gap;

    /// <summary>The chart style (<c>c:style</c>, 1 to 48; 2 is the default).</summary>
    public int Style { get; set; } = 2;

    /// <summary>The default color of text (the theme's text color at 65%).</summary>
    public PdfColor TextColor { get; set; } = PdfColor.FromRgb(0x59, 0x59, 0x59);

    /// <summary>The default color of gridlines.</summary>
    public PdfColor GridColor { get; set; } = PdfColor.FromRgb(0xD9, 0xD9, 0xD9);

    /// <summary>The default color of axis lines and chart area borders.</summary>
    public PdfColor AxisColor { get; set; } = PdfColor.FromRgb(0x86, 0x86, 0x86);

    /// <summary>The background color (the theme's background color).</summary>
    public PdfColor Background { get; set; } = PdfColor.White;

    /// <summary>The 1904 date system is used for date values.</summary>
    public bool Date1904 { get; set; }

    /// <summary>What the engine draws differently from Office (for diagnostics), such as 3-D charts drawn flat.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>The theme's accent colors 1 to 6, which the chart style's automatic colors come from.</summary>
    public List<PdfColor> Accents { get; } = [.. Enumerable.Range(1, 6).Select(i => DrawingML.DrawingMLColors.OfficeTheme["accent" + i])];

    /// <summary>
    /// Variations Office applies to the accent colors once six series have used them (the default chart colors part,
    /// <c>colors1.xml</c>): luminance modulation and offset.
    /// </summary>
    private static readonly (double Mod, double Off)[] Variations =
        [(1, 0), (0.6, 0), (0.8, 0.2), (0.8, 0), (0.6, 0.4), (0.5, 0), (0.7, 0.3), (0.7, 0), (0.5, 0.5)];

    /// <summary>
    /// The automatic color of a series or point by its index, from the chart style: style 2 (and 10, 18, ...) cycles
    /// the accent colors, darker and lighter after every six; style 1 (9, 17, ...) is shades of gray; styles 3 to 8
    /// are shades of one accent color.
    /// </summary>
    /// <param name="index">The series (or point) index.</param>
    /// <param name="count">How many series (or points) share the colors.</param>
    public PdfColor StyleColor(int index, int count)
    {
        index = Math.Max(index, 0);
        count = Math.Max(count, index + 1);
        var kind = (Style - 1) % 8;
        var fraction = count == 1 ? 0.5 : (index + 1.0) / (count + 1);
        switch (kind)
        {
            case 0:
                // Shades of gray, dark to light.
                var level = 0.2 + (0.6 * (count == 1 ? 0.5 : index / (count - 1.0)));
                return new PdfColor(level, level, level);
            case 1:
                var (mod, off) = Variations[index / 6 % Variations.Length];
                var accent = Accents[index % 6];
                return mod == 1 && off == 0 ? accent : DrawingML.DrawingMLColors.Luminance(accent, mod, off);
            default:
                // One accent color, from a dark shade to a light tint.
                var color = Accents[(kind - 2) % 6];
                return fraction < 0.5
                    ? DrawingML.DrawingMLColors.Luminance(color, 0.5 + fraction, 0)
                    : DrawingML.DrawingMLColors.Luminance(color, 1.5 - fraction, fraction - 0.5);
        }
    }
}

/// <summary>How blank cells are shown.</summary>
internal enum BlankDisplay
{
    /// <summary>A gap in the line.</summary>
    Gap,

    /// <summary>As zero.</summary>
    Zero,

    /// <summary>The line spans the gap.</summary>
    Span,
}

/// <summary>A title: of the chart or of an axis.</summary>
internal sealed class ChartTitle
{
    /// <summary>The text (lines separated by new lines), or <c>null</c> for the automatic title.</summary>
    public string? Text { get; set; }

    /// <summary>The text format.</summary>
    public ChartTextFormat Format { get; set; } = new();

    /// <summary>The fill and outline of the title's box, or <c>null</c>.</summary>
    public ChartShape? Shape { get; set; }

    /// <summary>Whether the title is drawn over the plot area instead of taking space from it.</summary>
    public bool Overlay { get; set; }

    /// <summary>A position given by the file, or <c>null</c> for the automatic one.</summary>
    public ManualLayout? Layout { get; set; }
}

/// <summary>A position and size given as fractions of the chart (<c>c:manualLayout</c>).</summary>
internal sealed class ManualLayout
{
    /// <summary>Whether the rectangle is the plot area's inner one (without tick labels).</summary>
    public bool Inner { get; set; }

    /// <summary>Whether <see cref="X"/> is a factor of the automatic position rather than an edge.</summary>
    public bool XFactor { get; set; }

    /// <summary>Whether <see cref="Y"/> is a factor of the automatic position rather than an edge.</summary>
    public bool YFactor { get; set; }

    /// <summary>Whether <see cref="Width"/> is a factor of the automatic size rather than the size.</summary>
    public bool WidthFactor { get; set; }

    /// <summary>Whether <see cref="Height"/> is a factor of the automatic size rather than the size.</summary>
    public bool HeightFactor { get; set; }

    /// <summary>The left edge (fraction of the chart width), or <c>null</c>.</summary>
    public double? X { get; set; }

    /// <summary>The top edge (fraction of the chart height), or <c>null</c>.</summary>
    public double? Y { get; set; }

    /// <summary>The width (fraction of the chart width), or <c>null</c>.</summary>
    public double? Width { get; set; }

    /// <summary>The height (fraction of the chart height), or <c>null</c>.</summary>
    public double? Height { get; set; }
}

/// <summary>A text format: what <c>a:defRPr</c> and <c>a:rPr</c> give; unset values inherit.</summary>
internal sealed record ChartTextFormat
{
    /// <summary>The size in points.</summary>
    public double? Size { get; init; }

    /// <summary>Whether the text is bold.</summary>
    public bool? Bold { get; init; }

    /// <summary>Whether the text is italic.</summary>
    public bool? Italic { get; init; }

    /// <summary>The color.</summary>
    public PdfColor? Color { get; init; }

    /// <summary>The typeface (a family, or a theme font reference such as <c>+mn-lt</c>).</summary>
    public string? Typeface { get; init; }

    /// <summary>The rotation in degrees counterclockwise, or <c>null</c> for automatic.</summary>
    public double? Rotation { get; init; }

    /// <summary>Whether the text is stacked vertically (<c>vert="wordArtVert"</c> and similar): drawn turned instead.</summary>
    public bool Vertical { get; init; }

    /// <summary>This format with unset values taken from another one.</summary>
    public ChartTextFormat Over(ChartTextFormat? parent) => parent is null ? this : new ChartTextFormat
    {
        Size = Size ?? parent.Size,
        Bold = Bold ?? parent.Bold,
        Italic = Italic ?? parent.Italic,
        Color = Color ?? parent.Color,
        Typeface = Typeface ?? parent.Typeface,
        Rotation = Rotation ?? parent.Rotation,
        Vertical = Vertical || parent.Vertical,
    };
}

/// <summary>A fill and an outline (<c>c:spPr</c>).</summary>
internal sealed record ChartShape
{
    /// <summary>The fill: <c>null</c> to use the automatic one, or a paint (which may be <see cref="Paint.None"/>).</summary>
    public Paint? Fill { get; init; }

    /// <summary>The outline color: <c>null</c> to use the automatic one, or a paint (which may be <see cref="Paint.None"/>).</summary>
    public Paint? Line { get; init; }

    /// <summary>The outline width in points, or <c>null</c> for the automatic one.</summary>
    public double? LineWidth { get; init; }

    /// <summary>The outline's dash pattern in multiples of the line width, or <c>null</c> for solid.</summary>
    public double[]? Dash { get; init; }

    /// <summary>This shape with unset values taken from another one.</summary>
    public ChartShape Over(ChartShape? parent) => parent is null ? this : new ChartShape
    {
        Fill = Fill ?? parent.Fill,
        Line = Line ?? parent.Line,
        LineWidth = LineWidth ?? parent.LineWidth,
        Dash = Dash ?? parent.Dash,
    };
}

/// <summary>A solid paint, or none.</summary>
/// <param name="Color">The color, or <c>null</c> for no paint.</param>
internal sealed record Paint(PdfColor? Color)
{
    /// <summary>No paint.</summary>
    public static Paint None { get; } = new((PdfColor?)null);
}

/// <summary>The plot area: its chart groups and axes.</summary>
internal sealed class ChartPlotArea
{
    /// <summary>A position given by the file, or <c>null</c> for the automatic one.</summary>
    public ManualLayout? Layout { get; set; }

    /// <summary>The chart groups (one per chart type and axis pair), in drawing order.</summary>
    public List<ChartGroup> Groups { get; } = [];

    /// <summary>The axes.</summary>
    public List<ChartAxis> Axes { get; } = [];

    /// <summary>The fill and outline of the plot area, or <c>null</c> for none.</summary>
    public ChartShape? Shape { get; set; }
}

/// <summary>The kinds of chart group.</summary>
internal enum ChartKind
{
    /// <summary>Bars or columns.</summary>
    Bar,

    /// <summary>Lines.</summary>
    Line,

    /// <summary>A pie.</summary>
    Pie,

    /// <summary>A doughnut.</summary>
    Doughnut,

    /// <summary>Areas.</summary>
    Area,

    /// <summary>XY points and lines.</summary>
    Scatter,

    /// <summary>Bubbles.</summary>
    Bubble,

    /// <summary>A radar (spider) chart.</summary>
    Radar,

    /// <summary>A stock chart (high-low-close lines).</summary>
    Stock,

    /// <summary>Box and whisker plots (chartex <c>boxWhisker</c>): the statistics of each series' values by category.</summary>
    BoxWhisker,

    /// <summary>A treemap (chartex): nested rectangles, their areas in proportion to the values of their leaves.</summary>
    Treemap,

    /// <summary>A sunburst (chartex): rings of a hierarchy, their angles in proportion to the values of their leaves.</summary>
    Sunburst,
}

/// <summary>How the series of a group are combined.</summary>
internal enum ChartGrouping
{
    /// <summary>Side by side (bars) or independent (lines, areas).</summary>
    Standard,

    /// <summary>Stacked on each other.</summary>
    Stacked,

    /// <summary>Stacked to 100%.</summary>
    PercentStacked,
}

/// <summary>A chart group: series of one chart type that share a pair of axes (<c>c:barChart</c>, <c>c:lineChart</c>...).</summary>
internal sealed class ChartGroup
{
    /// <summary>The chart type.</summary>
    public ChartKind Kind { get; set; }

    /// <summary>Whether bars are horizontal (<c>barDir="bar"</c>).</summary>
    public bool Horizontal { get; set; }

    /// <summary>How the series are combined.</summary>
    public ChartGrouping Grouping { get; set; }

    /// <summary>Whether each point has its own color (a single series or a pie).</summary>
    public bool VaryColors { get; set; }

    /// <summary>The gap between bar clusters, in percent of a bar's width.</summary>
    public double GapWidth { get; set; } = 150;

    /// <summary>How much bars of a cluster overlap, in percent of a bar's width (negative for gaps).</summary>
    public double Overlap { get; set; }

    /// <summary>The doughnut hole's size in percent of the diameter.</summary>
    public double HoleSize { get; set; } = 50;

    /// <summary>The angle of the first slice of a pie, clockwise from the top, in degrees.</summary>
    public double FirstSliceAngle { get; set; }

    /// <summary>Whether a line chart draws its automatic markers.</summary>
    public bool Markers { get; set; } = true;

    /// <summary>Whether a scatter chart draws lines by default (its <c>scatterStyle</c>).</summary>
    public bool ScatterLines { get; set; } = true;

    /// <summary>Whether a scatter chart draws smooth lines by default.</summary>
    public bool ScatterSmooth { get; set; }

    /// <summary>Whether a radar chart is filled.</summary>
    public bool RadarFilled { get; set; }

    /// <summary>Whether a radar chart draws markers by default.</summary>
    public bool RadarMarkers { get; set; }

    /// <summary>The bubble size scale in percent.</summary>
    public double BubbleScale { get; set; } = 100;

    /// <summary>Whether bubble sizes are proportional to their width rather than their area.</summary>
    public bool BubbleSizeWidth { get; set; }

    /// <summary>The high-low lines of a stock or line chart, or <c>null</c>.</summary>
    public ChartShape? HighLowLines { get; set; }

    /// <summary>The drop lines of a line or area chart, or <c>null</c>.</summary>
    public ChartShape? DropLines { get; set; }

    /// <summary>The up and down bars of a stock or line chart (up fill, down fill), or <c>null</c>.</summary>
    public (ChartShape Up, ChartShape Down)? UpDownBars { get; set; }

    /// <summary>Whether the group was a 3-D chart (drawn flat).</summary>
    public bool ThreeD { get; set; }

    /// <summary>The ids of the axes the group uses (category or X axis first).</summary>
    public List<long> AxisIds { get; } = [];

    /// <summary>The series, in their plot order.</summary>
    public List<ChartSeries> Series { get; } = [];

    /// <summary>The data labels of all series, or <c>null</c>.</summary>
    public ChartDataLabels? Labels { get; set; }

    /// <summary>Whether lines join the ends of floating bars (a waterfall's connector lines).</summary>
    public bool Connectors { get; set; }

    /// <summary>What box and whisker plots show besides the boxes and whiskers.</summary>
    public BoxOptions Box { get; set; } = new();

    /// <summary>Where a treemap puts the names of the nodes that have children.</summary>
    public ParentLabels ParentLabels { get; set; } = ParentLabels.Overlapping;
}

/// <summary>What box and whisker plots show besides the boxes and whiskers (chartex <c>cx:visibility</c>).</summary>
/// <param name="MeanLine">A line through the means of a series' boxes.</param>
/// <param name="MeanMarker">A marker at each mean.</param>
/// <param name="InnerPoints">The points between the whiskers.</param>
/// <param name="Outliers">The points beyond the whiskers.</param>
internal sealed record BoxOptions(bool MeanLine = false, bool MeanMarker = true, bool InnerPoints = false, bool Outliers = true);

/// <summary>Where a treemap puts the names of the nodes that have children (chartex <c>parentLabelLayout</c>).</summary>
internal enum ParentLabels
{
    /// <summary>Over the top-left corner of their children.</summary>
    Overlapping,

    /// <summary>In a strip above their children.</summary>
    Banner,

    /// <summary>Not shown.</summary>
    None,
}

/// <summary>
/// The statistics of a box and whisker plot of one series in one category: quartiles (by the file's quartile method),
/// median, mean, whiskers (the furthest values within 1.5 times the interquartile range of the box) and the values.
/// </summary>
/// <param name="Category">The category's index on the category axis.</param>
/// <param name="Q1">The first quartile.</param>
/// <param name="Median">The median.</param>
/// <param name="Q3">The third quartile.</param>
/// <param name="Mean">The mean.</param>
/// <param name="WhiskerLow">The end of the lower whisker.</param>
/// <param name="WhiskerHigh">The end of the upper whisker.</param>
/// <param name="Values">The values, ascending.</param>
internal sealed record BoxStatistics(int Category, double Q1, double Median, double Q3, double Mean, double WhiskerLow, double WhiskerHigh, double[] Values);

/// <summary>A node of a treemap's or sunburst's hierarchy: its name, value (the sum of its leaves) and children.</summary>
internal sealed class ChartNode(string name)
{
    /// <summary>The name (a category).</summary>
    public string Name { get; } = name;

    /// <summary>The value: a leaf's own, or the sum of its children's.</summary>
    public double Value { get; set; }

    /// <summary>The index of a leaf's point (for its format and label), or -1 for a node with children.</summary>
    public int Point { get; set; } = -1;

    /// <summary>The children, in order.</summary>
    public List<ChartNode> Children { get; } = [];
}

/// <summary>A series: its name, categories (or X values), values and format.</summary>
internal sealed class ChartSeries
{
    /// <summary>The series index (for automatic colors).</summary>
    public int Index { get; set; }

    /// <summary>The plot order.</summary>
    public int Order { get; set; }

    /// <summary>The name, or <c>null</c>.</summary>
    public string? Name { get; set; }

    /// <summary>The categories (or X values of a scatter or bubble series), or <c>null</c>.</summary>
    public ChartData? Categories { get; set; }

    /// <summary>The values (Y values of a scatter series).</summary>
    public ChartData Values { get; set; } = ChartData.Empty;

    /// <summary>The bubble sizes, or <c>null</c>.</summary>
    public ChartData? BubbleSizes { get; set; }

    /// <summary>The fill and outline, automatic parts already resolved.</summary>
    public ChartShape Shape { get; set; } = new();

    /// <summary>The automatic color of the series (from the chart style).</summary>
    public PdfColor AutoColor { get; set; } = PdfColor.Black;

    /// <summary>The marker, or <c>null</c> for the automatic one.</summary>
    public ChartMarker? Marker { get; set; }

    /// <summary>The automatic marker symbol of the series.</summary>
    public MarkerSymbol AutoMarker { get; set; } = MarkerSymbol.Diamond;

    /// <summary>Whether the line is smoothed, or <c>null</c> for the group's default.</summary>
    public bool? Smooth { get; set; }

    /// <summary>How far slices are pulled out of a pie, in percent of the radius.</summary>
    public double Explosion { get; set; }

    /// <summary>Whether negative bars are drawn inverted (white).</summary>
    public bool InvertIfNegative { get; set; }

    /// <summary>Formats of single points, by index.</summary>
    public Dictionary<int, ChartPoint> Points { get; } = [];

    /// <summary>The data labels, or <c>null</c> for the group's.</summary>
    public ChartDataLabels? Labels { get; set; }

    /// <summary>
    /// Where floating bars start (waterfalls, funnels), by point, or <c>null</c> for bars from the axis: a point's bar
    /// runs from its start to its start plus its value.
    /// </summary>
    public double?[]? Starts { get; set; }

    /// <summary>The legend's entries for the series (a waterfall's increase, decrease and total), or <c>null</c> for its name.</summary>
    public List<(string Text, PdfColor Color)>? LegendKeys { get; set; }

    /// <summary>The box and whisker statistics of the series by category, or <c>null</c>.</summary>
    public List<BoxStatistics>? Boxes { get; set; }

    /// <summary>The root of a treemap's or sunburst's hierarchy (its children are the top level), or <c>null</c>.</summary>
    public ChartNode? Hierarchy { get; set; }
}

/// <summary>The cached data of a series' categories, values or sizes.</summary>
internal sealed class ChartData
{
    /// <summary>No data.</summary>
    public static ChartData Empty { get; } = new([], null, null);

    /// <summary>Creates data from numbers (or <c>null</c> for blanks) and texts.</summary>
    public ChartData(double?[] numbers, string?[]? texts, string? formatCode)
    {
        Numbers = numbers;
        Texts = texts;
        FormatCode = formatCode;
    }

    /// <summary>The numbers by point index, <c>null</c> for blanks (all <c>null</c> for text data).</summary>
    public double?[] Numbers { get; }

    /// <summary>The texts by point index, or <c>null</c> for numeric data.</summary>
    public string?[]? Texts { get; }

    /// <summary>The number format code of the data, or <c>null</c> for General.</summary>
    public string? FormatCode { get; }

    /// <summary>The number of points.</summary>
    public int Count => Texts?.Length ?? Numbers.Length;

    /// <summary>Whether the data is text (categories).</summary>
    public bool IsText => Texts is not null;

    /// <summary>The number of a point, or <c>null</c>.</summary>
    public double? Number(int index) => index >= 0 && index < Numbers.Length ? Numbers[index] : null;
}

/// <summary>The format of one point.</summary>
internal sealed class ChartPoint
{
    /// <summary>The fill and outline, or <c>null</c>.</summary>
    public ChartShape? Shape { get; set; }

    /// <summary>The marker, or <c>null</c>.</summary>
    public ChartMarker? Marker { get; set; }

    /// <summary>The slice explosion in percent, or <c>null</c>.</summary>
    public double? Explosion { get; set; }

    /// <summary>Whether a negative bar is inverted, or <c>null</c>.</summary>
    public bool? InvertIfNegative { get; set; }
}

/// <summary>Marker symbols.</summary>
internal enum MarkerSymbol
{
    /// <summary>The automatic symbol of the series.</summary>
    Auto,

    /// <summary>No marker.</summary>
    None,

    /// <summary>A circle.</summary>
    Circle,

    /// <summary>A short horizontal dash.</summary>
    Dash,

    /// <summary>A diamond.</summary>
    Diamond,

    /// <summary>A small dot.</summary>
    Dot,

    /// <summary>A plus sign.</summary>
    Plus,

    /// <summary>A square.</summary>
    Square,

    /// <summary>An asterisk-like star.</summary>
    Star,

    /// <summary>A triangle.</summary>
    Triangle,

    /// <summary>An X.</summary>
    X,
}

/// <summary>A marker: its symbol, size and format.</summary>
internal sealed class ChartMarker
{
    /// <summary>The symbol.</summary>
    public MarkerSymbol Symbol { get; set; }

    /// <summary>The size in points (2 to 72), or <c>null</c>.</summary>
    public double? Size { get; set; }

    /// <summary>The fill and outline, or <c>null</c>.</summary>
    public ChartShape? Shape { get; set; }
}

/// <summary>Data labels of a group, series or point.</summary>
internal sealed class ChartDataLabels
{
    /// <summary>Whether the labels are deleted.</summary>
    public bool Delete { get; set; }

    /// <summary>Show the value.</summary>
    public bool ShowValue { get; set; }

    /// <summary>Show the category name.</summary>
    public bool ShowCategory { get; set; }

    /// <summary>Show the series name.</summary>
    public bool ShowSeries { get; set; }

    /// <summary>Show the percentage (pie charts).</summary>
    public bool ShowPercent { get; set; }

    /// <summary>Show the bubble size.</summary>
    public bool ShowBubbleSize { get; set; }

    /// <summary>The position (<c>outEnd</c>, <c>ctr</c>, <c>t</c>, <c>bestFit</c>...), or <c>null</c> for the default.</summary>
    public string? Position { get; set; }

    /// <summary>The number format, or <c>null</c> for the data's.</summary>
    public string? FormatCode { get; set; }

    /// <summary>The separator between parts, or <c>null</c> for the default.</summary>
    public string? Separator { get; set; }

    /// <summary>The text format.</summary>
    public ChartTextFormat Format { get; set; } = new();

    /// <summary>Labels of single points (which may be deleted), by index.</summary>
    public Dictionary<int, ChartDataLabels> Points { get; } = [];

    /// <summary>Whether anything is shown.</summary>
    public bool Shows => !Delete && (ShowValue || ShowCategory || ShowSeries || ShowPercent || ShowBubbleSize);
}

/// <summary>The kinds of axis.</summary>
internal enum AxisKind
{
    /// <summary>Categories.</summary>
    Category,

    /// <summary>Values.</summary>
    Value,

    /// <summary>Dates (categories on a time scale).</summary>
    Date,

    /// <summary>Series (3-D charts; not drawn).</summary>
    Series,
}

/// <summary>Where an axis is.</summary>
internal enum AxisPosition
{
    /// <summary>At the bottom.</summary>
    Bottom,

    /// <summary>At the left.</summary>
    Left,

    /// <summary>At the right.</summary>
    Right,

    /// <summary>At the top.</summary>
    Top,
}

/// <summary>Tick marks.</summary>
internal enum TickMark
{
    /// <summary>None.</summary>
    None,

    /// <summary>Inside the plot area.</summary>
    Inside,

    /// <summary>Outside the plot area.</summary>
    Outside,

    /// <summary>Across the axis.</summary>
    Cross,
}

/// <summary>Where the tick labels of an axis are.</summary>
internal enum TickLabelPosition
{
    /// <summary>Next to the axis.</summary>
    NextTo,

    /// <summary>At the low end of the crossing axis.</summary>
    Low,

    /// <summary>At the high end of the crossing axis.</summary>
    High,

    /// <summary>No labels.</summary>
    None,
}

/// <summary>Where an axis crosses the other one.</summary>
internal enum AxisCrosses
{
    /// <summary>At zero (or the minimum when zero is out of range).</summary>
    AutoZero,

    /// <summary>At the minimum.</summary>
    Min,

    /// <summary>At the maximum.</summary>
    Max,
}

/// <summary>An axis.</summary>
internal sealed class ChartAxis
{
    /// <summary>The id groups refer to it by.</summary>
    public long Id { get; set; }

    /// <summary>The kind.</summary>
    public AxisKind Kind { get; set; }

    /// <summary>The position.</summary>
    public AxisPosition Position { get; set; }

    /// <summary>Whether the axis is hidden (its scale still applies).</summary>
    public bool Deleted { get; set; }

    /// <summary>The id of the axis this one crosses.</summary>
    public long CrossAxisId { get; set; }

    /// <summary>Where the crossing axis crosses this one.</summary>
    public AxisCrosses Crosses { get; set; }

    /// <summary>The value at which the crossing axis crosses this one, or <c>null</c> (see <see cref="Crosses"/>).</summary>
    public double? CrossesAt { get; set; }

    /// <summary>Whether the axis runs from its maximum to its minimum.</summary>
    public bool Reversed { get; set; }

    /// <summary>The fixed minimum, or <c>null</c> for automatic.</summary>
    public double? Min { get; set; }

    /// <summary>The fixed maximum, or <c>null</c> for automatic.</summary>
    public double? Max { get; set; }

    /// <summary>The fixed major unit, or <c>null</c> for automatic.</summary>
    public double? MajorUnit { get; set; }

    /// <summary>The fixed minor unit, or <c>null</c> for automatic.</summary>
    public double? MinorUnit { get; set; }

    /// <summary>The logarithm base of a log scale, or <c>null</c> for a linear scale.</summary>
    public double? LogBase { get; set; }

    /// <summary>The number format of the tick labels, or <c>null</c>.</summary>
    public string? FormatCode { get; set; }

    /// <summary>Whether the number format follows the data's.</summary>
    public bool SourceLinked { get; set; } = true;

    /// <summary>The major gridlines, or <c>null</c> for none.</summary>
    public ChartShape? MajorGridlines { get; set; }

    /// <summary>The minor gridlines, or <c>null</c> for none.</summary>
    public ChartShape? MinorGridlines { get; set; }

    /// <summary>The major tick marks.</summary>
    public TickMark MajorTicks { get; set; } = TickMark.Cross;

    /// <summary>The minor tick marks.</summary>
    public TickMark MinorTicks { get; set; } = TickMark.None;

    /// <summary>Where the tick labels are.</summary>
    public TickLabelPosition Labels { get; set; }

    /// <summary>The axis line, or <c>null</c> for the automatic one.</summary>
    public ChartShape? Line { get; set; }

    /// <summary>The text format of the tick labels.</summary>
    public ChartTextFormat Text { get; set; } = new();

    /// <summary>The title, or <c>null</c>.</summary>
    public ChartTitle? Title { get; set; }

    /// <summary>Whether a value axis crosses a category axis in the middle of a category (<c>between</c>) rather than on it.</summary>
    public bool CrossBetween { get; set; } = true;

    /// <summary>Label every nth category, or <c>null</c> for automatic.</summary>
    public int? LabelSkip { get; set; }

    /// <summary>The base time unit of a date axis: <c>days</c>, <c>months</c> or <c>years</c>, or <c>null</c>.</summary>
    public string? BaseTimeUnit { get; set; }

    /// <summary>The major time unit of a date axis, or <c>null</c>.</summary>
    public string? MajorTimeUnit { get; set; }
}

/// <summary>Where the legend is.</summary>
internal enum LegendPosition
{
    /// <summary>At the right.</summary>
    Right,

    /// <summary>At the left.</summary>
    Left,

    /// <summary>At the top.</summary>
    Top,

    /// <summary>At the bottom.</summary>
    Bottom,

    /// <summary>In the top right corner.</summary>
    TopRight,
}

/// <summary>The legend.</summary>
internal sealed class ChartLegend
{
    /// <summary>The position.</summary>
    public LegendPosition Position { get; set; }

    /// <summary>Whether the legend is drawn over the plot area instead of taking space from it.</summary>
    public bool Overlay { get; set; }

    /// <summary>A position given by the file, or <c>null</c>.</summary>
    public ManualLayout? Layout { get; set; }

    /// <summary>The text format.</summary>
    public ChartTextFormat Text { get; set; } = new();

    /// <summary>The fill and outline of the legend's box, or <c>null</c>.</summary>
    public ChartShape? Shape { get; set; }

    /// <summary>The indexes of entries that are deleted.</summary>
    public HashSet<int> Deleted { get; } = [];
}

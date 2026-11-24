using System.Globalization;
using System.Xml.Linq;

namespace RaiCore.Pdf.Charts;

/// <summary>
/// Reads the newer chart types of Office 2016 (chartex, <c>cx:chartSpace</c>) into the chart model: waterfalls (floating
/// bars from running totals, with totals), histograms (bins counted here, as Excel bins them) and paretos (sorted bars
/// and a cumulative percentage line), box and whisker plots (quartiles by the file's quartile method), funnels
/// (centered bars), treemaps and sunbursts (hierarchies). Data comes from the cached levels of <c>cx:chartData</c>,
/// or from the caller's cells for references without them. Region maps are left out.
/// </summary>
internal sealed partial class ChartReader
{
    /// <summary>The namespace of chartex parts.</summary>
    internal const string ChartExNamespace = "http://schemas.microsoft.com/office/drawing/2014/chartex";

    /// <summary>The most bins a histogram gets.</summary>
    internal const int MaxBins = 1000;

    /// <summary>The deepest hierarchy of a treemap or sunburst.</summary>
    internal const int MaxLevels = 32;

    /// <summary>A dimension of chartex data: its levels, outermost first (one for flat data), in point order.</summary>
    private List<XElement> _exAxes = [];

    private sealed record ExDimension(List<ChartData> Levels)
    {
        public ChartData Leaf => Levels[^1];
    }

    private Chart? ReadChartEx(XElement root, ICollection<string>? unsupported)
    {
        var chartElement = Child(root, "chart");
        var plot = Child(chartElement, "plotArea");
        var region = Child(plot, "plotAreaRegion");
        if (chartElement is null || plot is null || region is null)
        {
            return null;
        }

        var chart = _chart;
        chart.RoundedCorners = false;
        chart.TextColor = DrawingML.DrawingMLColors.Luminance(Scheme("tx1"), 0.65, 0.35);
        chart.GridColor = DrawingML.DrawingMLColors.Luminance(Scheme("tx1"), 0.15, 0.85);
        chart.AxisColor = DrawingML.DrawingMLColors.Luminance(Scheme("tx1"), 0.5, 0.5);
        chart.Background = Scheme("bg1");
        chart.Accents.Clear();
        for (var i = 1; i <= 6; i++)
        {
            chart.Accents.Add(Scheme("accent" + i.ToString(CultureInfo.InvariantCulture)));
        }

        chart.Text = TextFormat(Child(root, "txPr")) ?? new ChartTextFormat();
        chart.Area = Shape(Child(root, "spPr"));
        chart.PlotArea.Shape = Shape(Child(Child(region, "plotSurface"), "spPr"));

        var data = new Dictionary<double, Dictionary<string, ExDimension>>();
        foreach (var element in Children(Child(root, "chartData")).Where(e => e.Name.LocalName == "data"))
        {
            ResourceLimits.Checkpoint();
            if (Val(element, "id") is not { } id || data.ContainsKey(id))
            {
                continue;
            }

            var dimensions = new Dictionary<string, ExDimension>(StringComparer.Ordinal);
            foreach (var dimension in Children(element))
            {
                var text = dimension.Name.LocalName == "strDim";
                if ((text || dimension.Name.LocalName == "numDim") && Dimension(dimension, text) is { } read)
                {
                    dimensions.TryAdd(Attr(dimension, "type") ?? (text ? "cat" : "val"), read);
                }
            }

            data[id] = dimensions;
        }

        var axes = Children(plot).Where(e => e.Name.LocalName == "axis").ToList();
        _exAxes = axes;
        var series = Children(region).Where(e => e.Name.LocalName == "series" && Bool(e, "hidden") != true).ToList();
        var layouts = series.Select(s => Attr(s, "layoutId") ?? string.Empty).ToList();
        var index = 0;
        for (var i = 0; i < series.Count; i++)
        {
            ResourceLimits.Checkpoint();
            var element = series[i];
            var dimensions = Val(Child(element, "dataId"), "val") is { } id && data.TryGetValue(id, out var found) ? found : null;
            switch (layouts[i])
            {
                case "waterfall":
                    Waterfall(element, dimensions, index++);
                    break;
                case "clusteredColumn":
                    var pareto = series.Where((s, k) => layouts[k] == "paretoLine" && (Val(s, "ownerIdx") ?? 0) == i).FirstOrDefault();
                    Columns(element, dimensions, index++, pareto);
                    if (pareto is not null)
                    {
                        index++;
                    }

                    break;
                case "funnel":
                    Funnel(element, dimensions, index++);
                    break;
                case "boxWhisker":
                    BoxWhisker(element, dimensions, index++);
                    break;
                case "treemap" or "sunburst":
                    Hierarchy(element, dimensions, index++, layouts[i] == "treemap" ? ChartKind.Treemap : ChartKind.Sunburst);
                    break;
                case "paretoLine":
                    break;
                case "regionMap":
                    unsupported?.Add("region map");
                    break;
                default:
                    unsupported?.Add(layouts[i].Length == 0 ? "chartex" : layouts[i]);
                    break;
            }
        }

        if (chart.PlotArea.Groups.Count == 0)
        {
            return chart;
        }

        // Axes: the file's, by id; groups name theirs (a category axis and a value axis).
        foreach (var axis in axes)
        {
            if (Val(axis, "id") is { } id && chart.PlotArea.Axes.FirstOrDefault(a => a.Id == (long)id) is { } target)
            {
                ExAxis(axis, target);
            }
        }

        var groupsBySeries = chart.PlotArea.Groups.SelectMany(g => g.Series).ToList();
        foreach (var s in groupsBySeries.Where(s => s.LegendKeys is null))
        {
            s.AutoColor = chart.StyleColor(s.Index, groupsBySeries.Count);
        }

        if (Child(chartElement, "title") is { } titleElement)
        {
            chart.Title = ExTitle(titleElement);
            chart.Title.Text ??= groupsBySeries.Count == 1 ? groupsBySeries[0].Name ?? "Chart Title" : "Chart Title";
        }

        if (Child(chartElement, "legend") is { } legend)
        {
            chart.Legend = new ChartLegend
            {
                Position = Attr(legend, "pos") switch
                {
                    "l" => LegendPosition.Left,
                    "t" => LegendPosition.Top,
                    "b" => LegendPosition.Bottom,
                    _ => LegendPosition.Right,
                },
                Overlay = Bool(legend, "overlay") ?? false,
                Text = TextFormat(Child(legend, "txPr")) ?? new ChartTextFormat(),
                Shape = Shape(Child(legend, "spPr")),
            };
        }

        return chart;
    }

    /// <summary>A number attribute, or <c>null</c>.</summary>
    private static double? Val(XElement? element, string name) =>
        double.TryParse(Attr(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    /// <summary>
    /// A dimension: its cached levels (the file lists the innermost first; here the outermost comes first), or the
    /// caller's cells for a formula without them (several columns of category cells are levels, the first outermost).
    /// </summary>
    private ExDimension? Dimension(XElement element, bool text)
    {
        var levels = new List<ChartData>();
        foreach (var level in Children(element).Where(e => e.Name.LocalName == "lvl").Take(MaxLevels))
        {
            if (Level(level, text) is { } read)
            {
                levels.Add(read);
            }
        }

        if (levels.Count > 0)
        {
            levels.Reverse();
            return new ExDimension(levels);
        }

        if (_references is null || Child(element, "f") is not { Value.Length: > 0 } formula || _references(formula.Value.Trim()) is not { } cells)
        {
            return null;
        }

        var columns = Math.Max(1, cells.Columns);
        var rows = (cells.Values.Count + columns - 1) / columns;
        var byRow = Attr(formula, "dir") == "row";
        var (count, lines) = byRow ? (columns, rows) : (rows, columns);
        if (!text)
        {
            // Numbers: all the cells, in order.
            (count, lines) = (cells.Values.Count, 1);
        }

        lines = Math.Min(lines, MaxLevels);
        count = Math.Min(count, MaxPointsPerSeries);
        for (var line = 0; line < lines; line++)
        {
            Count(count);
            var numbers = new double?[count];
            var texts = new string?[count];
            for (var i = 0; i < count; i++)
            {
                var at = !text ? i : byRow ? (line * columns) + i : (i * columns) + line;
                switch (at < cells.Values.Count ? cells.Values[at] : null)
                {
                    case double number when double.IsFinite(number):
                        numbers[i] = number;
                        texts[i] = ChartNumberFormat.Format(number, cells.FormatCode);
                        break;
                    case string value:
                        texts[i] = value;
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed))
                        {
                            numbers[i] = parsed;
                        }

                        break;
                }
            }

            levels.Add(new ChartData(numbers, text ? texts : null, cells.FormatCode));
        }

        return levels.Count == 0 ? null : new ExDimension(levels);
    }

    /// <summary>A cached level (<c>cx:lvl</c>): its points (<c>cx:pt</c>, the value as the element's text).</summary>
    private ChartData? Level(XElement level, bool text)
    {
        var points = new List<(int Index, string Value)>();
        var maxIndex = -1;
        foreach (var point in Children(level).Where(e => e.Name.LocalName == "pt"))
        {
            if (Attr(point, "idx") is { } idxText && int.TryParse(idxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index is >= 0 and < MaxPointsPerSeries)
            {
                points.Add((index, point.Value));
                maxIndex = Math.Max(maxIndex, index);
            }
        }

        var count = maxIndex + 1;
        if (Val(level, "ptCount") is { } stated && stated > count)
        {
            count = (int)Math.Min(stated, Math.Max(count, TrustedPointCount));
        }

        Count(count);
        var numbers = new double?[count];
        var texts = text ? new string?[count] : null;
        foreach (var (index, value) in points)
        {
            if (texts is not null)
            {
                texts[index] = value;
            }

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
            {
                numbers[index] = number;
            }
        }

        return new ChartData(numbers, texts, Attr(level, "formatCode") is { Length: > 0 } code ? code : null);
    }

    /// <summary>The value and category dimensions of a series' data (values: <c>val</c>, or <c>size</c>).</summary>
    private static (ChartData Values, ExDimension? Categories) Dimensions(Dictionary<string, ExDimension>? dimensions)
    {
        if (dimensions is null)
        {
            return (ChartData.Empty, null);
        }

        var values = dimensions.TryGetValue("val", out var val) ? val.Leaf : dimensions.TryGetValue("size", out var size) ? size.Leaf : ChartData.Empty;
        return (values, dimensions.TryGetValue("cat", out var cat) ? cat : null);
    }

    /// <summary>A new series with what every chartex series has: its name, fill and outline, points and data labels.</summary>
    private ChartSeries ExSeries(XElement element, int index, ChartData values, ChartData? categories)
    {
        var series = new ChartSeries
        {
            Index = index,
            Order = index,
            Name = Child(Child(element, "tx"), "txData") is { } txData ? Child(txData, "v")?.Value : null,
            Values = values,
            Categories = categories,
            Shape = Shape(Child(element, "spPr")) ?? new ChartShape(),
            Labels = ExLabels(Child(element, "dataLabels")),
        };
        foreach (var point in Children(element).Where(e => e.Name.LocalName == "dataPt"))
        {
            if (Val(point, "idx") is { } idx && idx is >= 0 and < MaxPointsPerSeries && Shape(Child(point, "spPr")) is { } shape)
            {
                series.Points[(int)idx] = new ChartPoint { Shape = shape };
            }
        }

        return series;
    }

    /// <summary>Data labels (<c>cx:dataLabels</c>): what they show (<c>cx:visibility</c>), where, their format and text.</summary>
    private ChartDataLabels? ExLabels(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        ChartDataLabels Read(XElement e)
        {
            var visibility = Child(e, "visibility");
            return new ChartDataLabels
            {
                ShowValue = Bool(visibility, "value") ?? visibility is null,
                ShowCategory = Bool(visibility, "categoryName") ?? false,
                ShowSeries = Bool(visibility, "seriesName") ?? false,
                Position = Attr(e, "pos"),
                FormatCode = Child(e, "numFmt") is { } format && Bool(format, "sourceLinked") != true && Attr(format, "formatCode") is { Length: > 0 } code ? code : null,
                Separator = Child(e, "separator")?.Value,
                Format = TextFormat(Child(e, "txPr")) ?? new ChartTextFormat(),
            };
        }

        var labels = Read(element);
        foreach (var point in Children(element))
        {
            if (point.Name.LocalName is "dataLabel" or "dataLabelHidden" && Val(point, "idx") is { } idx && idx is >= 0 and < MaxPointsPerSeries)
            {
                labels.Points[(int)idx] = point.Name.LocalName == "dataLabelHidden" ? new ChartDataLabels { Delete = true } : Read(point);
            }
        }

        return labels;
    }

    /// <summary>A group with axes: a category axis and a value axis, by the series' axis ids (or 0 and 1).</summary>
    private ChartGroup AxisGroup(ChartKind kind, XElement element, long categoryId = 0, long valueId = 1)
    {
        var ids = Children(element).Where(e => e.Name.LocalName == "axisId").Select(e => Val(e, "val")).OfType<double>().Select(v => (long)v).ToList();
        if (ids.Count == 0 && _exAxes.Count > 0)
        {
            // Without axis ids, the file's first category axis and first value axis.
            categoryId = _exAxes.Where(a => Child(a, "catScaling") is not null).Select(a => (long?)(long)(Val(a, "id") ?? 0)).FirstOrDefault() ?? categoryId;
            valueId = _exAxes.Where(a => Child(a, "valScaling") is not null).Select(a => (long?)(long)(Val(a, "id") ?? 1)).FirstOrDefault() ?? valueId;
        }
        else if (ids.Count == 1)
        {
            // One id: the file's axis of that id says which it is (a category axis has a category scaling).
            var named = _exAxes.FirstOrDefault(a => Val(a, "id") is { } id && (long)id == ids[0]);
            if (Child(named, "catScaling") is not null)
            {
                categoryId = ids[0];
            }
            else
            {
                valueId = ids[0];
            }
        }
        else if (ids.Count >= 2)
        {
            (categoryId, valueId) = (ids[0], ids[1]);
        }

        var group = new ChartGroup { Kind = kind };
        group.AxisIds.Add(categoryId);
        group.AxisIds.Add(valueId);
        AddAxis(categoryId, AxisKind.Category, AxisPosition.Bottom);
        AddAxis(valueId, AxisKind.Value, AxisPosition.Left);
        _chart.PlotArea.Groups.Add(group);
        return group;
    }

    /// <summary>An axis the groups use, made up hidden until the file's axis of its id is read.</summary>
    private ChartAxis AddAxis(long id, AxisKind kind, AxisPosition position)
    {
        if (_chart.PlotArea.Axes.FirstOrDefault(a => a.Id == id) is { } existing)
        {
            return existing;
        }

        var axis = new ChartAxis { Id = id, Kind = kind, Position = position, Deleted = true, MajorTicks = TickMark.None, Labels = TickLabelPosition.NextTo };
        _chart.PlotArea.Axes.Add(axis);
        return axis;
    }

    /// <summary>Reads a chartex axis (<c>cx:axis</c>) into one the groups use.</summary>
    private void ExAxis(XElement element, ChartAxis axis)
    {
        axis.Deleted = Bool(element, "hidden") == true || Child(element, "tickLabels") is null;
        if (Child(element, "valScaling") is { } scaling)
        {
            axis.Min = Val(scaling, "min") ?? axis.Min;
            axis.Max = Val(scaling, "max") ?? axis.Max;
            axis.MajorUnit = Val(scaling, "majorUnit") is { } major && major > 0 ? major : axis.MajorUnit;
            axis.MinorUnit = Val(scaling, "minorUnit") is { } minor && minor > 0 ? minor : axis.MinorUnit;
        }

        if (Child(element, "catScaling") is not null)
        {
            // Category labels stay at the plot area's edge when values go below zero.
            axis.Labels = TickLabelPosition.Low;
        }

        if (Child(element, "catScaling") is { } categories && Val(categories, "gapWidth") is { } gap)
        {
            foreach (var group in _chart.PlotArea.Groups.Where(g => g.AxisIds.Count > 0 && g.AxisIds[0] == axis.Id))
            {
                group.GapWidth = Math.Clamp(gap * 100, 0, 500);
            }
        }

        axis.MajorGridlines = Child(element, "majorGridlines") is { } major2 ? Shape(Child(major2, "spPr")) ?? new ChartShape() : null;
        axis.MinorGridlines = Child(element, "minorGridlines") is { } minor2 ? Shape(Child(minor2, "spPr")) ?? new ChartShape() : null;
        axis.MajorTicks = Attr(Child(element, "majorTickMarks"), "type") switch
        {
            "in" => TickMark.Inside,
            "out" => TickMark.Outside,
            "cross" => TickMark.Cross,
            _ => TickMark.None,
        };
        if (Child(element, "numFmt") is { } format && Attr(format, "formatCode") is { Length: > 0 } code)
        {
            axis.FormatCode = code;
            axis.SourceLinked = Bool(format, "sourceLinked") ?? false;
        }
        else if (Attr(Child(element, "units"), "unit") == "percentage")
        {
            axis.FormatCode = "0%";
            axis.SourceLinked = false;
        }

        axis.Text = TextFormat(Child(element, "txPr")) ?? axis.Text;
        axis.Line = Shape(Child(element, "spPr"));
        if (Child(element, "title") is { } title)
        {
            axis.Title = ExTitle(title);
        }
    }

    /// <summary>A chartex title: its text (<c>cx:txData/cx:v</c> or rich text) and format.</summary>
    private ChartTitle ExTitle(XElement element)
    {
        var title = new ChartTitle
        {
            Overlay = Bool(element, "overlay") ?? false,
            Shape = Shape(Child(element, "spPr")),
        };
        var format = TextFormat(Child(element, "txPr")) ?? new ChartTextFormat();
        var tx = Child(element, "tx");
        if (Child(tx, "rich") is { } rich)
        {
            var (text, richFormat) = RichText(rich);
            title.Text = text;
            format = richFormat.Over(format);
        }
        else if (Child(Child(tx, "txData"), "v") is { } v)
        {
            title.Text = v.Value;
        }

        title.Format = format;
        return title;
    }

    /// <summary>
    /// A waterfall: each point a bar floating from the running total to the total plus its value, except the totals
    /// (<c>cx:subtotals</c>), which stand on the axis and set the running total; increases, decreases and totals in
    /// their own colors (the first three accents, or the file's), each with a legend entry.
    /// </summary>
    private void Waterfall(XElement element, Dictionary<string, ExDimension>? dimensions, int index)
    {
        var (values, categories) = Dimensions(dimensions);
        var layout = Child(element, "layoutPr");
        var totals = Children(Child(layout, "subtotals")).Select(e => Val(e, "val")).OfType<double>().Select(v => (int)v).ToHashSet();
        var series = ExSeries(element, index, values, categories?.Leaf);
        series.Starts = WaterfallStarts(values, totals);
        var (up, down, total) = (_chart.Accents[0], _chart.Accents[1], _chart.Accents[2]);
        for (var i = 0; i < values.Count; i++)
        {
            var color = totals.Contains(i) ? total : (values.Number(i) ?? 0) < 0 ? down : up;
            var own = series.Points.TryGetValue(i, out var point) ? point.Shape : null;
            series.Points[i] = new ChartPoint { Shape = (own ?? new ChartShape()).Over(new ChartShape { Fill = new Paint(color) }).Over(series.Shape) };
        }

        series.LegendKeys = [("Increase", up), ("Decrease", down), ("Total", total)];
        series.AutoColor = up;
        var group = AxisGroup(ChartKind.Bar, element);
        group.Connectors = Bool(Child(layout, "visibility"), "connectorLines") ?? false;
        group.GapWidth = 50;
        group.Series.Add(series);
    }

    /// <summary>Where a waterfall's bars start: at the running total, or on the axis for the totals.</summary>
    internal static double?[] WaterfallStarts(ChartData values, IReadOnlySet<int> totals)
    {
        var starts = new double?[values.Count];
        var running = 0.0;
        for (var i = 0; i < values.Count; i++)
        {
            var value = values.Number(i) ?? 0;
            if (totals.Contains(i))
            {
                starts[i] = 0;
                running = value;
            }
            else
            {
                starts[i] = running;
                running += value;
            }
        }

        return starts;
    }

    /// <summary>
    /// Columns: a histogram when the series bins its values (<c>cx:binning</c>), else a column per category; with a
    /// pareto line, the columns sorted from the largest and the cumulative share as a line on its own percent axis.
    /// </summary>
    private void Columns(XElement element, Dictionary<string, ExDimension>? dimensions, int index, XElement? pareto)
    {
        var (values, categories) = Dimensions(dimensions);
        var labels = categories?.Leaf;
        if (Child(Child(element, "layoutPr"), "binning") is { } binning)
        {
            var numbers = values.Numbers.OfType<double>().ToArray();
            var bins = Bins(numbers, Bin.Read(binning), values.FormatCode);
            labels = new ChartData([.. bins.Select(b => (double?)null)], [.. bins.Select(b => b.Label)], null);
            values = new ChartData([.. bins.Select(b => (double?)b.Count)], null, null);
        }

        var group = AxisGroup(ChartKind.Bar, element);
        group.GapWidth = 0;
        if (pareto is not null)
        {
            // Sorted from the largest (stable), with the cumulative share of the total.
            var order = Enumerable.Range(0, values.Count).OrderByDescending(i => values.Number(i) ?? 0).ToArray();
            values = new ChartData([.. order.Select(values.Number)], null, values.FormatCode);
            if (labels is not null)
            {
                labels = new ChartData([.. order.Select(labels.Number)], labels.Texts is { } t ? [.. order.Select(i => i < t.Length ? t[i] : null)] : null, labels.FormatCode);
            }
        }

        var series = ExSeries(element, index, values, labels);
        group.Series.Add(series);
        if (pareto is null)
        {
            return;
        }

        // On its own (hidden) category axis, so that its percent axis does not take the columns' place.
        var line = AxisGroup(ChartKind.Line, pareto, long.MinValue, 2);
        line.Markers = false;
        var percentAxis = _chart.PlotArea.Axes.First(a => a.Id == line.AxisIds[1]);
        percentAxis.Position = AxisPosition.Right;
        (percentAxis.Min, percentAxis.Max, percentAxis.FormatCode, percentAxis.SourceLinked, percentAxis.Crosses) = (0, 1, "0%", false, AxisCrosses.Max);
        var share = ParetoShares(values);
        var cumulative = ExSeries(pareto, index + 1, new ChartData(share, null, "0%"), labels);
        line.Series.Add(cumulative);
    }

    /// <summary>The cumulative shares of the total of values (sorted from the largest), from 0 to 1.</summary>
    internal static double?[] ParetoShares(ChartData values)
    {
        var total = values.Numbers.Sum(v => Math.Max(v ?? 0, 0));
        var shares = new double?[values.Count];
        var sum = 0.0;
        for (var i = 0; i < shares.Length; i++)
        {
            sum += Math.Max(values.Number(i) ?? 0, 0);
            shares[i] = total > 0 ? sum / total : 0;
        }

        return shares;
    }

    /// <summary>How a histogram bins its values (<c>cx:binning</c>).</summary>
    /// <param name="ClosedRight">Whether bins include their upper bound (<c>(a, b]</c>, the default) or their lower (<c>[a, b)</c>).</param>
    /// <param name="Size">The width of the bins, or <c>null</c>.</param>
    /// <param name="Count">The number of bins, or <c>null</c> (neither: automatic, by Scott's normal reference rule).</param>
    /// <param name="Underflow">The bound of the underflow bin, or <c>null</c>.</param>
    /// <param name="Overflow">The bound of the overflow bin, or <c>null</c>.</param>
    internal sealed record Bin(bool ClosedRight = true, double? Size = null, int? Count = null, double? Underflow = null, double? Overflow = null)
    {
        public static Bin Read(XElement binning) => new(
            Attr(binning, "intervalClosed") != "l",
            Val(Child(binning, "binSize"), "val") is { } size && size > 0 ? size : null,
            Val(Child(binning, "binCount"), "val") is { } count && count >= 1 ? (int)Math.Min(count, MaxBins) : null,
            Val(binning, "underflow"),
            Val(binning, "overflow"));
    }

    /// <summary>
    /// The bins of a histogram with their labels and counts: from the smallest value (or the underflow bound) to the
    /// largest (or the overflow bound) in bins of the given width or number, else of the width of Scott's normal
    /// reference rule (3.5 standard deviations over the cube root of the number of values), as Excel bins them; with
    /// an underflow and an overflow bin when the file asks for them.
    /// </summary>
    internal static List<(string Label, int Count)> Bins(double[] values, Bin bin, string? format)
    {
        var result = new List<(string, int)>();
        if (values.Length == 0)
        {
            return result;
        }

        string F(double v) => ChartNumberFormat.Format(v, format);
        var (min, max) = (values.Min(), values.Max());
        var low = bin.Underflow is { } u && u > min ? u : min;
        var high = bin.Overflow is { } o && o < max ? o : max;
        if (high < low)
        {
            high = low;
        }

        double width;
        if (bin.Count is { } n)
        {
            width = (high - low) / n;
        }
        else if (bin.Size is { } size)
        {
            width = size;
        }
        else
        {
            var mean = values.Average();
            var sd = values.Length > 1 ? Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1)) : 0;
            width = 3.5 * sd / Math.Cbrt(values.Length);
        }

        var count = width > 0 && double.IsFinite(width) ? (int)Math.Clamp(Math.Ceiling((high - low) / width), 1, MaxBins) : 1;
        if (!(width > 0) || !double.IsFinite(width))
        {
            width = 0;
        }
        else if (bin.Size is not null && low + (count * width) < high)
        {
            // Fewer bins than the range needs (the limit): the last takes the rest.
            width = (high - low) / count;
        }

        var counts = new int[count];
        int under = 0, over = 0;
        var underflow = bin.Underflow is not null && low > min;
        var overflow = bin.Overflow is not null && high < max;
        foreach (var v in values)
        {
            if (underflow && (bin.ClosedRight ? v <= low : v < low))
            {
                under++;
            }
            else if (overflow && (bin.ClosedRight ? v > high : v >= high))
            {
                over++;
            }
            else
            {
                var position = width > 0 ? (v - low) / width : 0;
                var k = bin.ClosedRight ? (int)Math.Ceiling(position) - 1 : (int)Math.Floor(position);
                counts[Math.Clamp(k, 0, count - 1)]++;
            }
        }

        if (underflow)
        {
            result.Add(((bin.ClosedRight ? "\u2264" : "<") + F(low), under));
        }

        for (var k = 0; k < count; k++)
        {
            var (a, b) = (low + (k * width), k == count - 1 ? Math.Max(high, low + (k * width)) : low + ((k + 1) * width));
            var open = bin.ClosedRight ? (k == 0 && !underflow ? "[" : "(") : "[";
            var close = bin.ClosedRight ? "]" : (k == count - 1 && !overflow ? "]" : ")");
            result.Add(($"{open}{F(a)}, {F(b)}{close}", counts[k]));
        }

        if (overflow)
        {
            result.Add(((bin.ClosedRight ? ">" : "\u2265") + F(high), over));
        }

        return result;
    }

    /// <summary>
    /// A funnel: a bar per category, centered on the widest, the first at the top; the value axis is hidden and the
    /// values are shown in the bars.
    /// </summary>
    private void Funnel(XElement element, Dictionary<string, ExDimension>? dimensions, int index)
    {
        var (values, categories) = Dimensions(dimensions);
        var series = ExSeries(element, index, values, categories?.Leaf);
        var widest = values.Numbers.Select(v => Math.Abs(v ?? 0)).DefaultIfEmpty(0).Max();
        series.Starts = [.. values.Numbers.Select(v => (double?)((widest - Math.Abs(v ?? 0)) / 2))];
        series.Values = new ChartData([.. values.Numbers.Select(v => v is { } x ? Math.Abs(x) : (double?)null)], null, values.FormatCode);
        series.Labels ??= new ChartDataLabels { ShowValue = true };
        series.Labels.Position ??= "ctr";
        var group = AxisGroup(ChartKind.Bar, element, 0, -1);
        group.Horizontal = true;
        group.Series.Add(series);
        var categoryAxis = _chart.PlotArea.Axes.First(a => a.Id == group.AxisIds[0]);
        (categoryAxis.Position, categoryAxis.Reversed) = (AxisPosition.Left, true);
        var valueAxis = _chart.PlotArea.Axes.First(a => a.Id == group.AxisIds[1]);
        (valueAxis.Position, valueAxis.Min, valueAxis.Max) = (AxisPosition.Bottom, 0, widest > 0 ? widest : 1);
    }

    /// <summary>
    /// Box and whisker plots: the series' values grouped by category (one box per category and series), with the
    /// statistics by the file's quartile method (<c>exclusive</c> by default, or <c>inclusive</c>).
    /// </summary>
    private void BoxWhisker(XElement element, Dictionary<string, ExDimension>? dimensions, int index)
    {
        var (values, categories) = Dimensions(dimensions);
        var layout = Child(element, "layoutPr");
        var visibility = Child(layout, "visibility");
        var inclusive = Attr(Child(layout, "statistics"), "quartileMethod") == "inclusive";
        var group = _chart.PlotArea.Groups.FirstOrDefault(g => g.Kind == ChartKind.BoxWhisker) ?? AxisGroup(ChartKind.BoxWhisker, element);
        group.Box = new BoxOptions(
            Bool(visibility, "meanLine") ?? false,
            Bool(visibility, "meanMarker") ?? true,
            Bool(visibility, "nonoutliers") ?? false,
            Bool(visibility, "outliers") ?? true);

        // The categories of the group in order of appearance, shared by its series.
        var names = group.Series.FirstOrDefault()?.Categories?.Texts?.ToList() ?? [];
        var byCategory = new Dictionary<int, List<double>>();
        for (var i = 0; i < values.Count; i++)
        {
            if (values.Number(i) is not { } value)
            {
                continue;
            }

            var name = categories?.Leaf is { } leaf && i < leaf.Count ? leaf.Texts?[i] ?? (leaf.Number(i) is { } n ? ChartNumberFormat.Format(n, leaf.FormatCode) : string.Empty) : string.Empty;
            var at = names.IndexOf(name);
            if (at < 0)
            {
                at = names.Count;
                names.Add(name);
            }

            (byCategory.TryGetValue(at, out var list) ? list : byCategory[at] = []).Add(value);
        }

        if (names.Count == 0)
        {
            names.Add(string.Empty);
        }

        var labels = new ChartData(new double?[names.Count], [.. names], null);
        foreach (var other in group.Series)
        {
            other.Categories = labels;
        }

        var series = ExSeries(element, index, values, labels);
        series.Boxes = [.. byCategory.OrderBy(p => p.Key).Select(p => Statistics(p.Key, [.. p.Value], inclusive))];
        group.Series.Add(series);
    }

    /// <summary>
    /// The box and whisker statistics of values: quartiles by Excel's QUARTILE.EXC (positions p(n + 1)) or QUARTILE.INC
    /// (positions p(n - 1) + 1), interpolated; whiskers at the furthest values within 1.5 interquartile ranges.
    /// </summary>
    internal static BoxStatistics Statistics(int category, double[] values, bool inclusive)
    {
        Array.Sort(values);
        double Quantile(double p)
        {
            var n = values.Length;
            var position = inclusive ? (p * (n - 1)) + 1 : p * (n + 1);
            position = Math.Clamp(position, 1, n);
            var lower = (int)Math.Floor(position);
            var fraction = position - lower;
            return lower >= n ? values[n - 1] : values[lower - 1] + (fraction * (values[lower] - values[lower - 1]));
        }

        var (q1, median, q3) = (Quantile(0.25), Quantile(0.5), Quantile(0.75));
        var reach = 1.5 * (q3 - q1);
        var low = values.Where(v => v >= q1 - reach).DefaultIfEmpty(q1).Min();
        var high = values.Where(v => v <= q3 + reach).DefaultIfEmpty(q3).Max();
        return new BoxStatistics(category, q1, median, q3, values.Average(), Math.Min(low, q1), Math.Max(high, q3), values);
    }

    /// <summary>
    /// A treemap or sunburst: the categories' levels make the hierarchy (a row's blank inner levels make it a node of
    /// an outer level), the sizes its leaves' values; nodes of the same name under one parent are one node.
    /// </summary>
    private void Hierarchy(XElement element, Dictionary<string, ExDimension>? dimensions, int index, ChartKind kind)
    {
        var (values, categories) = Dimensions(dimensions);
        var series = ExSeries(element, index, values, categories?.Leaf);
        var root = new ChartNode(string.Empty);
        var levels = categories?.Levels ?? [];
        var count = levels.Count == 0 ? values.Count : levels.Max(l => l.Count);
        var previous = new List<string?>();
        for (var i = 0; i < count; i++)
        {
            var path = levels.Select(l => i < l.Count ? l.Texts?[i] ?? (l.Number(i) is { } n ? ChartNumberFormat.Format(n, l.FormatCode) : null) : null).ToList();

            // Blank outer levels repeat the row before (as the cells of a group are often left blank under its name).
            for (var k = 0; k < path.Count && string.IsNullOrEmpty(path[k]) && k < previous.Count && path.Skip(k).Any(p => !string.IsNullOrEmpty(p)); k++)
            {
                path[k] = previous[k];
            }

            previous = path;
            if (values.Number(i) is not { } value || value <= 0)
            {
                continue;
            }

            var node = root;
            path = [.. path];
            while (path.Count > 0 && string.IsNullOrEmpty(path[^1]))
            {
                path.RemoveAt(path.Count - 1);
            }

            if (path.Count == 0)
            {
                path.Add((i + 1).ToString(CultureInfo.InvariantCulture));
            }

            // The inner levels are nodes shared by the rows under them; the innermost named one is the row's leaf.
            for (var level = 0; level < path.Count - 1; level++)
            {
                var text = path[level] ?? string.Empty;
                var child = node.Children.FirstOrDefault(c => c.Name == text && c.Point < 0);
                if (child is null)
                {
                    child = new ChartNode(text);
                    node.Children.Add(child);
                }

                child.Value += value;
                node = child;
            }

            node.Children.Add(new ChartNode(path[^1] ?? string.Empty) { Value = value, Point = i });
            root.Value += value;
        }

        series.Hierarchy = root;
        var colors = root.Children.Count;
        series.LegendKeys = [.. root.Children.Take(1000).Select((c, k) => (c.Name, _chart.StyleColor(k, colors)))];
        var group = new ChartGroup { Kind = kind, VaryColors = true };
        group.ParentLabels = Attr(Child(Child(element, "layoutPr"), "parentLabelLayout"), "val") switch
        {
            "banner" => ParentLabels.Banner,
            "none" => ParentLabels.None,
            _ => ParentLabels.Overlapping,
        };
        series.Labels ??= new ChartDataLabels { ShowCategory = true };
        group.Series.Add(series);
        _chart.PlotArea.Groups.Add(group);
    }
}

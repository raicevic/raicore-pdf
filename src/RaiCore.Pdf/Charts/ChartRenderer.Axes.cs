namespace RaiCore.Pdf.Charts;

/// <summary>
/// Charts with axes: the axes of each chart group (categories or values, primary and secondary), their scales and tick
/// labels, the plot area's layout around them, gridlines, axis lines and tick marks.
/// </summary>
internal sealed partial class ChartRenderer
{
    /// <summary>An axis as laid out: its role, scale or categories, labels and position.</summary>
    private sealed class AxisLayout
    {
        public required ChartAxis Axis { get; init; }

        /// <summary>Whether the axis runs across (its labels below or above the plot area).</summary>
        public bool Horizontal { get; set; }

        /// <summary>Whether the axis shows categories rather than values.</summary>
        public bool Category { get; set; }

        /// <summary>The groups plotted on the axis.</summary>
        public List<ChartGroup> Groups { get; } = [];

        /// <summary>The number of categories.</summary>
        public int Count { get; set; }

        /// <summary>The category labels.</summary>
        public string[] CategoryLabels { get; set; } = [];

        /// <summary>Whether categories sit in bands between ticks (rather than on them).</summary>
        public bool Between { get; set; } = true;

        /// <summary>The scale of a value axis.</summary>
        public AxisScale Scale { get; set; } = new(0, 1, 0.2, 0.04);

        /// <summary>The number format of value labels.</summary>
        public string? Format { get; set; }

        /// <summary>The axis crossing this one.</summary>
        public AxisLayout? Cross { get; set; }

        /// <summary>The chart position of fraction 0 along the axis (the left, or the bottom).</summary>
        public double Start { get; set; }

        /// <summary>The chart position of fraction 1 along the axis (the right, or the top).</summary>
        public double End { get; set; }

        /// <summary>The tick labels: fraction along the axis and text block.</summary>
        public List<(double Fraction, TextBlock Block)> Labels { get; } = [];

        /// <summary>The title, laid out, or <c>null</c>.</summary>
        public TextBlock? Title { get; set; }

        /// <summary>Whether labels and title are drawn (the axis is not deleted).</summary>
        public bool Visible => !Axis.Deleted;

        /// <summary>Whether the axis is on the far side: right of the plot area, or above it.</summary>
        public bool FarSide => Horizontal ? Axis.Position == AxisPosition.Top : Axis.Position == AxisPosition.Right;

        /// <summary>The chart position of a fraction along the axis (reversed axes run backwards).</summary>
        public double Position(double fraction) => Start + ((Axis.Reversed ? 1 - fraction : fraction) * (End - Start));

        /// <summary>The fraction of a category's center.</summary>
        public double CategoryFraction(double index) =>
            Between ? (index + 0.5) / Math.Max(Count, 1) : Count > 1 ? index / (Count - 1) : 0.5;

        /// <summary>The width of a category band as a fraction of the axis.</summary>
        public double BandFraction => Between ? 1.0 / Math.Max(Count, 1) : Count > 1 ? 1.0 / (Count - 1) : 1;

        /// <summary>The fraction of a value (for value axes).</summary>
        public double ValueFraction(double value) => Scale.Fraction(value);

        /// <summary>The length of the axis in points.</summary>
        public double Length => Math.Abs(End - Start);
    }

    /// <summary>Draws a chart with axes into its plot area (the outer box, or the inner one the file fixes).</summary>
    private void DrawAxisChart(Box outer, Box? fixedInner)
    {
        var axes = BindAxes();
        if (axes.Count == 0)
        {
            return;
        }

        // Lay out twice: the scales depend on the axes' lengths, which depend on the labels' sizes.
        var inner = fixedInner ?? outer;
        for (var pass = 0; pass < 2; pass++)
        {
            Position(axes, inner);
            foreach (var axis in axes)
            {
                LabelAxis(axis);
            }

            if (fixedInner is null)
            {
                var (left, top, right, bottom) = Extents(axes);
                inner = outer.Deflate(left, top, right, bottom);
            }
        }

        Position(axes, inner);
        foreach (var axis in axes)
        {
            // The final labels for the final lengths (scales stay as laid out).
            LabelAxis(axis, rescale: false);
        }

        Paint(_chart.PlotArea.Shape, null, null, ThinLine, () => Rect(inner));
        foreach (var axis in axes)
        {
            DrawGridlines(axis, inner, minor: true);
        }

        foreach (var axis in axes)
        {
            DrawGridlines(axis, inner, minor: false);
        }

        DrawPlots(axes, inner);
        foreach (var axis in axes.Where(a => a.Visible))
        {
            DrawAxis(axis, inner);
        }

        DrawAxisDataLabels(axes, inner);
    }

    /// <summary>
    /// The axes the chart groups use, each group's category (or X) axis and value (or Y) axis paired; axes a group
    /// names but the file lacks are made up with default settings.
    /// </summary>
    private List<AxisLayout> BindAxes()
    {
        var byId = new Dictionary<long, AxisLayout>();
        var list = new List<AxisLayout>();
        var synthetic = -1L;
        AxisLayout Get(long? id, bool horizontal, bool category)
        {
            if (id is { } key && byId.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var axis = id is { } k ? _chart.PlotArea.Axes.FirstOrDefault(a => a.Id == k) : null;
            axis ??= new ChartAxis
            {
                Id = id ?? synthetic--,
                Kind = category ? AxisKind.Category : AxisKind.Value,
                Position = horizontal ? AxisPosition.Bottom : AxisPosition.Left,
                MajorGridlines = category ? null : new ChartShape(),
                MajorTicks = TickMark.Outside,
            };
            var layout = new AxisLayout { Axis = axis, Horizontal = horizontal, Category = category };
            byId[axis.Id] = layout;
            list.Add(layout);
            return layout;
        }

        foreach (var group in _chart.PlotArea.Groups.Where(g => g.Kind is not (ChartKind.Pie or ChartKind.Doughnut or ChartKind.Radar or ChartKind.Treemap or ChartKind.Sunburst)))
        {
            var xy = group.Kind is ChartKind.Scatter or ChartKind.Bubble;
            var categoryHorizontal = !(group.Kind == ChartKind.Bar && group.Horizontal);
            long? firstId = group.AxisIds.Count > 0 ? group.AxisIds[0] : null;
            long? secondId = group.AxisIds.Count > 1 ? group.AxisIds[1] : null;
            var first = Get(firstId, categoryHorizontal, category: !xy);
            var second = Get(secondId, !categoryHorizontal, category: false);
            if (ReferenceEquals(first, second))
            {
                second = Get(null, !categoryHorizontal, category: false);
            }

            first.Horizontal = categoryHorizontal;
            first.Category = !xy;
            second.Horizontal = !categoryHorizontal;
            second.Category = false;
            first.Cross = second;
            second.Cross = first;
            first.Groups.Add(group);
            second.Groups.Add(group);
            if (!xy)
            {
                // Bars sit between ticks; lines and areas follow the value axis's crossBetween.
                first.Between = group.Kind is ChartKind.Bar or ChartKind.BoxWhisker || second.Axis.CrossBetween;
            }
        }

        foreach (var axis in list.Where(a => a.Category))
        {
            Categories(axis);
        }

        return list;
    }

    /// <summary>The number and labels of a category axis's categories.</summary>
    private void Categories(AxisLayout axis)
    {
        var series = axis.Groups.SelectMany(g => g.Series).ToList();
        axis.Count = Math.Max(1, series.Count == 0 ? 1 : series.Max(s => s.Boxes is not null ? s.Categories?.Count ?? 1 : Math.Max(s.Values.Count, s.Categories?.Count ?? 0)));
        var source = series.FirstOrDefault(s => s.Categories is { Count: > 0 })?.Categories;
        var format = !axis.Axis.SourceLinked && axis.Axis.FormatCode is { } own ? own : source?.FormatCode ?? axis.Axis.FormatCode;
        var labels = new string[axis.Count];
        for (var i = 0; i < labels.Length; i++)
        {
            labels[i] = source is null || i >= source.Count
                ? source is null ? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty
                : source.Texts?[i] is { } text && !(source.Number(i) is { } n && axis.Axis.Kind == AxisKind.Date)
                    ? text
                    : source.Number(i) is { } number ? ChartNumberFormat.Format(number, format, _chart.Date1904) : source.Texts?[i] ?? string.Empty;
        }

        axis.CategoryLabels = labels;
    }

    /// <summary>Puts each axis along the plot area's inner box.</summary>
    private static void Position(List<AxisLayout> axes, Box inner)
    {
        foreach (var axis in axes)
        {
            (axis.Start, axis.End) = axis.Horizontal ? (inner.X, inner.Right) : (inner.Bottom, inner.Y);
        }
    }

    /// <summary>The range of the values plotted on a value axis.</summary>
    private static (double Min, double Max, bool Percent) Range(AxisLayout axis)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        var percent = false;
        void Add(double v)
        {
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        foreach (var group in axis.Groups)
        {
            var xAxis = axis.Horizontal;
            var xy = group.Kind is ChartKind.Scatter or ChartKind.Bubble;
            if (xy)
            {
                // The first axis of a scatter group is X (it is horizontal); the second is Y.
                foreach (var series in group.Series)
                {
                    var data = xAxis ? XValues(series) : series.Values.Numbers;
                    foreach (var v in data)
                    {
                        if (v is { } value)
                        {
                            Add(value);
                        }
                    }
                }

                continue;
            }

            if (group.Grouping == ChartGrouping.PercentStacked && group.Kind != ChartKind.Stock)
            {
                percent = true;
                var (lo, hi) = PercentRange(group);
                Add(lo);
                Add(hi);
            }
            else if (group.Grouping == ChartGrouping.Stacked && group.Kind != ChartKind.Stock)
            {
                var count = group.Series.Count == 0 ? 0 : group.Series.Max(s => s.Values.Count);
                for (var i = 0; i < count; i++)
                {
                    double positive = 0, negative = 0;
                    foreach (var series in group.Series)
                    {
                        var v = series.Values.Number(i) ?? 0;
                        if (v >= 0)
                        {
                            positive += v;
                            Add(positive);
                        }
                        else
                        {
                            negative += v;
                            Add(negative);
                        }
                    }
                }
            }
            else
            {
                foreach (var series in group.Series)
                {
                    for (var i = 0; i < series.Values.Count; i++)
                    {
                        if (series.Values.Number(i) is not { } value)
                        {
                            continue;
                        }

                        // Floating bars reach from their start; box plots from their smallest value to their largest.
                        var start = series.Starts is { } starts && i < starts.Length ? starts[i] ?? 0 : 0;
                        Add(start + value);
                        if (series.Starts is not null)
                        {
                            Add(start);
                        }
                    }
                }
            }
        }

        return double.IsFinite(min) ? (min, max, percent) : (double.NaN, double.NaN, percent);
    }

    /// <summary>The range of a percent-stacked group: 0 to 1, below 0 for negative values.</summary>
    private static (double Min, double Max) PercentRange(ChartGroup group)
    {
        var count = group.Series.Count == 0 ? 0 : group.Series.Max(s => s.Values.Count);
        double lo = 0, hi = 0;
        for (var i = 0; i < count; i++)
        {
            var total = group.Series.Sum(s => Math.Abs(s.Values.Number(i) ?? 0));
            if (total <= 0)
            {
                continue;
            }

            hi = Math.Max(hi, group.Series.Sum(s => Math.Max(s.Values.Number(i) ?? 0, 0)) / total);
            lo = Math.Min(lo, group.Series.Sum(s => Math.Min(s.Values.Number(i) ?? 0, 0)) / total);
        }

        return (lo, hi == 0 && lo == 0 ? 1 : hi);
    }

    /// <summary>The X values of a scatter or bubble series: its numbers, or 1, 2, 3... for text or missing ones.</summary>
    private static double?[] XValues(ChartSeries series)
    {
        if (series.Categories is { IsText: false } x && x.Numbers.Any(n => n is not null))
        {
            return x.Numbers;
        }

        var count = Math.Max(series.Values.Count, series.Categories?.Count ?? 0);
        return [.. Enumerable.Range(1, count).Select(i => (double?)i)];
    }

    /// <summary>The font size of an axis's tick labels.</summary>
    private ChartTextFormat LabelFormat(AxisLayout axis) => Resolve(axis.Axis.Text);

    /// <summary>Makes an axis's scale (for value axes) and tick labels for its current length.</summary>
    private void LabelAxis(AxisLayout axis, bool rescale = true)
    {
        axis.Labels.Clear();
        var format = LabelFormat(axis);
        var size = format.Size ?? 10;
        var length = Math.Max(axis.Length, 1);
        var autoRotation = format.Rotation is null;
        if (axis.Category)
        {
            LabelCategories(axis, format, length, autoRotation);
        }
        else
        {
            if (rescale)
            {
                var (min, max, percent) = Range(axis);
                var a = axis.Axis;
                var source = axis.Groups.SelectMany(g => g.Series).FirstOrDefault()?.Values.FormatCode;
                axis.Format = !a.SourceLinked && a.FormatCode is { } own ? own : percent ? "0%" : source ?? a.FormatCode;
                var maxIntervals = axis.Horizontal ? AxisScale.MaxIntervals : (int)Math.Floor(length / (size * 1.5));
                // A percent-stacked axis runs from 0% (or -100%) to 100% unless the file says otherwise.
                var (fixedMin, fixedMax) = percent && a.LogBase is null
                    ? (a.Min ?? (min < 0 ? -1 : 0), a.Max ?? (max > 0 ? 1 : 0))
                    : (a.Min, a.Max);
                if (fixedMin >= fixedMax)
                {
                    (fixedMin, fixedMax) = (a.Min, a.Max);
                }

                axis.Scale = AxisScale.Automatic(min, max, fixedMin, fixedMax, a.MajorUnit, a.MinorUnit, a.LogBase, maxIntervals);
                if (axis.Horizontal)
                {
                    // Across, the labels' widths decide how many intervals fit.
                    for (var attempt = 0; attempt < 4; attempt++)
                    {
                        var widest = axis.Scale.MajorTicks().DefaultIfEmpty(0).Max(t => Text(ChartNumberFormat.Format(t, axis.Format, _chart.Date1904), format).Width);
                        var intervals = Math.Max(1, axis.Scale.MajorTicks().Count - 1);
                        var room = (int)Math.Floor(length / (widest + size));
                        if (room >= intervals || a.MajorUnit is not null)
                        {
                            break;
                        }

                        axis.Scale = AxisScale.Automatic(min, max, fixedMin, fixedMax, a.MajorUnit, a.MinorUnit, a.LogBase, Math.Max(room, 1));
                    }
                }
            }

            if (axis.Axis.Labels != TickLabelPosition.None)
            {
                foreach (var tick in axis.Scale.MajorTicks())
                {
                    var block = Text(ChartNumberFormat.Format(tick, axis.Format, _chart.Date1904), format);
                    axis.Labels.Add((axis.ValueFraction(tick), block));
                }
            }
        }

        if (axis.Axis.Title is { } title && title.Text is { Length: > 0 } titleText && axis.Visible)
        {
            var titleFormat = Resolve(title.Format, 1, bold: true);
            if (titleFormat.Rotation is null && !axis.Horizontal)
            {
                titleFormat = titleFormat with { Rotation = 90 };
            }

            // Wrapped to the axis's length: across for a horizontal axis, along a vertical one when turned.
            var turned = Math.Abs(Math.Abs(titleFormat.Rotation ?? 0) - 90) < 1;
            axis.Title = Wrap(titleText, titleFormat, axis.Horizontal || turned ? length : _width * 0.3);
        }
        else
        {
            axis.Title = null;
        }
    }

    /// <summary>The labels of a category axis: straight, wrapped, turned 45 degrees or thinned out to fit their bands.</summary>
    private void LabelCategories(AxisLayout axis, ChartTextFormat format, double length, bool autoRotation)
    {
        if (axis.Axis.Labels == TickLabelPosition.None || axis.Count == 0)
        {
            return;
        }

        var band = length * axis.BandFraction;
        var size = format.Size ?? 10;
        var blocks = axis.CategoryLabels.Select(l => Text(l, format)).ToList();
        var skip = axis.Axis.LabelSkip ?? 1;
        if (axis.Horizontal && autoRotation)
        {
            var widest = blocks.Count == 0 ? 0 : blocks.Max(b => b.Width);
            if (widest > band * 0.95 && axis.Axis.LabelSkip is null)
            {
                // Wrap at spaces when every word fits; else turn the labels; else label every nth category.
                var wrapped = axis.CategoryLabels.Select(l => Wrap(l, format, band * 0.95)).ToList();
                if (wrapped.All(b => b.Width <= band * 0.95 && b.Lines.Length <= 3))
                {
                    blocks = wrapped;
                }
                else
                {
                    foreach (var block in blocks)
                    {
                        block.Rotation = 45;
                    }

                    var pitch = size * 1.2 / Math.Sin(Math.PI / 4);
                    skip = Math.Max(1, (int)Math.Ceiling(pitch / Math.Max(band, 0.01)));
                }
            }
        }
        else if (!axis.Horizontal && axis.Axis.LabelSkip is null)
        {
            skip = Math.Max(1, (int)Math.Ceiling(size * 1.2 / Math.Max(band, 0.01)));
        }
        else if (axis.Horizontal && axis.Axis.LabelSkip is null && !autoRotation)
        {
            var pitch = blocks.Count == 0 ? 0 : blocks.Max(b => b.TurnedWidth) + (size * 0.3);
            skip = Math.Max(1, (int)Math.Ceiling(pitch / Math.Max(band, 0.01)));
        }

        skip = Math.Clamp(skip, 1, Math.Max(axis.Count, 1));
        for (var i = 0; i < blocks.Count; i += skip)
        {
            axis.Labels.Add((axis.CategoryFraction(i), blocks[i]));
        }
    }

    /// <summary>The fraction along the crossing axis where an axis's line is.</summary>
    private static double CrossFraction(AxisLayout axis)
    {
        if (axis.Cross is not { } cross)
        {
            return 0;
        }

        var a = axis.Axis;
        if (cross.Category)
        {
            // A value axis crossing categories: at the first category, the last, or at a category number.
            if (a.CrossesAt is { } at)
            {
                return Math.Clamp(cross.Between ? (at - 1) / Math.Max(cross.Count, 1) : cross.Count > 1 ? (at - 1) / (cross.Count - 1) : 0, 0, 1);
            }

            return a.Crosses == AxisCrosses.Max ? 1 : 0;
        }

        var scale = cross.Scale;
        var value = a.CrossesAt ?? a.Crosses switch
        {
            AxisCrosses.Max => scale.Max,
            AxisCrosses.Min => scale.Min,
            _ => scale.LogBase is null ? Math.Clamp(0, scale.Min, scale.Max) : scale.Min,
        };
        var fraction = scale.Fraction(value);
        return double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
    }

    /// <summary>The chart position (across the axis) of an axis's line.</summary>
    private static double LinePosition(AxisLayout axis) => axis.Cross is { } cross ? cross.Position(CrossFraction(axis)) : 0;

    /// <summary>
    /// Where an axis's labels go: the chart position across the axis they start from, and whether they extend toward
    /// the far side (right or up) rather than the near one.
    /// </summary>
    private static (double Edge, bool Far) LabelEdge(AxisLayout axis)
    {
        var cross = axis.Cross;
        var line = LinePosition(axis);
        if (cross is null)
        {
            return (line, axis.FarSide);
        }

        var low = cross.Position(0);
        var high = cross.Position(1);
        return axis.Axis.Labels switch
        {
            TickLabelPosition.Low => (low, axis.Horizontal ? low < high : low > high),
            TickLabelPosition.High => (high, axis.Horizontal ? high < low : high > low),
            _ when axis.FarSide => (line, true),
            _ => (line, false),
        };
    }

    /// <summary>The space each side of the plot area needs for the axes' tick marks, labels and titles.</summary>
    private (double Left, double Top, double Right, double Bottom) Extents(List<AxisLayout> axes)
    {
        double left = 0, top = 0, right = 0, bottom = 0;
        foreach (var axis in axes.Where(a => a.Visible))
        {
            var size = LabelFormat(axis).Size ?? 10;
            var ticks = axis.Axis.MajorTicks is TickMark.Outside or TickMark.Cross ? TickLength : 0;
            var labels = axis.Labels.Count == 0 ? 0 : axis.Labels.Max(l => axis.Horizontal ? l.Block.TurnedHeight : l.Block.TurnedWidth);
            var (_, far) = LabelEdge(axis);
            var atEdge = axis.Axis.Labels != TickLabelPosition.NextTo || IsAtEdge(axis);
            var extent = (atEdge ? ticks + (labels > 0 ? (size * 0.3) + labels : 0) : 0)
                + (axis.Title is { } title ? (axis.Horizontal ? title.TurnedHeight : title.TurnedWidth) + (size * 0.5) : 0);
            if (axis.Horizontal)
            {
                if (far)
                {
                    top = Math.Max(top, extent);
                }
                else
                {
                    bottom = Math.Max(bottom, extent);
                }
            }
            else if (far)
            {
                right = Math.Max(right, extent);
            }
            else
            {
                left = Math.Max(left, extent);
            }
        }

        // Keep at least half of the area for the plot.
        var maxSide = Math.Min(_width, _height) * 0.4;
        return (Math.Min(left, maxSide), Math.Min(top, maxSide), Math.Min(right, maxSide), Math.Min(bottom, maxSide));
    }

    /// <summary>Whether an axis's line is at an edge of the plot area (so its labels are outside it).</summary>
    private static bool IsAtEdge(AxisLayout axis)
    {
        var fraction = CrossFraction(axis);
        return fraction <= 1e-9 || fraction >= 1 - 1e-9;
    }

    /// <summary>Draws an axis's major or minor gridlines across the plot area.</summary>
    private void DrawGridlines(AxisLayout axis, Box inner, bool minor)
    {
        var shape = minor ? axis.Axis.MinorGridlines : axis.Axis.MajorGridlines;
        if (shape is null)
        {
            return;
        }

        var fractions = new List<double>();
        if (axis.Category)
        {
            var count = axis.Between ? axis.Count + 1 : axis.Count;
            for (var i = 0; i < count && i <= 10000; i++)
            {
                fractions.Add(axis.Between ? i / (double)Math.Max(axis.Count, 1) : axis.CategoryFraction(i));
            }

            if (minor)
            {
                return;
            }
        }
        else
        {
            fractions.AddRange((minor ? axis.Scale.MinorTicks() : axis.Scale.MajorTicks()).Select(axis.ValueFraction));
        }

        var color = _chart.GridColor;
        foreach (var fraction in fractions.Where(f => f is >= -1e-9 and <= 1 + 1e-9))
        {
            var p = axis.Position(fraction);
            if (axis.Horizontal)
            {
                Line(shape, color, ThinLine, p, inner.Y, p, inner.Bottom);
            }
            else
            {
                Line(shape, color, ThinLine, inner.X, p, inner.Right, p);
            }
        }
    }

    /// <summary>Draws an axis: its line, tick marks, tick labels and title.</summary>
    private void DrawAxis(AxisLayout axis, Box inner)
    {
        var line = LinePosition(axis);
        var a = axis.Axis;
        var lineColor = a.Line?.Line is { } paint ? paint.Color : _chart.AxisColor;
        var lineWidth = a.Line?.LineWidth ?? ThinLine;
        if (lineColor is not null)
        {
            if (axis.Horizontal)
            {
                Line(a.Line, _chart.AxisColor, ThinLine, inner.X, line, inner.Right, line);
            }
            else
            {
                Line(a.Line, _chart.AxisColor, ThinLine, line, inner.Y, line, inner.Bottom);
            }
        }

        var (edge, far) = LabelEdge(axis);

        // Tick marks: outside points to where the labels are.
        if (lineColor is { } tickColor)
        {
            DrawTicks(axis, a.MajorTicks, line, far, tickColor, lineWidth, minor: false);
            DrawTicks(axis, a.MinorTicks, line, far, tickColor, lineWidth, minor: true);
        }

        var outsideTicks = a.MajorTicks is TickMark.Outside or TickMark.Cross && Math.Abs(edge - line) < 0.5 ? TickLength : 0;
        var size = LabelFormat(axis).Size ?? 10;
        var gap = outsideTicks + (size * 0.3);
        var direction = far ? -1 : 1;
        if (!axis.Horizontal)
        {
            direction = far ? 1 : -1;
        }

        var labelsExtent = 0.0;
        foreach (var (fraction, block) in axis.Labels)
        {
            if (fraction is < -1e-9 or > 1 + 1e-9)
            {
                continue;
            }

            var p = axis.Position(fraction);
            if (axis.Horizontal)
            {
                var y = edge + (direction * gap);
                var turned = Math.Abs(block.Rotation) > 0.01;
                var alignX = turned ? block.Rotation > 0 ? 1 : 0 : 0.5;
                DrawText(block, p, y, alignX, far ? 1 : 0);
                labelsExtent = Math.Max(labelsExtent, block.TurnedHeight);
            }
            else
            {
                var x = edge + (direction * gap);
                DrawText(block, x, p, far ? 0 : 1, 0.5);
                labelsExtent = Math.Max(labelsExtent, block.TurnedWidth);
            }
        }

        if (axis.Title is { } title)
        {
            var offset = gap + labelsExtent + (size * 0.5);
            if (axis.Horizontal)
            {
                var y = edge + (direction * offset);
                DrawText(title, (inner.X + inner.Right) / 2, Math.Clamp(y, 0, _height), 0.5, far ? 1 : 0);
            }
            else
            {
                var x = edge + (direction * offset);
                DrawText(title, Math.Clamp(x, 0, _width), (inner.Y + inner.Bottom) / 2, far ? 0 : 1, 0.5);
            }
        }
    }

    /// <summary>Draws the major or minor tick marks of an axis.</summary>
    private void DrawTicks(AxisLayout axis, TickMark mark, double line, bool far, PdfColor color, double width, bool minor)
    {
        if (mark == TickMark.None)
        {
            return;
        }

        var fractions = new List<double>();
        if (axis.Category)
        {
            if (minor)
            {
                return;
            }

            var count = axis.Between ? axis.Count + 1 : axis.Count;
            var step = Math.Max(1, (int)Math.Ceiling(count / 2000.0));
            for (var i = 0; i < count; i += step)
            {
                fractions.Add(axis.Between ? i / (double)Math.Max(axis.Count, 1) : axis.CategoryFraction(i));
            }
        }
        else
        {
            fractions.AddRange((minor ? axis.Scale.MinorTicks() : axis.Scale.MajorTicks()).Select(axis.ValueFraction));
        }

        var length = minor ? TickLength / 2 : TickLength;

        // The side away from the plot is where the labels are (outside), or the other one (inside).
        var outward = axis.Horizontal ? (far ? -1 : 1) : (far ? 1 : -1);
        var (from, to) = mark switch
        {
            TickMark.Outside => (0, outward * length),
            TickMark.Inside => (0, -outward * length),
            _ => (-length, length),
        };
        SetStroke(color, width, null);
        foreach (var fraction in fractions.Where(f => f is >= -1e-9 and <= 1 + 1e-9))
        {
            var p = axis.Position(fraction);
            if (axis.Horizontal)
            {
                MoveTo(p, line + from);
                LineTo(p, line + to);
            }
            else
            {
                MoveTo(line + from, p);
                LineTo(line + to, p);
            }
        }

        _canvas.Stroke();
    }
}

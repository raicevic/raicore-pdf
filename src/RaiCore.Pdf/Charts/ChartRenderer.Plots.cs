namespace RaiCore.Pdf.Charts;

/// <summary>The series of charts with axes: bars, lines, areas, scatter points, bubbles and stock charts, and their data labels.</summary>
internal sealed partial class ChartRenderer
{
    /// <summary>The marker size no format gives.</summary>
    private const double DefaultMarkerSize = 7;

    /// <summary>Where a data label goes: a point or a bar, with what it shows.</summary>
    private sealed record LabelSpot(ChartGroup Group, ChartSeries Series, int Index, double X, double Y, Box? Bar, bool Negative, bool Horizontal, double Value, double? Size);

    private readonly List<LabelSpot> _labelSpots = [];

    /// <summary>The order Office draws chart types in: areas behind bars, bars behind lines, lines behind points.</summary>
    private static int DrawOrder(ChartKind kind) => kind switch
    {
        ChartKind.Area => 0,
        ChartKind.Bar => 1,
        ChartKind.Stock => 2,
        ChartKind.Line => 3,
        ChartKind.Scatter => 4,
        _ => 5,
    };

    /// <summary>Draws the series of every chart group with axes.</summary>
    private void DrawPlots(List<AxisLayout> axes, Box inner)
    {
        foreach (var group in _chart.PlotArea.Groups.OrderBy(g => DrawOrder(g.Kind)))
        {
            var first = axes.FirstOrDefault(a => a.Groups.Contains(group) && (a.Category || a.Horizontal == !(group.Kind == ChartKind.Bar && group.Horizontal)));
            var second = first?.Cross;
            if (first is null || second is null)
            {
                continue;
            }

            ResourceLimits.Checkpoint();
            _canvas.SaveState();
            switch (group.Kind)
            {
                case ChartKind.Bar:
                    ClipTo(inner);
                    DrawBars(group, first, second);
                    break;
                case ChartKind.Area:
                    ClipTo(inner);
                    DrawAreas(group, first, second);
                    break;
                case ChartKind.Line or ChartKind.Stock:
                    ClipTo(inner.Deflate(-8, -8, -8, -8));
                    DrawLines(group, first, second);
                    break;
                case ChartKind.Scatter or ChartKind.Bubble:
                    ClipTo(inner.Deflate(-8, -8, -8, -8));
                    DrawScatter(group, first, second);
                    break;
                case ChartKind.BoxWhisker:
                    ClipTo(inner.Deflate(-8, -8, -8, -8));
                    DrawBoxes(group, first, second);
                    break;
            }

            _canvas.RestoreState();
        }
    }

    /// <summary>The value at which a series' point is drawn: its own, stacked on the series before it, or as a share of 100%.</summary>
    private static double?[] Plotted(ChartGroup group, int seriesIndex, out double?[] below)
    {
        var series = group.Series[seriesIndex];
        var count = series.Values.Count;
        var top = new double?[count];
        below = new double?[count];
        for (var i = 0; i < count; i++)
        {
            var value = series.Values.Number(i);
            if (series.Starts is { } starts)
            {
                // A floating bar: from its start to its start plus its value.
                below[i] = i < starts.Length ? starts[i] ?? 0 : 0;
                top[i] = value is null ? null : below[i] + value;
                continue;
            }

            if (group.Grouping == ChartGrouping.Standard || group.Kind == ChartKind.Stock)
            {
                top[i] = value;
                continue;
            }

            // Positive values stack upwards and negative ones downwards, each on the series before.
            double positive = 0, negative = 0;
            for (var k = 0; k < seriesIndex; k++)
            {
                var v = group.Series[k].Values.Number(i) ?? 0;
                if (v >= 0)
                {
                    positive += v;
                }
                else
                {
                    negative += v;
                }
            }

            var own = value ?? 0;
            var start = own >= 0 ? positive : negative;
            var end = start + own;
            if (group.Grouping == ChartGrouping.PercentStacked)
            {
                var total = group.Series.Sum(s => Math.Abs(s.Values.Number(i) ?? 0));
                (start, end) = total > 0 ? (start / total, end / total) : (0, 0);
            }

            below[i] = start;
            top[i] = value is null ? null : end;
        }

        return top;
    }

    /// <summary>The automatic fill of a point: the series' color, or the point's own when the group varies colors.</summary>
    private PdfColor AutoColor(ChartGroup group, ChartSeries series, int index, int count) =>
        group.VaryColors && (group.Series.Count == 1 || group.Kind is ChartKind.Pie or ChartKind.Doughnut)
            ? _chart.StyleColor(index, count)
            : series.AutoColor;

    /// <summary>A point's shape over its series' shape.</summary>
    private static ChartShape PointShape(ChartSeries series, int index) =>
        series.Points.TryGetValue(index, out var point) && point.Shape is { } shape ? shape.Over(series.Shape) : series.Shape;

    /// <summary>Draws bars or columns: clustered side by side, or stacked.</summary>
    private void DrawBars(ChartGroup group, AxisLayout categories, AxisLayout values)
    {
        var stacked = group.Grouping != ChartGrouping.Standard;
        var n = stacked ? 1 : Math.Max(group.Series.Count, 1);
        var gap = group.GapWidth / 100;
        var overlap = group.Overlap / 100;
        var band = categories.BandFraction;
        var bar = band / (n - ((n - 1) * overlap) + gap);
        var baseFraction = CrossFraction(categories);
        for (var s = 0; s < group.Series.Count; s++)
        {
            var series = group.Series[s];
            var tops = Plotted(group, s, out var bottoms);
            var slot = stacked ? 0 : s;
            var floating = series.Starts is not null;
            (double Edge, double Level)? previous = null;
            for (var i = 0; i < tops.Length && i < categories.Count; i++)
            {
                if (tops[i] is not { } top)
                {
                    continue;
                }

                var start = categories.CategoryFraction(i) - (band / 2) + (gap * bar / 2) + (slot * bar * (1 - overlap));
                var from = stacked || floating ? values.ValueFraction(bottoms[i] ?? 0) : baseFraction;
                var to = values.ValueFraction(top);
                if (!double.IsFinite(from) || !double.IsFinite(to))
                {
                    continue;
                }

                // Keep the rectangle bounded; the plot area clips it.
                (from, to) = (Math.Clamp(from, -2, 3), Math.Clamp(to, -2, 3));
                var (c0, c1) = (categories.Position(start), categories.Position(start + bar));
                var (v0, v1) = (values.Position(from), values.Position(to));
                var box = categories.Horizontal
                    ? new Box(Math.Min(c0, c1), Math.Min(v0, v1), Math.Abs(c1 - c0), Math.Abs(v1 - v0))
                    : new Box(Math.Min(v0, v1), Math.Min(c0, c1), Math.Abs(v1 - v0), Math.Abs(c1 - c0));
                var value = series.Values.Number(i) ?? 0;
                var shape = PointShape(series, i);
                var inverted = value < 0 && (series.Points.TryGetValue(i, out var p) ? p.InvertIfNegative ?? series.InvertIfNegative : series.InvertIfNegative);
                var auto = AutoColor(group, series, i, categories.Count);
                if (inverted && shape.Fill is null)
                {
                    Paint(shape, PdfColor.White, auto, ThinLine, () => Rect(box));
                }
                else
                {
                    Paint(shape, auto, null, ThinLine, () => Rect(box));
                }

                var negative = categories.Horizontal ? v1 > v0 : v1 < v0;
                _labelSpots.Add(new LabelSpot(group, series, i, box.CenterX, box.CenterY, box, negative, !categories.Horizontal, value, null));
                if (group.Connectors && floating)
                {
                    // A waterfall's connector: from the end of the bar before, across to this one, at that end's level.
                    var (near, far) = (Math.Min(c0, c1), Math.Max(c0, c1));
                    if (previous is { } before)
                    {
                        SetStroke(_chart.AxisColor, ThinLine, null);
                        if (categories.Horizontal)
                        {
                            MoveTo(before.Edge, before.Level);
                            LineTo(near, before.Level);
                        }
                        else
                        {
                            MoveTo(before.Level, before.Edge);
                            LineTo(before.Level, near);
                        }

                        _canvas.Stroke();
                    }

                    previous = (far, v1);
                }
            }
        }
    }

    /// <summary>The chart position of a category (or X value) and of a value.</summary>
    private static (double X, double Y) Point(AxisLayout categories, AxisLayout values, double categoryFraction, double value)
    {
        var c = categories.Position(categoryFraction);
        var v = values.Position(Math.Clamp(values.ValueFraction(value), -2, 3));
        return categories.Horizontal ? (c, v) : (v, c);
    }

    /// <summary>Draws areas: standard ones from the axis, stacked ones on the series before.</summary>
    private void DrawAreas(ChartGroup group, AxisLayout categories, AxisLayout values)
    {
        var baseFraction = CrossFraction(categories);
        var baseValue = values.Scale.ValueAt(baseFraction);
        for (var s = 0; s < group.Series.Count; s++)
        {
            var series = group.Series[s];
            var tops = Plotted(group, s, out var bottoms);
            var count = Math.Min(tops.Length, categories.Count);
            if (count == 0)
            {
                continue;
            }

            var stacked = group.Grouping != ChartGrouping.Standard;
            var upper = new List<(double X, double Y)>();
            var lower = new List<(double X, double Y)>();
            for (var i = 0; i < count; i++)
            {
                var f = categories.CategoryFraction(i);
                upper.Add(Point(categories, values, f, tops[i] ?? (stacked ? bottoms[i] ?? 0 : baseValue)));
                lower.Add(Point(categories, values, f, stacked ? bottoms[i] ?? 0 : baseValue));
                if (series.Values.Number(i) is { } v)
                {
                    var (x, y) = upper[^1];
                    _labelSpots.Add(new LabelSpot(group, series, i, x, y, null, v < 0, false, v, null));
                }
            }

            Paint(series.Shape, series.AutoColor, null, ThinLine, () =>
            {
                MoveTo(upper[0].X, upper[0].Y);
                foreach (var (x, y) in upper.Skip(1))
                {
                    LineTo(x, y);
                }

                for (var i = lower.Count - 1; i >= 0; i--)
                {
                    LineTo(lower[i].X, lower[i].Y);
                }

                _canvas.ClosePath();
            });
        }
    }

    /// <summary>Draws lines (with their markers) and stock charts.</summary>
    private void DrawLines(ChartGroup group, AxisLayout categories, AxisLayout values)
    {
        var stock = group.Kind == ChartKind.Stock;
        if (stock || group.HighLowLines is not null || group.UpDownBars is not null)
        {
            DrawStockParts(group, categories, values);
        }

        for (var s = 0; s < group.Series.Count; s++)
        {
            var series = group.Series[s];
            var tops = Plotted(group, s, out _);
            var points = new List<(double X, double Y)?>();
            for (var i = 0; i < tops.Length && i < categories.Count; i++)
            {
                var value = tops[i] ?? (_chart.Blanks == BlankDisplay.Zero && group.Grouping == ChartGrouping.Standard ? 0 : null);
                points.Add(value is { } v ? Point(categories, values, categories.CategoryFraction(i), v) : null);
                if (series.Values.Number(i) is { } own && points[^1] is { } p)
                {
                    _labelSpots.Add(new LabelSpot(group, series, i, p.X, p.Y, null, own < 0, false, own, null));
                }
            }

            if (group.DropLines is { } drop)
            {
                foreach (var point in points.OfType<(double X, double Y)>())
                {
                    var axisLine = LinePosition(categories);
                    Line(drop, _chart.AxisColor, ThinLine, point.X, point.Y, categories.Horizontal ? point.X : axisLine, categories.Horizontal ? axisLine : point.Y);
                }
            }

            var autoLine = stock ? (PdfColor?)null : series.AutoColor;
            DrawPolyline(points, series.Shape, autoLine, series.Smooth ?? false);
            DrawMarkers(group, series, points, stock ? false : group.Markers);
        }
    }

    /// <summary>The high-low lines and up-down bars of a stock chart (or a line chart that has them).</summary>
    private void DrawStockParts(ChartGroup group, AxisLayout categories, AxisLayout values)
    {
        if (group.Series.Count == 0)
        {
            return;
        }

        var count = Math.Min(categories.Count, group.Series.Max(s => s.Values.Count));
        var hiLow = group.HighLowLines ?? (group.Kind == ChartKind.Stock ? new ChartShape() : null);
        for (var i = 0; i < count; i++)
        {
            var numbers = group.Series.Select(s => s.Values.Number(i)).OfType<double>().ToList();
            if (numbers.Count == 0)
            {
                continue;
            }

            var f = categories.CategoryFraction(i);
            if (hiLow is not null)
            {
                var (x1, y1) = Point(categories, values, f, numbers.Max());
                var (x2, y2) = Point(categories, values, f, numbers.Min());
                Line(hiLow, _chart.TextColor, ThinLine, x1, y1, x2, y2);
            }

            if (group.UpDownBars is { } bars && group.Series.Count >= 2
                && group.Series[0].Values.Number(i) is { } open && group.Series[^1].Values.Number(i) is { } close)
            {
                var width = categories.BandFraction / 2.5;
                var (c0, c1) = (categories.Position(f - (width / 2)), categories.Position(f + (width / 2)));
                var (v0, v1) = (values.Position(values.ValueFraction(open)), values.Position(values.ValueFraction(close)));
                var box = categories.Horizontal
                    ? new Box(Math.Min(c0, c1), Math.Min(v0, v1), Math.Abs(c1 - c0), Math.Abs(v1 - v0))
                    : new Box(Math.Min(v0, v1), Math.Min(c0, c1), Math.Abs(v1 - v0), Math.Abs(c1 - c0));
                Paint(close >= open ? bars.Up : bars.Down, close >= open ? PdfColor.White : PdfColor.Black, _chart.TextColor, ThinLine, () => Rect(box));
            }
        }
    }

    /// <summary>Strokes a line through points (breaking at missing ones), straight or smoothed.</summary>
    private void DrawPolyline(List<(double X, double Y)?> points, ChartShape shape, PdfColor? autoColor, bool smooth)
    {
        var color = shape.Line is { } l ? l.Color : autoColor;
        var width = shape.LineWidth ?? SeriesLine;
        if (color is not { } c || width <= 0)
        {
            return;
        }

        // Runs of points without gaps (or all present points when blanks are spanned).
        var runs = new List<List<(double X, double Y)>>();
        var current = new List<(double X, double Y)>();
        foreach (var point in points)
        {
            if (point is { } p)
            {
                current.Add(p);
            }
            else if (_chart.Blanks != BlankDisplay.Span && current.Count > 0)
            {
                runs.Add(current);
                current = [];
            }
        }

        if (current.Count > 0)
        {
            runs.Add(current);
        }

        SetStroke(c, width, shape.Dash);
        _canvas.SetLineJoin(LineJoin.Round);
        _canvas.SetLineCap(LineCap.Round);
        var any = false;
        foreach (var run in runs.Where(r => r.Count >= 2))
        {
            any = true;
            MoveTo(run[0].X, run[0].Y);
            for (var i = 1; i < run.Count; i++)
            {
                if (smooth)
                {
                    // A Catmull-Rom spline through the points, as cubic Bezier curves.
                    var p0 = run[Math.Max(i - 2, 0)];
                    var p1 = run[i - 1];
                    var p2 = run[i];
                    var p3 = run[Math.Min(i + 1, run.Count - 1)];
                    CurveTo(p1.X + ((p2.X - p0.X) / 6), p1.Y + ((p2.Y - p0.Y) / 6), p2.X - ((p3.X - p1.X) / 6), p2.Y - ((p3.Y - p1.Y) / 6), p2.X, p2.Y);
                }
                else
                {
                    LineTo(run[i].X, run[i].Y);
                }
            }
        }

        if (any)
        {
            _canvas.Stroke();
        }

        _canvas.SetLineCap(LineCap.Butt);
        _canvas.SetLineJoin(LineJoin.Miter);
    }

    /// <summary>Draws the markers of a series' points.</summary>
    private void DrawMarkers(ChartGroup group, ChartSeries series, List<(double X, double Y)?> points, bool automatic)
    {
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i] is not { } p)
            {
                continue;
            }

            var marker = series.Points.TryGetValue(i, out var point) && point.Marker is { } own ? own : series.Marker;
            var symbol = marker?.Symbol ?? MarkerSymbol.Auto;
            if (symbol == MarkerSymbol.Auto)
            {
                symbol = automatic ? series.AutoMarker : MarkerSymbol.None;
            }

            if (symbol == MarkerSymbol.None)
            {
                continue;
            }

            var size = marker?.Size ?? series.Marker?.Size ?? DefaultMarkerSize;
            var color = AutoColor(group, series, i, points.Count);
            var shape = marker?.Shape ?? series.Marker?.Shape;
            DrawMarker(symbol, p.X, p.Y, size, shape, color);
        }
    }

    /// <summary>Draws one marker centered on a point.</summary>
    private void DrawMarker(MarkerSymbol symbol, double x, double y, double size, ChartShape? shape, PdfColor color)
    {
        var r = size / 2;
        var lineWidth = shape?.LineWidth ?? ThinLine;
        switch (symbol)
        {
            case MarkerSymbol.Plus or MarkerSymbol.X or MarkerSymbol.Star or MarkerSymbol.Dash:
            {
                var stroke = shape?.Line is { } l ? l.Color : color;
                if (stroke is not { } c)
                {
                    return;
                }

                SetStroke(c, Math.Max(lineWidth, 1), null);
                if (symbol is MarkerSymbol.Plus or MarkerSymbol.Star)
                {
                    MoveTo(x - r, y);
                    LineTo(x + r, y);
                    MoveTo(x, y - r);
                    LineTo(x, y + r);
                }

                if (symbol is MarkerSymbol.X or MarkerSymbol.Star)
                {
                    var d = r * 0.75;
                    MoveTo(x - d, y - d);
                    LineTo(x + d, y + d);
                    MoveTo(x - d, y + d);
                    LineTo(x + d, y - d);
                }

                if (symbol == MarkerSymbol.Dash)
                {
                    MoveTo(x - r, y);
                    LineTo(x + r, y);
                }

                _canvas.Stroke();
                return;
            }
        }

        Paint(shape, color, color, lineWidth, () =>
        {
            switch (symbol)
            {
                case MarkerSymbol.Circle or MarkerSymbol.Dot:
                    Ellipse(x, y, symbol == MarkerSymbol.Dot ? r / 2 : r, symbol == MarkerSymbol.Dot ? r / 2 : r);
                    break;
                case MarkerSymbol.Diamond:
                    MoveTo(x, y - r);
                    LineTo(x + r, y);
                    LineTo(x, y + r);
                    LineTo(x - r, y);
                    _canvas.ClosePath();
                    break;
                case MarkerSymbol.Triangle:
                    MoveTo(x, y - r);
                    LineTo(x + r, y + r);
                    LineTo(x - r, y + r);
                    _canvas.ClosePath();
                    break;
                default:
                    Rect(new Box(x - r, y - r, size, size));
                    break;
            }
        });
    }

    /// <summary>Adds an ellipse to the path.</summary>
    private void Ellipse(double cx, double cy, double rx, double ry)
    {
        const double K = 0.5523;
        MoveTo(cx + rx, cy);
        CurveTo(cx + rx, cy + (K * ry), cx + (K * rx), cy + ry, cx, cy + ry);
        CurveTo(cx - (K * rx), cy + ry, cx - rx, cy + (K * ry), cx - rx, cy);
        CurveTo(cx - rx, cy - (K * ry), cx - (K * rx), cy - ry, cx, cy - ry);
        CurveTo(cx + (K * rx), cy - ry, cx + rx, cy - (K * ry), cx + rx, cy);
        _canvas.ClosePath();
    }

    /// <summary>Draws scatter points and lines, or bubbles.</summary>
    private void DrawScatter(ChartGroup group, AxisLayout xAxis, AxisLayout yAxis)
    {
        var bubbles = group.Kind == ChartKind.Bubble;
        var maxSize = bubbles ? group.Series.SelectMany(s => s.BubbleSizes?.Numbers ?? []).OfType<double>().Select(Math.Abs).DefaultIfEmpty(0).Max() : 0;
        var maxRadius = Math.Min(xAxis.Length, yAxis.Length) * 0.125 * group.BubbleScale / 100;
        foreach (var series in group.Series)
        {
            var xs = XValues(series);
            var points = new List<(double X, double Y)?>();
            for (var i = 0; i < series.Values.Count; i++)
            {
                if (series.Values.Number(i) is not { } y || i >= xs.Length || xs[i] is not { } x)
                {
                    points.Add(null);
                    continue;
                }

                var px = xAxis.Position(Math.Clamp(xAxis.ValueFraction(x), -2, 3));
                var py = yAxis.Position(Math.Clamp(yAxis.ValueFraction(y), -2, 3));
                points.Add(double.IsFinite(px) && double.IsFinite(py) ? (px, py) : null);
                if (points[^1] is { } p)
                {
                    _labelSpots.Add(new LabelSpot(group, series, i, p.X, p.Y, null, y < 0, false, y, series.BubbleSizes?.Number(i)));
                }
            }

            if (bubbles)
            {
                for (var i = 0; i < points.Count; i++)
                {
                    if (points[i] is not { } p || series.BubbleSizes?.Number(i) is not { } size || maxSize <= 0 || size <= 0)
                    {
                        continue;
                    }

                    var ratio = Math.Abs(size) / maxSize;
                    var radius = maxRadius * (group.BubbleSizeWidth ? ratio : Math.Sqrt(ratio));
                    var color = AutoColor(group, series, i, points.Count);
                    Paint(PointShape(series, i), color, null, ThinLine, () => Ellipse(p.X, p.Y, radius, radius));
                }

                continue;
            }

            var autoLine = group.ScatterLines ? series.AutoColor : (PdfColor?)null;
            DrawPolyline(points, series.Shape, autoLine, series.Smooth ?? group.ScatterSmooth);
            DrawMarkers(group, series, points, group.Markers);
        }
    }

    /// <summary>The settings of a point's data label: its own, its series', or its group's.</summary>
    private static ChartDataLabels? LabelsOf(ChartGroup group, ChartSeries series, int index)
    {
        var labels = series.Labels ?? group.Labels;
        if (labels is null)
        {
            return null;
        }

        return labels.Points.TryGetValue(index, out var own) ? own.Delete ? null : Merge(own, labels) : labels;
    }

    /// <summary>A point's label settings over its series' (a point label that shows nothing shows what the series does).</summary>
    private static ChartDataLabels Merge(ChartDataLabels own, ChartDataLabels parent) =>
        own.Shows ? new ChartDataLabels
        {
            ShowValue = own.ShowValue,
            ShowCategory = own.ShowCategory,
            ShowSeries = own.ShowSeries,
            ShowPercent = own.ShowPercent,
            ShowBubbleSize = own.ShowBubbleSize,
            Position = own.Position ?? parent.Position,
            FormatCode = own.FormatCode ?? parent.FormatCode,
            Separator = own.Separator ?? parent.Separator,
            Format = own.Format.Over(parent.Format),
        }
        : parent;

    /// <summary>The text of a data label.</summary>
    private string LabelText(ChartDataLabels labels, ChartSeries series, string? category, double value, double? percent, double? size)
    {
        var parts = new List<string>();
        if (labels.ShowSeries && series.Name is { } name)
        {
            parts.Add(name);
        }

        if (labels.ShowCategory && category is { })
        {
            parts.Add(category);
        }

        if (labels.ShowValue)
        {
            parts.Add(ChartNumberFormat.Format(value, labels.FormatCode ?? series.Values.FormatCode, _chart.Date1904));
        }

        if (labels.ShowPercent && percent is { } share)
        {
            parts.Add(ChartNumberFormat.Format(share, labels.FormatCode is { } code && code.Contains('%', StringComparison.Ordinal) ? code : "0%", _chart.Date1904));
        }

        if (labels.ShowBubbleSize && size is { } bubble)
        {
            parts.Add(ChartNumberFormat.Format(bubble, null, _chart.Date1904));
        }

        return string.Join(labels.Separator ?? ", ", parts);
    }

    /// <summary>Draws the data labels of charts with axes, where their points and bars are.</summary>
    private void DrawAxisDataLabels(List<AxisLayout> axes, Box inner)
    {
        foreach (var spot in _labelSpots)
        {
            if (LabelsOf(spot.Group, spot.Series, spot.Index) is not { Shows: true } labels)
            {
                continue;
            }

            var categoryAxis = axes.FirstOrDefault(a => a.Category && a.Groups.Contains(spot.Group));
            var category = categoryAxis is not null && spot.Index < categoryAxis.CategoryLabels.Length ? categoryAxis.CategoryLabels[spot.Index]
                : spot.Series.Categories?.Texts?.ElementAtOrDefault(spot.Index) ?? spot.Series.Categories?.Number(spot.Index)?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var text = LabelText(labels, spot.Series, category, spot.Value, null, spot.Size);
            if (text.Length == 0)
            {
                continue;
            }

            var block = Text(text, Resolve(labels.Format));
            var gap = block.Size * 0.3;
            if (spot.Bar is { } bar)
            {
                var position = labels.Position ?? (spot.Group.Grouping == ChartGrouping.Standard ? "outEnd" : "ctr");
                if (spot.Horizontal)
                {
                    // Bars across: the end is right (left for negative values).
                    var (end, start) = spot.Negative ? (bar.X, bar.Right) : (bar.Right, bar.X);
                    var dir = spot.Negative ? -1 : 1;
                    var (x, alignX) = position switch
                    {
                        "ctr" => (bar.CenterX, 0.5),
                        "inEnd" => (end - (dir * gap), spot.Negative ? 0.0 : 1.0),
                        "inBase" => (start + (dir * gap), spot.Negative ? 1.0 : 0.0),
                        _ => (end + (dir * gap), spot.Negative ? 1.0 : 0.0),
                    };
                    DrawText(block, x, bar.CenterY, alignX, 0.5);
                }
                else
                {
                    var (end, start) = spot.Negative ? (bar.Bottom, bar.Y) : (bar.Y, bar.Bottom);
                    var dir = spot.Negative ? 1 : -1;
                    var (y, alignY) = position switch
                    {
                        "ctr" => (bar.CenterY, 0.5),
                        "inEnd" => (end - (dir * gap), spot.Negative ? 1.0 : 0.0),
                        "inBase" => (start + (dir * gap), spot.Negative ? 0.0 : 1.0),
                        _ => (end + (dir * gap), spot.Negative ? 0.0 : 1.0),
                    };
                    DrawText(block, bar.CenterX, y, 0.5, alignY);
                }

                continue;
            }

            var defaultPosition = spot.Group.Kind switch
            {
                ChartKind.Area or ChartKind.Bubble => "ctr",
                _ => "r",
            };
            var offset = DefaultMarkerSize / 2 + gap;
            switch (labels.Position ?? defaultPosition)
            {
                case "t":
                    DrawText(block, spot.X, spot.Y - offset, 0.5, 1);
                    break;
                case "b":
                    DrawText(block, spot.X, spot.Y + offset, 0.5, 0);
                    break;
                case "l":
                    DrawText(block, spot.X - offset, spot.Y, 1, 0.5);
                    break;
                case "ctr":
                    DrawText(block, spot.X, spot.Y, 0.5, 0.5);
                    break;
                default:
                    DrawText(block, spot.X + offset, spot.Y, 0, 0.5);
                    break;
            }
        }
    }
}

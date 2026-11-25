namespace RaiCore.Pdf.Charts;

/// <summary>
/// The newer chart types that do not draw as bars or lines: box and whisker plots (on axes), and treemaps and
/// sunbursts (hierarchies, without axes). Waterfalls, funnels, histograms and paretos draw as bars and lines.
/// </summary>
internal sealed partial class ChartRenderer
{
    /// <summary>The deepest hierarchy drawn (the reader bounds it already; this keeps drawing safe on its own).</summary>
    private const int MaxDepth = 32;

    /// <summary>
    /// Draws box and whisker plots: per category, a box from the first to the third quartile with the median across
    /// it, whiskers to the furthest values within 1.5 interquartile ranges, the mean as a cross, the outliers (and the
    /// inner points, when shown) as circles, and a line through the means when the group asks for one.
    /// </summary>
    private void DrawBoxes(ChartGroup group, AxisLayout categories, AxisLayout values)
    {
        var n = Math.Max(group.Series.Count, 1);
        var gap = group.GapWidth / 100;
        var band = categories.BandFraction;
        var width = band / (n + gap);
        var options = group.Box;
        for (var s = 0; s < group.Series.Count; s++)
        {
            var series = group.Series[s];
            var fill = series.Shape.Fill is { } f ? f.Color : series.AutoColor;
            var line = series.Shape.Line is { } l ? l.Color ?? _chart.AxisColor : _chart.AxisColor;
            var lineWidth = series.Shape.LineWidth ?? ThinLine;
            var means = new List<(double X, double Y)>();
            foreach (var box in series.Boxes ?? [])
            {
                if (box.Category >= categories.Count)
                {
                    continue;
                }

                var start = categories.CategoryFraction(box.Category) - (band / 2) + (gap * width / 2) + (s * width);
                var (c0, c1, cm) = (categories.Position(start), categories.Position(start + width), categories.Position(start + (width / 2)));
                double V(double value) => values.Position(Math.Clamp(values.ValueFraction(value), -2, 3));
                (double X, double Y) P(double c, double v) => categories.Horizontal ? (c, v) : (v, c);
                void Segment(double ca, double va, double cb, double vb)
                {
                    var (x1, y1) = P(ca, va);
                    var (x2, y2) = P(cb, vb);
                    MoveTo(x1, y1);
                    LineTo(x2, y2);
                }

                // Whiskers with their caps.
                SetStroke(line, lineWidth, null);
                var quarter = (c1 - c0) / 4;
                Segment(cm, V(box.WhiskerLow), cm, V(box.Q1));
                Segment(cm, V(box.Q3), cm, V(box.WhiskerHigh));
                Segment(cm - quarter, V(box.WhiskerLow), cm + quarter, V(box.WhiskerLow));
                Segment(cm - quarter, V(box.WhiskerHigh), cm + quarter, V(box.WhiskerHigh));
                _canvas.Stroke();

                // The box and the median.
                var (a, b) = (P(c0, V(box.Q1)), P(c1, V(box.Q3)));
                var rect = new Box(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                PaintPath(fill, line, lineWidth, series.Shape.Dash, () => Rect(rect));
                SetStroke(line, lineWidth, null);
                Segment(c0, V(box.Median), c1, V(box.Median));
                _canvas.Stroke();

                var mean = P(cm, V(box.Mean));
                means.Add(mean);
                if (options.MeanMarker)
                {
                    DrawMarker(MarkerSymbol.X, mean.X, mean.Y, 6, new ChartShape { Line = new Paint(line), Fill = Charts.Paint.None }, line);
                }

                foreach (var value in box.Values)
                {
                    var outlier = value < box.WhiskerLow || value > box.WhiskerHigh;
                    if (outlier ? options.Outliers : options.InnerPoints)
                    {
                        var point = P(cm, V(value));
                        DrawMarker(MarkerSymbol.Circle, point.X, point.Y, 5, new ChartShape { Line = new Paint(line), Fill = Charts.Paint.None }, line);
                    }
                }
            }

            if (options.MeanLine && means.Count > 1)
            {
                SetStroke(line, lineWidth, null);
                MoveTo(means[0].X, means[0].Y);
                foreach (var (x, y) in means.Skip(1))
                {
                    LineTo(x, y);
                }

                _canvas.Stroke();
            }
        }
    }

    /// <summary>A node of a hierarchy as laid out: its box (treemaps) or ring segment (sunbursts) and its color.</summary>
    private sealed record PlacedNode(ChartNode Node, int Depth, Box Box, PdfColor Color);

    /// <summary>Draws a treemap or sunburst: the hierarchy of its first series.</summary>
    private void DrawHierarchy(ChartGroup group, Box box)
    {
        if (group.Series.FirstOrDefault() is not { Hierarchy: { Value: > 0 } root } series)
        {
            return;
        }

        ResourceLimits.Checkpoint();
        if (group.Kind == ChartKind.Treemap)
        {
            DrawTreemap(group, series, root, box);
        }
        else
        {
            DrawSunburst(series, root, box);
        }
    }

    /// <summary>The color of a top-level branch and of everything under it (the point's own fill wins for leaves).</summary>
    private PdfColor BranchColor(ChartSeries series, ChartNode node, int branch, int branches) =>
        node.Point >= 0 && series.Points.TryGetValue(node.Point, out var point) && point.Shape?.Fill is { Color: { } own } ? own : _chart.StyleColor(branch, branches);

    /// <summary>
    /// Draws a treemap: each level's nodes squarified into their parent's box, leaves filled in their branch's color
    /// with white borders and their names (and values, when the labels show them); the names of nodes with children
    /// over their corner, in a banner above them, or not at all, as the group says.
    /// </summary>
    private void DrawTreemap(ChartGroup group, ChartSeries series, ChartNode root, Box box)
    {
        var labels = series.Labels ?? group.Labels;
        var format = Resolve(labels?.Format);
        var parents = new List<PlacedNode>();
        var leaves = new List<PlacedNode>();
        var banner = format.Size ?? 10;
        void Place(ChartNode node, Box area, int depth, int branch, int branches)
        {
            if (depth > MaxDepth || node.Children.Count == 0)
            {
                return;
            }

            var boxes = TreemapLayout([.. node.Children.Select(c => c.Value)], area.X, area.Y, area.Width, area.Height);
            for (var i = 0; i < node.Children.Count; i++)
            {
                var child = node.Children[i];
                var (x, y, w, h) = boxes[i];
                var own = depth == 0 ? i : branch;
                var color = BranchColor(series, child, own, depth == 0 ? node.Children.Count : branches);
                var placed = new PlacedNode(child, depth, new Box(x, y, w, h), color);
                if (child.Children.Count == 0)
                {
                    leaves.Add(placed);
                    continue;
                }

                parents.Add(placed);
                var inner = group.ParentLabels == ParentLabels.Banner && h > banner * 3 ? new Box(x, y + (banner * 1.4), w, h - (banner * 1.4)) : new Box(x, y, w, h);
                Place(child, inner, depth + 1, own, depth == 0 ? node.Children.Count : branches);
            }
        }

        Place(root, box, 0, 0, root.Children.Count);
        foreach (var leaf in leaves)
        {
            var shape = leaf.Node.Point >= 0 && series.Points.TryGetValue(leaf.Node.Point, out var point) ? point.Shape?.Over(series.Shape) ?? series.Shape : series.Shape;
            Paint(shape, leaf.Color, _chart.Background, ThinLine, () => Rect(leaf.Box));
        }

        foreach (var parent in parents.Where(p => group.ParentLabels == ParentLabels.Banner))
        {
            var strip = new Box(parent.Box.X, parent.Box.Y, parent.Box.Width, Math.Min(banner * 1.4, parent.Box.Height));
            PaintPath(parent.Color, _chart.Background, ThinLine, null, () => Rect(strip));
            NodeLabel(parent.Node.Name, null, strip, parent.Color, format, bold: true);
        }

        // Overlapping parent names stack at their corner, outer first; the names of the leaves there go below them.
        var below = new Dictionary<(long, long), double>();
        static (long, long) Corner(Box b) => ((long)Math.Round(b.X * 10), (long)Math.Round(b.Y * 10));
        if (group.ParentLabels == ParentLabels.Overlapping)
        {
            foreach (var parent in parents.OrderBy(p => p.Depth))
            {
                var corner = Corner(parent.Box);
                var offset = below.GetValueOrDefault(corner);
                var area = parent.Box.Deflate(0, offset, 0, 0);
                if (NodeLabel(parent.Node.Name, null, area, parent.Color, Resolve(labels?.Format, 1.1), bold: true) is { } height)
                {
                    below[corner] = offset + height;
                }
            }
        }

        foreach (var leaf in leaves)
        {
            if (labels is null || labels.Shows)
            {
                var value = labels?.ShowValue == true ? ChartNumberFormat.Format(leaf.Node.Value, labels.FormatCode ?? series.Values.FormatCode, _chart.Date1904) : null;
                var offset = below.GetValueOrDefault(Corner(leaf.Box));
                NodeLabel(labels is null || labels.ShowCategory ? leaf.Node.Name : null, value, leaf.Box.Deflate(0, offset, 0, 0), leaf.Color, format, bold: false);
            }
        }
    }

    /// <summary>
    /// A name (and value) at the top-left corner of a box, in white or black for contrast, if it fits; returns the
    /// height it takes, or <c>null</c> when it does not fit.
    /// </summary>
    private double? NodeLabel(string? name, string? value, Box box, PdfColor fill, ChartTextFormat format, bool bold)
    {
        var text = string.Join('\n', new[] { name, value }.Where(t => !string.IsNullOrEmpty(t)));
        var size = format.Size ?? 10;
        var pad = size * 0.3;
        if (text.Length == 0 || box.Width < size * 2 || box.Height < size * 1.4)
        {
            return null;
        }

        var light = (0.299 * fill.Red) + (0.587 * fill.Green) + (0.114 * fill.Blue) > 0.6;
        var color = format.Color is { } own && !own.Equals(_chart.TextColor) ? own : light ? PdfColor.Black : PdfColor.White;
        var block = Wrap(text, format with { Color = color, Bold = bold || format.Bold == true }, box.Width - (2 * pad));
        while (block.Lines.Length > 1 && block.Height > box.Height - (2 * pad))
        {
            block = Text(block.Lines[0], format with { Color = color, Bold = bold || format.Bold == true });
        }

        if (block.Height > box.Height || block.Width > box.Width)
        {
            return null;
        }

        DrawText(block, box.X + pad, box.Y + pad, 0, 0);
        return block.Height + pad;
    }

    /// <summary>
    /// Squarified treemap layout (Bruls, Huizing and van Wijk): boxes for values (in their order) that fill a
    /// rectangle, each with an area in proportion to its value, laid out in rows along the shorter side so that they
    /// are as square as they can be.
    /// </summary>
    internal static List<(double X, double Y, double Width, double Height)> TreemapLayout(IReadOnlyList<double> values, double x, double y, double width, double height)
    {
        var result = new (double, double, double, double)[values.Count];
        var total = values.Sum(v => Math.Max(v, 0));
        if (total <= 0 || !(width > 0) || !(height > 0))
        {
            return [.. result];
        }

        // Areas in the order of the values, laid out from the largest.
        var scale = width * height / total;
        var order = Enumerable.Range(0, values.Count).OrderByDescending(i => values[i]).ToList();
        var (rx, ry, rw, rh) = (x, y, width, height);
        var start = 0;
        while (start < order.Count)
        {
            var side = Math.Min(rw, rh);
            var end = start + 1;
            var worst = Worst(start, end);
            while (end < order.Count)
            {
                var next = Worst(start, end + 1);
                if (next > worst)
                {
                    break;
                }

                worst = next;
                end++;
            }

            var sum = 0.0;
            for (var k = start; k < end; k++)
            {
                sum += Area(k);
            }

            if (end == order.Count)
            {
                // The last row takes what is left, so that rounding leaves no gap.
                sum = rw * rh;
            }

            var thickness = side > 0 ? sum / side : 0;
            var along = 0.0;
            var rowSum = Enumerable.Range(start, end - start).Sum(Area);
            for (var k = start; k < end; k++)
            {
                var length = rowSum > 0 ? side * Area(k) / rowSum : 0;
                result[order[k]] = rw >= rh
                    ? (rx, ry + along, thickness, length)
                    : (rx + along, ry, length, thickness);
                along += length;
            }

            if (rw >= rh)
            {
                (rx, rw) = (rx + thickness, Math.Max(rw - thickness, 0));
            }
            else
            {
                (ry, rh) = (ry + thickness, Math.Max(rh - thickness, 0));
            }

            start = end;
        }

        return [.. result];

        double Area(int k) => Math.Max(values[order[k]], 0) * scale;

        // The worst aspect ratio of a row of the boxes [from, to) along the shorter side.
        double Worst(int from, int to)
        {
            var side = Math.Min(rw, rh);
            double sum = 0, max = 0, min = double.PositiveInfinity;
            for (var k = from; k < to; k++)
            {
                var a = Area(k);
                sum += a;
                max = Math.Max(max, a);
                min = Math.Min(min, a);
            }

            if (sum <= 0 || min <= 0 || side <= 0)
            {
                return double.PositiveInfinity;
            }

            var s2 = side * side;
            return Math.Max(s2 * max / (sum * sum), sum * sum / (s2 * min));
        }
    }

    /// <summary>
    /// Draws a sunburst: the top level in the innermost ring, each level a ring further out, every node's angle in
    /// proportion to its value from the top clockwise, in its branch's color, with its name where it fits.
    /// </summary>
    private void DrawSunburst(ChartSeries series, ChartNode root, Box box)
    {
        var depth = Depth(root, 0);
        var radius = Math.Min(box.Width, box.Height) / 2;
        var (cx, cy) = (box.CenterX, box.CenterY);
        var ring = radius / Math.Max(depth, 1);
        var labels = series.Labels;
        var format = Resolve(labels?.Format);
        var segments = SunburstSegments(root);
        foreach (var (node, level, start, sweep, branch) in segments)
        {
            var color = BranchColor(series, node, branch, root.Children.Count);
            var shape = node.Point >= 0 && series.Points.TryGetValue(node.Point, out var point) ? point.Shape?.Over(series.Shape) ?? series.Shape : series.Shape;
            var (inner, outer) = (level * ring, (level + 1) * ring);
            Paint(shape, color, _chart.Background, ThinLine, () => SlicePath(cx, cy, inner, outer, start, sweep));
        }

        foreach (var (node, level, start, sweep, branch) in segments)
        {
            if (labels is not null && !labels.ShowCategory && !labels.ShowValue)
            {
                continue;
            }

            var text = labels?.ShowValue == true
                ? string.Join('\n', new[] { labels.ShowCategory ? node.Name : null, ChartNumberFormat.Format(node.Value, labels.FormatCode ?? series.Values.FormatCode, _chart.Date1904) }.OfType<string>())
                : node.Name;
            var color = BranchColor(series, node, branch, root.Children.Count);
            var light = (0.299 * color.Red) + (0.587 * color.Green) + (0.114 * color.Blue) > 0.6;
            var block = Text(text, format with { Color = light ? PdfColor.Black : PdfColor.White });
            var middle = (level + 0.5) * ring;
            var arc = middle * sweep * Math.PI / 180;
            if (block.Width <= (level == 0 && sweep >= 359.99 ? ring * 2 : Math.Max(arc, ring) * 0.95) && block.Height <= Math.Min(ring, arc) * 0.95)
            {
                var (x, y) = level == 0 && sweep >= 359.99 ? (cx, cy) : OnCircle(cx, cy, middle, start + (sweep / 2));
                DrawText(block, x, y, 0.5, 0.5);
            }
        }
    }

    /// <summary>The depth of a hierarchy below a node.</summary>
    private static int Depth(ChartNode node, int level) =>
        level >= MaxDepth || node.Children.Count == 0 ? 0 : 1 + node.Children.Max(c => Depth(c, level + 1));

    /// <summary>
    /// The ring segments of a sunburst: each node's ring (0 for the top level), start angle and sweep in degrees
    /// (clockwise from the top, in proportion to its value), and its top-level branch.
    /// </summary>
    internal static List<(ChartNode Node, int Level, double Start, double Sweep, int Branch)> SunburstSegments(ChartNode root)
    {
        var result = new List<(ChartNode, int, double, double, int)>();
        void Add(ChartNode node, int level, double start, double sweep, int branch)
        {
            if (level >= MaxDepth || node.Value <= 0)
            {
                return;
            }

            var at = start;
            for (var i = 0; i < node.Children.Count; i++)
            {
                var child = node.Children[i];
                var share = sweep * Math.Max(child.Value, 0) / node.Value;
                var own = level == 0 ? i : branch;
                result.Add((child, level, at, share, own));
                Add(child, level + 1, at, share, own);
                at += share;
            }
        }

        Add(root, 0, 0, 360, 0);
        return result;
    }
}

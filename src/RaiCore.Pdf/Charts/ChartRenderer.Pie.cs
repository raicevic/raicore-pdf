namespace RaiCore.Pdf.Charts;

/// <summary>Round charts: pies and doughnuts (slices clockwise from the first slice angle, pulled out by explosion) and radar charts.</summary>
internal sealed partial class ChartRenderer
{
    /// <summary>The point on a circle at an angle clockwise from the top (chart coordinates, y downwards).</summary>
    private static (double X, double Y) OnCircle(double cx, double cy, double r, double degrees)
    {
        var a = degrees * Math.PI / 180;
        return (cx + (r * Math.Sin(a)), cy - (r * Math.Cos(a)));
    }

    /// <summary>Adds an arc clockwise from one angle to another (continuing the path) as cubic Bezier curves of at most 90 degrees.</summary>
    private void Arc(double cx, double cy, double r, double from, double to)
    {
        var segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(to - from) / 90));
        var step = (to - from) / segments;
        for (var i = 0; i < segments; i++)
        {
            var a1 = (from + (i * step)) * Math.PI / 180;
            var a2 = (from + ((i + 1) * step)) * Math.PI / 180;
            var h = 4.0 / 3 * Math.Tan((a2 - a1) / 4) * r;
            var (x1, y1) = (cx + (r * Math.Sin(a1)), cy - (r * Math.Cos(a1)));
            var (x2, y2) = (cx + (r * Math.Sin(a2)), cy - (r * Math.Cos(a2)));
            CurveTo(x1 + (h * Math.Cos(a1)), y1 + (h * Math.Sin(a1)), x2 - (h * Math.Cos(a2)), y2 - (h * Math.Sin(a2)), x2, y2);
        }
    }

    /// <summary>A slice as drawn: its angles, center and radii, for its label.</summary>
    private sealed record Slice(ChartGroup Group, ChartSeries Series, int Index, double Start, double Sweep, double CenterX, double CenterY, double Inner, double Outer, double Value, double Share);

    /// <summary>Draws the pie or doughnut groups of the chart in a box.</summary>
    private void DrawPies(Box box)
    {
        var group = _chart.PlotArea.Groups.FirstOrDefault(g => g.Kind is ChartKind.Pie or ChartKind.Doughnut);
        if (group is null || group.Series.Count == 0)
        {
            return;
        }

        var doughnut = group.Kind == ChartKind.Doughnut;
        var rings = doughnut ? group.Series : [group.Series[0]];

        // Room for labels outside the pie.
        var outside = 0.0;
        foreach (var series in rings)
        {
            for (var i = 0; i < series.Values.Count; i++)
            {
                if (!doughnut && LabelsOf(group, series, i) is { Shows: true } labels && labels.Position is null or "outEnd" or "bestFit")
                {
                    var text = LabelText(labels, series, CategoryOf(series, i), series.Values.Number(i) ?? 0, 0.5, null);
                    var block = Text(text, Resolve(labels.Format));
                    outside = Math.Max(outside, Math.Max(block.Width * 0.8, block.Height) + (block.Size * 0.5));
                    if (labels.Position is "outEnd")
                    {
                        outside = Math.Max(outside, block.Width + (block.Size * 0.5));
                    }
                }
            }
        }

        var maxExplosion = rings.SelectMany(s => s.Points.Values.Select(p => p.Explosion ?? s.Explosion).Append(s.Explosion)).DefaultIfEmpty(0).Max() / 100;
        var radius = Math.Min(box.Width, box.Height) / 2;
        radius = Math.Max(radius - outside, radius * 0.4) / (1 + Math.Min(maxExplosion, 4));
        var (cx, cy) = (box.CenterX, box.CenterY);
        var hole = doughnut ? radius * group.HoleSize / 100 : 0;
        var ringWidth = (radius - hole) / rings.Count;
        var slices = new List<Slice>();
        for (var r = 0; r < rings.Count; r++)
        {
            var series = rings[r];
            var count = series.Values.Count;
            var total = series.Values.Numbers.Sum(v => Math.Abs(v ?? 0));
            if (total <= 0 || !double.IsFinite(total))
            {
                continue;
            }

            var inner = hole + (r * ringWidth);
            var outer = inner + ringWidth;
            var angle = group.FirstSliceAngle;
            for (var i = 0; i < count; i++)
            {
                ResourceLimits.Checkpoint();
                var value = Math.Abs(series.Values.Number(i) ?? 0);
                if (value <= 0)
                {
                    continue;
                }

                var sweep = 360 * value / total;
                var explosion = (series.Points.TryGetValue(i, out var point) ? point.Explosion ?? series.Explosion : series.Explosion) / 100;
                var (ox, oy) = doughnut && r < rings.Count - 1 ? (cx, cy) : OnCircle(cx, cy, explosion * radius, angle + (sweep / 2));
                slices.Add(new Slice(group, series, i, angle, sweep, ox, oy, inner, outer, series.Values.Number(i) ?? 0, value / total));
                var color = AutoColor(group, series, i, count);
                var shape = PointShape(series, i);
                var (sliceStart, sliceSweep, sliceInner, sliceOuter, sx, sy) = (angle, sweep, inner, outer, ox, oy);
                Paint(shape, color, _chart.Background, ThinLine, () => SlicePath(sx, sy, sliceInner, sliceOuter, sliceStart, sliceSweep));
                angle += sweep;
            }
        }

        foreach (var slice in slices)
        {
            DrawSliceLabel(slice);
        }
    }

    /// <summary>Adds a slice (or a doughnut's ring segment) to the path.</summary>
    private void SlicePath(double cx, double cy, double inner, double outer, double start, double sweep)
    {
        if (sweep >= 359.99)
        {
            Ellipse(cx, cy, outer, outer);
            if (inner > 0)
            {
                Ellipse(cx, cy, inner, inner);
            }

            return;
        }

        var (x1, y1) = OnCircle(cx, cy, outer, start);
        if (inner > 0)
        {
            MoveTo(x1, y1);
            Arc(cx, cy, outer, start, start + sweep);
            var (x2, y2) = OnCircle(cx, cy, inner, start + sweep);
            LineTo(x2, y2);
            Arc(cx, cy, inner, start + sweep, start);
        }
        else
        {
            MoveTo(cx, cy);
            LineTo(x1, y1);
            Arc(cx, cy, outer, start, start + sweep);
        }

        _canvas.ClosePath();
    }

    /// <summary>The category name of a series' point.</summary>
    private string? CategoryOf(ChartSeries series, int index)
    {
        if (series.Categories is not { } categories || index >= categories.Count)
        {
            return (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return categories.Texts?[index] ?? (categories.Number(index) is { } n ? ChartNumberFormat.Format(n, categories.FormatCode, _chart.Date1904) : null);
    }

    /// <summary>Draws a slice's data label: inside or outside, as its position says (best fit: inside when it fits).</summary>
    private void DrawSliceLabel(Slice slice)
    {
        if (LabelsOf(slice.Group, slice.Series, slice.Index) is not { Shows: true } labels)
        {
            return;
        }

        var text = LabelText(labels, slice.Series, CategoryOf(slice.Series, slice.Index), slice.Value, slice.Share, null);
        if (text.Length == 0)
        {
            return;
        }

        var block = Text(text, Resolve(labels.Format));
        var mid = slice.Start + (slice.Sweep / 2);
        var ring = slice.Outer - slice.Inner;
        var doughnut = slice.Inner > 0;
        var position = labels.Position ?? (doughnut ? "ctr" : "bestFit");
        if (position == "bestFit")
        {
            // Inside when the label fits in the slice at two thirds of the radius.
            var r = slice.Inner + (ring * 0.65);
            var arc = r * slice.Sweep * Math.PI / 180;
            position = arc > Math.Max(block.Width, block.Height) * 1.1 && block.Width < ring * 0.9 ? "inside" : "outEnd";
        }

        switch (position)
        {
            case "outEnd" when !doughnut:
            {
                var (x, y) = OnCircle(slice.CenterX, slice.CenterY, slice.Outer + (block.Size * 0.4), mid);
                var sin = Math.Sin(mid * Math.PI / 180);
                var cos = Math.Cos(mid * Math.PI / 180);
                DrawText(block, x, y, Math.Clamp(0.5 - (sin * 0.75), 0, 1), Math.Clamp(0.5 + (cos * 0.75), 0, 1));
                break;
            }

            case "inEnd":
            {
                var (x, y) = OnCircle(slice.CenterX, slice.CenterY, slice.Outer - (ring * 0.2), mid);
                DrawText(block, x, y, 0.5, 0.5);
                break;
            }

            case "ctr" or "inBase" when !doughnut:
            {
                var (x, y) = OnCircle(slice.CenterX, slice.CenterY, slice.Outer * 0.5, mid);
                DrawText(block, x, y, 0.5, 0.5);
                break;
            }

            default:
            {
                var (x, y) = OnCircle(slice.CenterX, slice.CenterY, doughnut ? slice.Inner + (ring / 2) : slice.Inner + (ring * 0.65), mid);
                DrawText(block, x, y, 0.5, 0.5);
                break;
            }
        }
    }

    /// <summary>Draws a radar chart: spokes for the categories, value gridlines as polygons, and the series as closed lines or areas.</summary>
    private void DrawRadar(Box box)
    {
        var groups = _chart.PlotArea.Groups.Where(g => g.Kind == ChartKind.Radar).ToList();
        var series = groups.SelectMany(g => g.Series).ToList();
        var count = series.Count == 0 ? 0 : series.Max(s => Math.Max(s.Values.Count, s.Categories?.Count ?? 0));
        if (count == 0)
        {
            return;
        }

        var group = groups[0];
        var catAxis = group.AxisIds.Count > 0 ? _chart.PlotArea.Axes.FirstOrDefault(a => a.Id == group.AxisIds[0]) : null;
        var valAxis = group.AxisIds.Count > 1 ? _chart.PlotArea.Axes.FirstOrDefault(a => a.Id == group.AxisIds[1]) : null;
        valAxis ??= new ChartAxis { Kind = AxisKind.Value, MajorGridlines = new ChartShape() };
        catAxis ??= new ChartAxis { Kind = AxisKind.Category };

        // Category labels around the circle.
        var labelFormat = Resolve(catAxis.Text);
        var source = series.FirstOrDefault(s => s.Categories is { Count: > 0 });
        var labels = Enumerable.Range(0, count).Select(i => source is null ? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : CategoryOf(source, i) ?? string.Empty).ToList();
        var blocks = catAxis.Deleted || catAxis.Labels == TickLabelPosition.None ? [] : labels.Select(l => Text(l, labelFormat)).ToList();
        var maxWidth = blocks.Count == 0 ? 0 : blocks.Max(b => b.Width);
        var maxHeight = blocks.Count == 0 ? 0 : blocks.Max(b => b.Height);
        var radius = Math.Max(Math.Min((box.Width / 2) - maxWidth, (box.Height / 2) - maxHeight) - 4, Math.Min(box.Width, box.Height) * 0.2);
        var (cx, cy) = (box.CenterX, box.CenterY);

        // The value scale, from the center outwards.
        var values = series.SelectMany(s => s.Values.Numbers).OfType<double>().ToList();
        var size = Resolve(valAxis.Text).Size ?? 10;
        var scale = AxisScale.Automatic(values.Count == 0 ? double.NaN : values.Min(), values.Count == 0 ? double.NaN : values.Max(),
            valAxis.Min, valAxis.Max, valAxis.MajorUnit, valAxis.MinorUnit, null, (int)Math.Floor(radius / (size * 1.5)));
        double R(double value) => Math.Clamp(scale.Fraction(value), -0.1, 1.5) * radius;
        double Angle(int i) => 360.0 * i / count;

        // Gridlines: polygons at the major values, and spokes (the category axis).
        if (valAxis.MajorGridlines is { } grid)
        {
            foreach (var tick in scale.MajorTicks())
            {
                var r = R(tick);
                var color = grid.Line is { } l ? l.Color : _chart.GridColor;
                if (color is { } c)
                {
                    SetStroke(c, grid.LineWidth ?? ThinLine, grid.Dash);
                    for (var i = 0; i <= count; i++)
                    {
                        var (x, y) = OnCircle(cx, cy, r, Angle(i % count));
                        if (i == 0)
                        {
                            MoveTo(x, y);
                        }
                        else
                        {
                            LineTo(x, y);
                        }
                    }

                    _canvas.Stroke();
                }
            }
        }

        for (var i = 0; i < count; i++)
        {
            var (x, y) = OnCircle(cx, cy, radius, Angle(i));
            Line(catAxis.Line, _chart.GridColor, ThinLine, cx, cy, x, y);
        }

        foreach (var g in groups)
        {
            foreach (var s in g.Series)
            {
                var points = new List<(double X, double Y)?>();
                for (var i = 0; i < count; i++)
                {
                    points.Add(s.Values.Number(i) is { } v ? OnCircle(cx, cy, R(v), Angle(i)) : null);
                }

                var present = points.OfType<(double X, double Y)>().ToList();
                if (g.RadarFilled && present.Count >= 3)
                {
                    Paint(s.Shape, s.AutoColor, null, ThinLine, () =>
                    {
                        MoveTo(present[0].X, present[0].Y);
                        foreach (var (x, y) in present.Skip(1))
                        {
                            LineTo(x, y);
                        }

                        _canvas.ClosePath();
                    });
                    continue;
                }

                // A closed line: the first point again at the end.
                var closed = new List<(double X, double Y)?>(points);
                if (points.Count > 0)
                {
                    closed.Add(points[0]);
                }

                DrawPolyline(closed, s.Shape, s.AutoColor, false);
                DrawMarkers(g, s, points, g.RadarMarkers);
            }
        }

        // Value labels up the first spoke, category labels around.
        if (!valAxis.Deleted && valAxis.Labels != TickLabelPosition.None)
        {
            var format = valAxis.SourceLinked || valAxis.FormatCode is null ? series.FirstOrDefault()?.Values.FormatCode ?? valAxis.FormatCode : valAxis.FormatCode;
            foreach (var tick in scale.MajorTicks())
            {
                var block = Text(ChartNumberFormat.Format(tick, format, _chart.Date1904), Resolve(valAxis.Text));
                DrawText(block, cx - (size * 0.3), cy - R(tick), 1, 0.5);
            }
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            var angle = Angle(i);
            // Labels at the top and bottom stand off further, clear of the value labels up the first spoke.
            var sin = Math.Sin(angle * Math.PI / 180);
            var cos = Math.Cos(angle * Math.PI / 180);
            var (x, y) = OnCircle(cx, cy, radius + ((labelFormat.Size ?? 10) * (0.4 + (0.5 * Math.Abs(cos)))), angle);
            DrawText(blocks[i], x, y, Math.Clamp(0.5 - (sin * 0.75), 0, 1), Math.Clamp(0.5 + (cos * 0.75), 0, 1));
        }
    }
}


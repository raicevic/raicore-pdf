namespace RaiCore.Pdf.Charts;

/// <summary>The chart title and the legend: entries for series (or for the points of a pie), with keys like their series.</summary>
internal sealed partial class ChartRenderer
{
    /// <summary>A title as placed.</summary>
    private sealed record PlacedTitle(TextBlock Block, Box Box, ChartTitle Title);

    /// <summary>A legend entry: what it stands for and its text.</summary>
    /// <param name="Group">The group.</param>
    /// <param name="Series">The series.</param>
    /// <param name="Point">The point of a pie (or of a series of varied colors), or -1 for the series.</param>
    /// <param name="Block">The text.</param>
    /// <param name="Key">The color of a key of the series' own (<see cref="ChartSeries.LegendKeys"/>), or <c>null</c>.</param>
    private sealed record LegendEntry(ChartGroup Group, ChartSeries Series, int Point, TextBlock Block, PdfColor? Key = null);

    /// <summary>A legend as placed: its box and its entries' positions (the top-left corner of each).</summary>
    private sealed record PlacedLegend(Box Box, List<(LegendEntry Entry, double X, double Y)> Items, double KeyWidth, double ItemHeight);

    /// <summary>Lays out the chart title at the top of the free area (or where the file puts it).</summary>
    private PlacedTitle? PlaceTitle(ChartTitle title, Box free)
    {
        if (title.Text is not { Length: > 0 } text)
        {
            return null;
        }

        var block = Wrap(text, Resolve(title.Format, 1.2, bold: true), free.Width * 0.9);
        var pad = block.Size * 0.2;
        var (w, h) = (block.TurnedWidth + (2 * pad), block.TurnedHeight + (2 * pad));
        var box = new Box(free.CenterX - (w / 2), free.Y, w, h);
        if (title.Layout is { } layout)
        {
            var x = layout.X is { } lx ? layout.XFactor ? box.X + (lx * _width) : lx * _width : box.X;
            var y = layout.Y is { } ly ? layout.YFactor ? box.Y + (ly * _height) : ly * _height : box.Y;
            box = new Box(Math.Clamp(x, 0, Math.Max(_width - w, 0)), Math.Clamp(y, 0, Math.Max(_height - h, 0)), w, h);
        }

        return new PlacedTitle(block, box, title);
    }

    private void DrawTitle(PlacedTitle title)
    {
        Paint(title.Title.Shape, null, null, ThinLine, () => Rect(title.Box));
        DrawText(title.Block, title.Box.CenterX, title.Box.CenterY, 0.5, 0.5);
    }

    /// <summary>The legend's entries in order (series, or the points of a pie or of a single series with varied colors).</summary>
    private List<LegendEntry> LegendEntries(ChartLegend legend)
    {
        var entries = new List<LegendEntry>();
        var format = Resolve(legend.Text);
        var seriesCount = _chart.PlotArea.Groups.Sum(g => g.Series.Count);
        foreach (var group in _chart.PlotArea.Groups)
        {
            if (group.Series.Any(s => s.LegendKeys is not null))
            {
                foreach (var series in group.Series)
                {
                    foreach (var (text, color) in series.LegendKeys ?? [])
                    {
                        if (entries.Count < 1000)
                        {
                            entries.Add(new LegendEntry(group, series, -1, Text(text, format), color));
                        }
                    }
                }

                continue;
            }

            var points = group.Kind is ChartKind.Pie or ChartKind.Doughnut || (group.VaryColors && seriesCount == 1);
            if (points && group.Series.Count > 0)
            {
                var series = group.Series[0];
                var count = Math.Max(series.Values.Count, series.Categories?.Count ?? 0);
                for (var i = 0; i < count && entries.Count < 1000; i++)
                {
                    if (!legend.Deleted.Contains(i))
                    {
                        entries.Add(new LegendEntry(group, series, i, Text(CategoryOf(series, i) ?? string.Empty, format)));
                    }
                }

                continue;
            }

            var ordered = group.Series.AsEnumerable();
            var vertical = legend.Position is LegendPosition.Right or LegendPosition.Left or LegendPosition.TopRight;
            if (vertical && group.Grouping != ChartGrouping.Standard && ((group.Kind == ChartKind.Bar && !group.Horizontal) || group.Kind == ChartKind.Area))
            {
                // A vertical legend lists stacked series top-down, as they are stacked.
                ordered = ordered.Reverse();
            }

            foreach (var series in ordered)
            {
                if (!legend.Deleted.Contains(series.Index) && entries.Count < 1000)
                {
                    var name = series.Name ?? "Series " + (series.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    entries.Add(new LegendEntry(group, series, -1, Text(name, format)));
                }
            }
        }

        return entries;
    }

    /// <summary>Lays out the legend at its side of the free area (or where the file puts it).</summary>
    private PlacedLegend? PlaceLegend(ChartLegend legend, Box free, double padding)
    {
        var entries = LegendEntries(legend);
        if (entries.Count == 0)
        {
            return null;
        }

        var size = Resolve(legend.Text).Size ?? 10;
        var lineKeys = entries.Any(e => IsLineKey(e));
        var keyWidth = lineKeys ? size * 2 : size * 0.75;
        var spacing = size * 0.4;
        var itemHeight = Math.Max(entries.Max(e => e.Block.Height), size * 1.2);
        var pad = size * 0.3;
        var vertical = legend.Position is LegendPosition.Right or LegendPosition.Left or LegendPosition.TopRight;
        var items = new List<(LegendEntry, double, double)>();
        double width, height;
        if (vertical)
        {
            // Wrap long names so the legend takes at most a third of the chart.
            var maxText = Math.Max((_width / 3) - keyWidth - spacing - (2 * pad), size * 3);
            var format = Resolve(legend.Text);
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Block.Width > maxText)
                {
                    entries[i] = entries[i] with { Block = Wrap(string.Join(' ', entries[i].Block.Lines), format, maxText) };
                }
            }

            var y = pad;
            var widest = 0.0;
            foreach (var entry in entries)
            {
                var h = Math.Max(entry.Block.Height, itemHeight);
                if (y + h > free.Height - pad && items.Count > 0)
                {
                    break;
                }

                items.Add((entry, pad, y));
                y += h + (size * 0.15);
                widest = Math.Max(widest, keyWidth + spacing + entry.Block.Width);
            }

            (width, height) = (widest + (2 * pad), y - (size * 0.15) + pad);
        }
        else
        {
            // Rows of entries, wrapped to the free width and centered.
            var maxRow = free.Width - (2 * pad);
            var rows = new List<List<(LegendEntry Entry, double Width)>> { new() };
            var rowWidth = 0.0;
            foreach (var entry in entries)
            {
                var w = keyWidth + spacing + entry.Block.Width;
                if (rows[^1].Count > 0 && rowWidth + size + w > maxRow)
                {
                    rows.Add([]);
                    rowWidth = 0;
                }

                rowWidth += (rows[^1].Count > 0 ? size : 0) + w;
                rows[^1].Add((entry, w));
            }

            var rowWidths = rows.Select(r => r.Sum(e => e.Width) + (size * Math.Max(r.Count - 1, 0))).ToList();
            width = rowWidths.Max() + (2 * pad);
            var y = pad;
            for (var r = 0; r < rows.Count; r++)
            {
                if (y + itemHeight > free.Height * 0.5 && r > 0)
                {
                    break;
                }

                var x = pad + ((width - (2 * pad) - rowWidths[r]) / 2);
                var rowHeight = rows[r].Max(e => Math.Max(e.Entry.Block.Height, itemHeight));
                foreach (var (entry, w) in rows[r])
                {
                    items.Add((entry, x, y));
                    x += w + size;
                }

                y += rowHeight;
            }

            height = y + pad;
        }

        width = Math.Min(width, free.Width);
        height = Math.Min(height, free.Height);
        var box = legend.Position switch
        {
            LegendPosition.Left => new Box(free.X, free.CenterY - (height / 2), width, height),
            LegendPosition.Top => new Box(free.CenterX - (width / 2), free.Y, width, height),
            LegendPosition.Bottom => new Box(free.CenterX - (width / 2), free.Bottom - height, width, height),
            LegendPosition.TopRight => new Box(free.Right - width, free.Y, width, height),
            _ => new Box(free.Right - width, free.CenterY - (height / 2), width, height),
        };
        if (legend.Layout is { } layout)
        {
            var manual = Manual(layout, box);
            box = new Box(manual.X, manual.Y, Math.Max(manual.Width, width), Math.Max(manual.Height, height));
        }

        return new PlacedLegend(box, [.. items.Select(i => (i.Item1, box.X + i.Item2, box.Y + i.Item3))], keyWidth, itemHeight);
    }

    /// <summary>Whether an entry's key is a line (lines, scatter, unfilled radar) rather than a filled square.</summary>
    private static bool IsLineKey(LegendEntry entry) =>
        entry.Point < 0 && entry.Key is null && entry.Group.Kind switch
        {
            ChartKind.Line or ChartKind.Scatter or ChartKind.Stock => true,
            ChartKind.Radar => !entry.Group.RadarFilled,
            _ => false,
        };

    private void DrawLegend(PlacedLegend legend)
    {
        Paint(_chart.Legend?.Shape, null, null, ThinLine, () => Rect(legend.Box));
        foreach (var (entry, x, y) in legend.Items)
        {
            var size = entry.Block.Size;
            var centerY = y + (Math.Max(entry.Block.Height, legend.ItemHeight) / 2);
            var keyCenterX = x + (legend.KeyWidth / 2);
            var series = entry.Series;
            if (IsLineKey(entry))
            {
                var stock = entry.Group.Kind == ChartKind.Stock;
                var lineColor = series.Shape.Line is { } line ? line.Color : stock ? null : series.AutoColor;
                if (lineColor is { } c)
                {
                    SetStroke(c, Math.Min(series.Shape.LineWidth ?? SeriesLine, size / 3), series.Shape.Dash);
                    MoveTo(x, centerY);
                    LineTo(x + legend.KeyWidth, centerY);
                    _canvas.Stroke();
                }

                var symbol = series.Marker?.Symbol ?? MarkerSymbol.Auto;
                var automatic = entry.Group.Kind switch
                {
                    ChartKind.Line => entry.Group.Markers,
                    ChartKind.Scatter => entry.Group.Markers,
                    ChartKind.Radar => entry.Group.RadarMarkers,
                    _ => false,
                };
                symbol = symbol == MarkerSymbol.Auto ? automatic ? series.AutoMarker : MarkerSymbol.None : symbol;
                if (symbol != MarkerSymbol.None)
                {
                    DrawMarker(symbol, keyCenterX, centerY, Math.Min(series.Marker?.Size ?? DefaultMarkerSize, size), series.Marker?.Shape, series.AutoColor);
                }
            }
            else if (entry.Key is { } own)
            {
                var key = size * 0.75;
                var box = new Box(keyCenterX - (key / 2), centerY - (key / 2), key, key);
                PaintPath(own, null, ThinLine, null, () => Rect(box));
            }
            else
            {
                var key = size * 0.75;
                var index = entry.Point;
                var shape = index >= 0 ? PointShape(series, index) : series.Shape;
                var color = index >= 0 ? AutoColor(entry.Group, series, index, Math.Max(series.Values.Count, series.Categories?.Count ?? 0)) : series.AutoColor;
                var box = new Box(keyCenterX - (key / 2), centerY - (key / 2), key, key);
                Paint(shape with { LineWidth = Math.Min(shape.LineWidth ?? ThinLine, 1) }, color, null, ThinLine, () => Rect(box));
            }

            DrawText(entry.Block, x + legend.KeyWidth + (size * 0.4), centerY, 0, 0.5);
        }
    }
}

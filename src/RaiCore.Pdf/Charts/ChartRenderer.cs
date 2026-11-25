using System.Xml.Linq;
using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Charts;

/// <summary>
/// Finds the fonts for chart text: the typeface (a family, a theme font reference such as <c>+mn-lt</c>, or
/// <c>null</c> for the default), bold and italic; returns at least one font, the preferred first and fallbacks after.
/// </summary>
internal delegate IReadOnlyList<PdfFont> ChartFontResolver(string? typeface, bool bold, bool italic);

/// <summary>How charts are drawn: the fonts, the default text size and the defaults of the host format.</summary>
internal sealed class ChartRenderOptions
{
    /// <summary>Finds fonts for chart text.</summary>
    public required ChartFontResolver Fonts { get; init; }

    /// <summary>The size of text no format gives a size (10 points in workbooks and documents, 18 in presentations).</summary>
    public double DefaultFontSize { get; init; } = 10;

    /// <summary>
    /// Whether a chart area without a fill of its own is white with a gray border (workbooks and documents), rather
    /// than transparent (presentations).
    /// </summary>
    public bool OpaqueChartArea { get; init; } = true;
}

/// <summary>
/// Draws a <see cref="Chart"/> as vector graphics into a rectangle of a <see cref="PdfCanvas"/>: the chart area, the
/// title, the legend, and the plot area with its axes (Excel's automatic scaling), gridlines, series (bars, lines,
/// areas, pies, doughnuts, scatter, bubbles, radar and stock charts) and data labels. Text is drawn with the core's
/// fonts, so it can be extracted. Layout follows Office's automatic layout loosely; manual layouts the file gives are
/// applied to the plot area, the title and the legend.
/// </summary>
/// <remarks>
/// Partial: text (<c>ChartRenderer.Text.cs</c>), axes (<c>.Axes.cs</c>), bar, line, area, scatter, bubble, radar and
/// stock plots (<c>.Plots.cs</c>), pies and doughnuts (<c>.Pie.cs</c>) and the legend (<c>.Legend.cs</c>). Internally
/// positions are in points from the chart's top-left corner, y downwards.
/// </remarks>
internal sealed partial class ChartRenderer
{
    /// <summary>The length of tick marks.</summary>
    private const double TickLength = 4;

    /// <summary>The width of lines no format gives a width (gridlines, axes).</summary>
    private const double ThinLine = 0.75;

    /// <summary>The width of series lines no format gives a width.</summary>
    private const double SeriesLine = 2.25;

    private readonly PdfCanvas _canvas;
    private readonly Chart _chart;
    private readonly ChartRenderOptions _options;
    private readonly double _x0;
    private readonly double _y0;
    private readonly double _width;
    private readonly double _height;
    private readonly ChartTextFormat _baseText;

    private ChartRenderer(PdfCanvas canvas, Chart chart, double x, double y, double width, double height, ChartRenderOptions options)
    {
        _canvas = canvas;
        _chart = chart;
        _options = options;
        (_x0, _y0, _width, _height) = (x, y, width, height);
        _baseText = chart.Text.Over(new ChartTextFormat
        {
            Size = options.DefaultFontSize > 0 ? options.DefaultFontSize : 10,
            Bold = false,
            Italic = false,
            Color = chart.TextColor,
        });
    }

    /// <summary>
    /// Draws a chart into a rectangle of the canvas (in the canvas's coordinates, y upwards, (x, y) the bottom-left corner).
    /// </summary>
    public static void Draw(PdfCanvas canvas, Chart chart, double x, double y, double width, double height, ChartRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        if (!(width > 1) || !(height > 1) || !double.IsFinite(x + y + width + height) || width > 1e5 || height > 1e5)
        {
            return;
        }

        canvas.SaveState();
        try
        {
            new ChartRenderer(canvas, chart, x, y, width, height, options).Draw();
        }
        finally
        {
            canvas.RestoreState();
        }
    }

    /// <summary>
    /// Reads and draws a chart part in one step (the entry point for converters that place charts themselves, such as
    /// a workbook's drawing anchors). Returns <c>false</c>, drawing nothing, when the part holds no chart the engine
    /// can draw (a caller then draws the chart's stored picture, if any, and reports it).
    /// </summary>
    /// <param name="canvas">The canvas.</param>
    /// <param name="chartSpace">The chart part's root element (<c>c:chartSpace</c>), parsed by the caller's package reader.</param>
    /// <param name="bounds">The rectangle in the canvas's coordinates.</param>
    /// <param name="scheme">Resolves scheme colors (<c>accent1</c>, <c>tx1</c>, ...), or <c>null</c> for the Office theme.</param>
    /// <param name="options">The fonts and defaults.</param>
    /// <param name="limits">The limits that bound the chart's data.</param>
    /// <param name="report">Receives what is drawn differently from Office (3-D drawn flat, chart types left out).</param>
    /// <param name="references">Resolves cell references without cached values to the cells' values (workbooks), or <c>null</c>.</param>
    /// <exception cref="ResourceLimitExceededException">The chart has more points than the limits allow.</exception>
    public static bool Draw(PdfCanvas canvas, XElement chartSpace, PdfRectangle bounds, Func<string, PdfColor?>? scheme,
        ChartRenderOptions options, ResourceLimits limits, Action<string>? report, Func<string, ChartReference?>? references = null)
    {
        var unsupported = new List<string>();
        var chart = ChartReader.Read(chartSpace, scheme, limits, unsupported, references);
        if (chart is null || chart.PlotArea.Groups.Count == 0)
        {
            return false;
        }

        foreach (var kind in unsupported.Distinct())
        {
            report?.Invoke($"A chart's {kind} part is not drawn.");
        }

        foreach (var note in chart.Notes.Distinct())
        {
            report?.Invoke(note);
        }

        Draw(canvas, chart, bounds.X, bounds.Y, bounds.Width, bounds.Height, options);
        return true;
    }

    /// <summary>
    /// Draws a chart as an image of a size in points (a form XObject), for converters that place charts like pictures,
    /// such as documents laid out by the layout engine.
    /// </summary>
    public static PdfImage ToImage(Chart chart, double width, double height, ChartRenderOptions options)
    {
        width = double.IsFinite(width) ? Math.Clamp(width, 2, 14400) : 360;
        height = double.IsFinite(height) ? Math.Clamp(height, 2, 14400) : 216;
        return PdfImage.FromDrawing(width, height, canvas => Draw(canvas, chart, 0, 0, width, height, options));
    }

    /// <summary>A rectangle in chart coordinates (y downwards).</summary>
    private readonly record struct Box(double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;

        public double Bottom => Y + Height;

        public double CenterX => X + (Width / 2);

        public double CenterY => Y + (Height / 2);

        public Box Deflate(double left, double top, double right, double bottom) =>
            new(X + left, Y + top, Math.Max(Width - left - right, 1), Math.Max(Height - top - bottom, 1));
    }

    private void Draw()
    {
        var chartBox = new Box(0, 0, _width, _height);
        DrawChartArea(chartBox);

        // Room for the title and the legend, then the plot area.
        var padding = Math.Clamp(Math.Min(_width, _height) * 0.03, 3, 10);
        var free = chartBox.Deflate(padding, padding, padding, padding);
        var title = _chart.Title is { } t ? PlaceTitle(t, free) : null;
        if (title is { } placedTitle && !_chart.Title!.Overlay && _chart.Title.Layout is null)
        {
            free = free.Deflate(0, placedTitle.Box.Height + (padding * 0.75), 0, 0);
        }

        var legend = _chart.Legend is { } l ? PlaceLegend(l, free, padding) : null;
        if (legend is { } placedLegend && !_chart.Legend!.Overlay && _chart.Legend.Layout is null)
        {
            var gap = padding * 0.75;
            free = _chart.Legend.Position switch
            {
                LegendPosition.Left => free.Deflate(placedLegend.Box.Width + gap, 0, 0, 0),
                LegendPosition.Top => free.Deflate(0, placedLegend.Box.Height + gap, 0, 0),
                LegendPosition.Bottom => free.Deflate(0, 0, 0, placedLegend.Box.Height + gap),
                _ => free.Deflate(0, 0, placedLegend.Box.Width + gap, 0),
            };
        }

        var plot = _chart.PlotArea;
        var outer = free;
        var inner = (Box?)null;
        if (plot.Layout is { } layout)
        {
            var manual = Manual(layout, free);
            if (layout.Inner)
            {
                inner = manual;
            }
            else
            {
                outer = manual;
            }
        }

        var round = plot.Groups.All(g => g.Kind is ChartKind.Pie or ChartKind.Doughnut);
        if (plot.Groups.FirstOrDefault(g => g.Kind is ChartKind.Treemap or ChartKind.Sunburst) is { } hierarchy)
        {
            var box = inner ?? outer;
            Paint(plot.Shape, null, null, ThinLine, () => Rect(box));
            DrawHierarchy(hierarchy, box);
        }
        else if (round)
        {
            var box = inner ?? outer;
            Paint(plot.Shape, null, null, ThinLine, () => Rect(box));
            DrawPies(box);
        }
        else if (plot.Groups.Any(g => g.Kind == ChartKind.Radar) && plot.Groups.All(g => g.Kind is ChartKind.Radar or ChartKind.Pie or ChartKind.Doughnut))
        {
            var box = inner ?? outer;
            Paint(plot.Shape, null, null, ThinLine, () => Rect(box));
            DrawRadar(box);
        }
        else
        {
            DrawAxisChart(outer, inner);
        }

        if (legend is { } drawnLegend)
        {
            DrawLegend(drawnLegend);
        }

        if (title is { } drawnTitle)
        {
            DrawTitle(drawnTitle);
        }
    }

    /// <summary>The chart area: its fill and border (white with a gray border by default in workbooks and documents).</summary>
    private void DrawChartArea(Box box)
    {
        var shape = _chart.Area ?? new ChartShape();
        var fill = shape.Fill is { } f ? f.Color : _options.OpaqueChartArea ? _chart.Background : null;
        var line = shape.Line is { } l ? l.Color : _options.OpaqueChartArea ? _chart.AxisColor : null;
        var width = shape.LineWidth ?? ThinLine;
        var inset = line is null ? 0 : width / 2;
        var area = box.Deflate(inset, inset, inset, inset);
        var radius = _chart.RoundedCorners && line is not null ? Math.Min(Math.Min(area.Width, area.Height) / 2, 8) : 0;
        PaintPath(fill, line, width, shape.Dash, () =>
        {
            if (radius > 0)
            {
                RoundedRect(area, radius);
            }
            else
            {
                Rect(area);
            }
        });
    }

    /// <summary>A rectangle given as fractions of the chart (edges) or of the automatic box (factors).</summary>
    private Box Manual(ManualLayout layout, Box automatic)
    {
        var x = layout.X is { } lx ? layout.XFactor ? automatic.X + (lx * _width) : lx * _width : automatic.X;
        var y = layout.Y is { } ly ? layout.YFactor ? automatic.Y + (ly * _height) : ly * _height : automatic.Y;
        var w = layout.Width is { } lw ? layout.WidthFactor ? automatic.Width * (1 + lw) : lw * _width : automatic.Right - x;
        var h = layout.Height is { } lh ? layout.HeightFactor ? automatic.Height * (1 + lh) : lh * _height : automatic.Bottom - y;
        x = Math.Clamp(x, 0, _width - 1);
        y = Math.Clamp(y, 0, _height - 1);
        return new Box(x, y, Math.Clamp(w, 1, _width - x), Math.Clamp(h, 1, _height - y));
    }

    // Painting in chart coordinates.

    /// <summary>The x of a chart position on the canvas.</summary>
    private double X(double x) => _x0 + Finite(x);

    /// <summary>The y of a chart position on the canvas (chart y runs downwards).</summary>
    private double Y(double y) => _y0 + _height - Finite(y);

    /// <summary>A coordinate kept finite and near the chart (PDF numbers must be finite; the plot area clips the rest).</summary>
    private static double Finite(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, -1e5, 1e5);

    private void MoveTo(double x, double y) => _canvas.MoveTo(X(x), Y(y));

    private void LineTo(double x, double y) => _canvas.LineTo(X(x), Y(y));

    private void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) =>
        _canvas.CurveTo(X(x1), Y(y1), X(x2), Y(y2), X(x3), Y(y3));

    private void Rect(Box box) => _canvas.Rectangle(X(box.X), Y(box.Bottom), Finite(box.Width), Finite(box.Height));

    private void RoundedRect(Box box, double r)
    {
        const double K = 0.5523;
        var (l, t, rt, b) = (box.X, box.Y, box.Right, box.Bottom);
        MoveTo(l + r, t);
        LineTo(rt - r, t);
        CurveTo(rt - r + (K * r), t, rt, t + r - (K * r), rt, t + r);
        LineTo(rt, b - r);
        CurveTo(rt, b - r + (K * r), rt - r + (K * r), b, rt - r, b);
        LineTo(l + r, b);
        CurveTo(l + r - (K * r), b, l, b - r + (K * r), l, b - r);
        LineTo(l, t + r);
        CurveTo(l, t + r - (K * r), l + r - (K * r), t, l + r, t);
        _canvas.ClosePath();
    }

    /// <summary>
    /// Fills and strokes a path with a shape's paints (falling back to the automatic fill and line), or does nothing
    /// when neither paints.
    /// </summary>
    private void Paint(ChartShape? shape, PdfColor? autoFill, PdfColor? autoLine, double autoWidth, Action path)
    {
        var fill = shape?.Fill is { } f ? f.Color : autoFill;
        var line = shape?.Line is { } l ? l.Color : autoLine;
        PaintPath(fill, line, shape?.LineWidth ?? autoWidth, shape?.Dash, path);
    }

    /// <summary>Fills and strokes a path.</summary>
    private void PaintPath(PdfColor? fill, PdfColor? line, double width, double[]? dash, Action path)
    {
        if (fill is null && (line is null || width <= 0))
        {
            return;
        }

        if (fill is { } fillColor)
        {
            _canvas.SetFillColor(fillColor);
        }

        var stroke = line is { } && width > 0;
        if (stroke)
        {
            SetStroke(line!.Value, width, dash);
        }

        path();
        if (fill is not null && stroke)
        {
            _canvas.FillAndStroke();
        }
        else if (fill is not null)
        {
            _canvas.Fill();
        }
        else
        {
            _canvas.Stroke();
        }
    }

    /// <summary>Sets the stroke color, width and dash pattern (in multiples of the width).</summary>
    private void SetStroke(PdfColor color, double width, double[]? dash)
    {
        _canvas.SetStrokeColor(color);
        _canvas.SetLineWidth(Math.Clamp(width, 0.1, 100));
        _canvas.SetDash(dash is { Length: > 0 } ? [.. dash.Select(d => d * Math.Max(width, 0.5))] : []);
    }

    /// <summary>Strokes a straight line with a shape's line (or the automatic one).</summary>
    private void Line(ChartShape? shape, PdfColor autoColor, double autoWidth, double x1, double y1, double x2, double y2)
    {
        var color = shape?.Line is { } l ? l.Color : autoColor;
        var width = shape?.LineWidth ?? autoWidth;
        if (color is not { } c || width <= 0)
        {
            return;
        }

        SetStroke(c, width, shape?.Dash);
        MoveTo(x1, y1);
        LineTo(x2, y2);
        _canvas.Stroke();
    }

    /// <summary>Clips what follows to a box (until the state is restored).</summary>
    private void ClipTo(Box box)
    {
        Rect(box);
        _canvas.Clip();
        _canvas.EndPath();
    }
}

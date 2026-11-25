using System.Globalization;
using System.Text;
using System.Xml.Linq;
using RaiCore.Pdf.DrawingML;

namespace RaiCore.Pdf.Charts;

/// <summary>
/// Reads a DrawingML chart part (<c>c:chartSpace</c>, as stored in XLSX, DOCX and PPTX packages) into a
/// <see cref="Chart"/>: chart groups, series with their cached categories and values (<c>c:strCache</c>,
/// <c>c:numCache</c>, literals), axes, legend, titles, data labels, and fills, lines and text formats with theme colors
/// resolved through the caller. The element tree comes from the caller's package reader, which parses it without DTDs
/// and bounds its size; this reader bounds the points of each series and of the whole chart.
/// </summary>
internal sealed partial class ChartReader
{
    /// <summary>The most points a series may have (the rows of a worksheet).</summary>
    internal const int MaxPointsPerSeries = 1 << 20;

    /// <summary>A point count (<c>c:ptCount</c>) beyond the points present is believed up to this many points.</summary>
    internal const int TrustedPointCount = 4096;

    /// <summary>Marker symbols Office gives series in turn.</summary>
    private static readonly MarkerSymbol[] AutoMarkers =
        [MarkerSymbol.Diamond, MarkerSymbol.Square, MarkerSymbol.Triangle, MarkerSymbol.X, MarkerSymbol.Star, MarkerSymbol.Circle, MarkerSymbol.Plus, MarkerSymbol.Dot, MarkerSymbol.Dash];

    private readonly Func<string, PdfColor?> _scheme;
    private readonly Func<string, ChartReference?>? _references;
    private readonly ResourceLimits _limits;
    private readonly Chart _chart = new();
    private long _points;

    private ChartReader(Func<string, PdfColor?>? scheme, ResourceLimits limits, Dictionary<string, string>? colorMap, Func<string, ChartReference?>? references)
    {
        _limits = limits;
        _references = references;
        _scheme = colorMap is { Count: > 0 }
            ? name => colorMap.TryGetValue(name, out var mapped) ? DrawingMLColors.Scheme(mapped, scheme) : scheme?.Invoke(name)
            : name => scheme?.Invoke(name);
    }

    /// <summary>
    /// Reads a chart part. Returns <c>null</c> when the root is not a chart space or the chart has no plot area;
    /// chart groups of types the engine cannot draw are left out and named in <paramref name="unsupported"/>.
    /// </summary>
    /// <param name="root">
    /// The part's root element: <c>c:chartSpace</c>, or <c>cx:chartSpace</c> for the newer chart types (chartex:
    /// waterfall, histogram, pareto, box and whisker, funnel, treemap and sunburst; region maps are unsupported).
    /// </param>
    /// <param name="scheme">Resolves scheme colors (<c>accent1</c>, <c>tx1</c>, ...) through the theme and color map, or <c>null</c> for the Office theme.</param>
    /// <param name="limits">The limits: <see cref="ResourceLimits.MaxTableCells"/> bounds the points of the chart.</param>
    /// <param name="unsupported">Receives the chart types that are left out (such as <c>surface</c>).</param>
    /// <param name="references">
    /// Resolves a cell reference (<c>c:f</c>, such as <c>'Data'!$B$2:$B$5</c>) to the cells' values, for references
    /// without cached values (a workbook's charts written by other tools), or <c>null</c>.
    /// </param>
    /// <exception cref="ResourceLimitExceededException">The chart has more points than the limit allows.</exception>
    public static Chart? Read(XElement root, Func<string, PdfColor?>? scheme, ResourceLimits limits, ICollection<string>? unsupported = null,
        Func<string, ChartReference?>? references = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(limits);
        if (root.Name.LocalName != "chartSpace")
        {
            return null;
        }

        Dictionary<string, string>? map = null;
        if (Child(root, "clrMapOvr") is { } overrides)
        {
            map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var attribute in overrides.Attributes().Where(a => a.Name.NamespaceName.Length == 0))
            {
                map[attribute.Name.LocalName] = attribute.Value;
            }
        }

        var reader = new ChartReader(scheme, limits, map, references);
        return root.Name.NamespaceName == ChartExNamespace ? reader.ReadChartEx(root, unsupported) : reader.ReadChart(root, unsupported);
    }

    private Chart? ReadChart(XElement root, ICollection<string>? unsupported)
    {
        var chartElement = Child(root, "chart");
        var plot = Child(chartElement, "plotArea");
        if (chartElement is null || plot is null)
        {
            return null;
        }

        var chart = _chart;
        chart.Date1904 = Bool(Child(root, "date1904")) ?? false;
        chart.RoundedCorners = Bool(Child(root, "roundedCorners")) ?? true;
        chart.Style = Val(Child(root, "style")) is { } style && style is >= 1 and <= 48 ? (int)style : 2;
        chart.TextColor = DrawingMLColors.Luminance(Scheme("tx1"), 0.65, 0.35);
        chart.GridColor = DrawingMLColors.Luminance(Scheme("tx1"), 0.15, 0.85);
        chart.AxisColor = DrawingMLColors.Luminance(Scheme("tx1"), 0.5, 0.5);
        chart.Background = Scheme("bg1");
        chart.Accents.Clear();
        for (var i = 1; i <= 6; i++)
        {
            chart.Accents.Add(Scheme("accent" + i.ToString(CultureInfo.InvariantCulture)));
        }

        chart.Text = TextFormat(Child(root, "txPr")) ?? new ChartTextFormat();
        chart.Area = Shape(Child(root, "spPr"));

        chart.Blanks = Attr(Child(chartElement, "dispBlanksAs"), "val") switch
        {
            "zero" => BlankDisplay.Zero,
            "span" => BlankDisplay.Span,
            _ => BlankDisplay.Gap,
        };

        // The plot area: chart groups and axes.
        chart.PlotArea.Layout = Layout(Child(plot, "layout"));
        chart.PlotArea.Shape = Shape(Child(plot, "spPr"));
        foreach (var element in Children(plot))
        {
            ResourceLimits.Checkpoint();
            var name = element.Name.LocalName;
            if (name is "catAx" or "valAx" or "dateAx" or "serAx")
            {
                chart.PlotArea.Axes.Add(Axis(element));
            }
            else if (name.EndsWith("Chart", StringComparison.Ordinal))
            {
                if (Group(element) is { } group)
                {
                    chart.PlotArea.Groups.Add(group);
                }
                else
                {
                    unsupported?.Add(name[..^"Chart".Length]);
                }
            }
        }

        AssignAutomaticFormats();

        // The title: automatic for a single series unless deleted.
        var titleElement = Child(chartElement, "title");
        var autoTitleDeleted = Bool(Child(chartElement, "autoTitleDeleted")) ?? false;
        var allSeries = chart.PlotArea.Groups.SelectMany(g => g.Series).ToList();
        if (titleElement is not null)
        {
            chart.Title = Title(titleElement);
            chart.Title.Text ??= allSeries.Count == 1 ? allSeries[0].Name ?? "Chart Title" : "Chart Title";
        }
        else if (!autoTitleDeleted && allSeries.Count == 1 && allSeries[0].Name is { Length: > 0 } seriesName)
        {
            chart.Title = new ChartTitle { Text = seriesName };
        }

        if (Child(chartElement, "legend") is { } legend)
        {
            chart.Legend = Legend(legend);
        }

        return chart;
    }

    /// <summary>Gives series their automatic colors and markers from the chart style.</summary>
    private void AssignAutomaticFormats()
    {
        var all = _chart.PlotArea.Groups.SelectMany(g => g.Series).ToList();
        foreach (var series in all)
        {
            series.AutoColor = _chart.StyleColor(series.Index, all.Count);
            series.AutoMarker = AutoMarkers[(int)((uint)series.Index % (uint)AutoMarkers.Length)];
        }
    }

    private PdfColor Scheme(string name) => DrawingMLColors.Scheme(name, _scheme);

    /// <summary>Reads a chart group, or <c>null</c> for a type the engine cannot draw.</summary>
    private ChartGroup? Group(XElement element)
    {
        var name = element.Name.LocalName;
        var kind = name switch
        {
            "barChart" or "bar3DChart" => ChartKind.Bar,
            "lineChart" or "line3DChart" => ChartKind.Line,
            "pieChart" or "pie3DChart" or "ofPieChart" => ChartKind.Pie,
            "doughnutChart" => ChartKind.Doughnut,
            "areaChart" or "area3DChart" => ChartKind.Area,
            "scatterChart" => ChartKind.Scatter,
            "bubbleChart" => ChartKind.Bubble,
            "radarChart" => ChartKind.Radar,
            "stockChart" => ChartKind.Stock,
            _ => (ChartKind?)null,
        };
        if (kind is not { } k)
        {
            return null;
        }

        var group = new ChartGroup { Kind = k, ThreeD = name.Contains("3D", StringComparison.Ordinal) };
        if (group.ThreeD)
        {
            _chart.Notes.Add("A 3-D chart is drawn flat.");
        }

        if (name == "ofPieChart")
        {
            _chart.Notes.Add("A pie-of-pie or bar-of-pie chart is drawn as a single pie.");
        }

        if (k == ChartKind.Bubble && Bool(Child(element, "bubble3D")) == true)
        {
            group.ThreeD = true;
        }

        group.Horizontal = Attr(Child(element, "barDir"), "val") == "bar";
        group.Grouping = Attr(Child(element, "grouping"), "val") switch
        {
            "stacked" => ChartGrouping.Stacked,
            "percentStacked" => ChartGrouping.PercentStacked,
            _ => ChartGrouping.Standard,
        };
        group.VaryColors = Bool(Child(element, "varyColors")) ?? false;
        group.GapWidth = Math.Clamp(Val(Child(element, "gapWidth")) ?? 150, 0, 500);
        group.Overlap = Math.Clamp(Val(Child(element, "overlap")) ?? (group.Grouping == ChartGrouping.Standard ? 0 : 100), -100, 100);
        if (group.Grouping != ChartGrouping.Standard && k == ChartKind.Bar)
        {
            // Stacked bars always overlap fully.
            group.Overlap = 100;
        }

        group.HoleSize = Math.Clamp(Val(Child(element, "holeSize")) ?? 50, 1, 90);
        group.FirstSliceAngle = Math.Clamp(Val(Child(element, "firstSliceAng")) ?? 0, 0, 360);
        group.Markers = Bool(Child(element, "marker")) ?? true;
        var scatterStyle = Attr(Child(element, "scatterStyle"), "val");
        group.ScatterLines = scatterStyle is not ("marker" or "none");
        group.ScatterSmooth = scatterStyle is "smooth" or "smoothMarker";
        var radarStyle = Attr(Child(element, "radarStyle"), "val");
        group.RadarFilled = radarStyle == "filled";
        group.RadarMarkers = radarStyle == "marker";
        group.BubbleScale = Math.Clamp(Val(Child(element, "bubbleScale")) ?? 100, 0, 300);
        group.BubbleSizeWidth = Attr(Child(element, "sizeRepresents"), "val") == "w";
        if (Child(element, "hiLowLines") is { } hiLow)
        {
            group.HighLowLines = Shape(Child(hiLow, "spPr")) ?? new ChartShape();
        }

        if (Child(element, "dropLines") is { } drop)
        {
            group.DropLines = Shape(Child(drop, "spPr")) ?? new ChartShape();
        }

        if (Child(element, "upDownBars") is { } upDown)
        {
            group.UpDownBars = (
                Shape(Child(Child(upDown, "upBars"), "spPr")) ?? new ChartShape { Fill = new Paint(PdfColor.White) },
                Shape(Child(Child(upDown, "downBars"), "spPr")) ?? new ChartShape { Fill = new Paint(PdfColor.Black) });
        }

        foreach (var axisId in Children(element).Where(e => e.Name.LocalName == "axId"))
        {
            if (Val(axisId) is { } id)
            {
                group.AxisIds.Add((long)id);
            }
        }

        group.Labels = DataLabels(Child(element, "dLbls"));
        foreach (var series in Children(element).Where(e => e.Name.LocalName == "ser"))
        {
            ResourceLimits.Checkpoint();
            group.Series.Add(Series(series, k));
        }

        group.Series.Sort((a, b) => a.Order.CompareTo(b.Order));
        return group;
    }

    /// <summary>Reads a series.</summary>
    private ChartSeries Series(XElement element, ChartKind kind)
    {
        var series = new ChartSeries
        {
            Index = (int)Math.Clamp(Val(Child(element, "idx")) ?? 0, 0, int.MaxValue),
            Name = SeriesName(Child(element, "tx")),
            Shape = Shape(Child(element, "spPr")) ?? new ChartShape(),
            InvertIfNegative = Bool(Child(element, "invertIfNegative")) ?? false,
            Explosion = Math.Clamp(Val(Child(element, "explosion")) ?? 0, 0, 400),
            Labels = DataLabels(Child(element, "dLbls")),
        };
        series.Order = (int)Math.Clamp(Val(Child(element, "order")) ?? series.Index, 0, int.MaxValue);
        if (Child(element, "smooth") is { } smooth)
        {
            series.Smooth = Bool(smooth) ?? true;
        }

        if (Child(element, "marker") is { } marker)
        {
            series.Marker = Marker(marker);
        }

        var xy = kind is ChartKind.Scatter or ChartKind.Bubble;
        series.Categories = Data(Child(element, xy ? "xVal" : "cat"));
        series.Values = Data(Child(element, xy ? "yVal" : "val")) ?? ChartData.Empty;
        if (kind == ChartKind.Bubble)
        {
            series.BubbleSizes = Data(Child(element, "bubbleSize"));
        }

        foreach (var point in Children(element).Where(e => e.Name.LocalName == "dPt"))
        {
            if (Val(Child(point, "idx")) is not { } index || index < 0 || index >= MaxPointsPerSeries)
            {
                continue;
            }

            series.Points[(int)index] = new ChartPoint
            {
                Shape = Shape(Child(point, "spPr")),
                Marker = Child(point, "marker") is { } m ? Marker(m) : null,
                Explosion = Val(Child(point, "explosion")) is { } explosion ? Math.Clamp(explosion, 0, 400) : null,
                InvertIfNegative = Bool(Child(point, "invertIfNegative")),
            };
        }

        return series;
    }

    /// <summary>A series name: a string reference's cache, a literal, or <c>null</c>.</summary>
    private string? SeriesName(XElement? tx)
    {
        if (tx is null)
        {
            return null;
        }

        if (Child(tx, "v") is { } literal)
        {
            return literal.Value;
        }

        var reference = Child(tx, "strRef");
        var cache = Child(reference, "strCache");
        if (!HasPoints(cache) && Child(reference, "f")?.Value is { Length: > 0 } formula && _references?.Invoke(formula) is { } cells)
        {
            return string.Join(" ", cells.Values.Take(TrustedPointCount).Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
        }

        return cache is null ? null : string.Join(" ", Children(cache).Where(e => e.Name.LocalName == "pt").Select(p => Child(p, "v")?.Value ?? string.Empty));
    }

    /// <summary>Whether a cache has points.</summary>
    private static bool HasPoints(XElement? cache) => Children(cache).Any(e => e.Name.LocalName == "pt");

    /// <summary>A marker.</summary>
    private ChartMarker Marker(XElement element) => new()
    {
        Symbol = Attr(Child(element, "symbol"), "val") switch
        {
            "none" => MarkerSymbol.None,
            "circle" => MarkerSymbol.Circle,
            "dash" => MarkerSymbol.Dash,
            "diamond" => MarkerSymbol.Diamond,
            "dot" => MarkerSymbol.Dot,
            "plus" => MarkerSymbol.Plus,
            "square" or "picture" => MarkerSymbol.Square,
            "star" => MarkerSymbol.Star,
            "triangle" => MarkerSymbol.Triangle,
            "x" => MarkerSymbol.X,
            _ => MarkerSymbol.Auto,
        },
        Size = Val(Child(element, "size")) is { } size ? Math.Clamp(size, 2, 72) : null,
        Shape = Shape(Child(element, "spPr")),
    };

    /// <summary>
    /// The cached data of <c>c:cat</c>, <c>c:val</c>, <c>c:xVal</c>, <c>c:yVal</c> or <c>c:bubbleSize</c>: a string or
    /// number reference's cache, a multi-level string cache (its innermost level), or a literal.
    /// </summary>
    private ChartData? Data(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        foreach (var source in Children(element))
        {
            switch (source.Name.LocalName)
            {
                case "numRef":
                    return Referenced(source, text: false) ?? Points(Child(source, "numCache"), text: false);
                case "numLit":
                    return Points(source, text: false);
                case "strRef":
                    return Referenced(source, text: true) ?? Points(Child(source, "strCache"), text: true);
                case "strLit":
                    return Points(source, text: true);
                case "multiLvlStrRef":
                    return Points(Child(Child(source, "multiLvlStrCache"), "lvl"), text: true);
            }
        }

        return null;
    }

    /// <summary>
    /// The values of a reference without cached points, from the caller's cells (<c>null</c> when it has a cache or
    /// the caller cannot resolve it). Text in a number reference is a blank; a number reference whose cells are all
    /// text (category names written as a number reference) is read as text.
    /// </summary>
    private ChartData? Referenced(XElement reference, bool text)
    {
        var cache = Child(reference, text ? "strCache" : "numCache");
        if (HasPoints(cache) || _references is null || Child(reference, "f")?.Value is not { Length: > 0 } formula || _references(formula) is not { } cells)
        {
            return null;
        }

        var count = Math.Min(cells.Values.Count, MaxPointsPerSeries);
        Count(count);
        var numbers = new double?[count];
        var texts = new string?[count];
        var anyText = false;
        for (var i = 0; i < count; i++)
        {
            switch (cells.Values[i])
            {
                case double number when double.IsFinite(number):
                    numbers[i] = number;
                    texts[i] = ChartNumberFormat.Format(number, cells.FormatCode);
                    break;
                case string value:
                    texts[i] = value;
                    anyText = true;
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed))
                    {
                        numbers[i] = parsed;
                    }

                    break;
            }
        }

        return new ChartData(numbers, text || anyText ? texts : null, cells.FormatCode);
    }

    /// <summary>Counts points against <see cref="ResourceLimits.MaxTableCells"/>.</summary>
    private void Count(int count)
    {
        _points += count;
        if (_points > _limits.MaxTableCells)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxTableCells), _limits.MaxTableCells,
                $"A chart has more data points than ResourceLimits.MaxTableCells allows ({_limits.MaxTableCells}).");
        }
    }

    /// <summary>The points of a cache or literal.</summary>
    private ChartData? Points(XElement? cache, bool text)
    {
        if (cache is null)
        {
            return null;
        }

        var points = new List<(int Index, string Value)>();
        var maxIndex = -1;
        foreach (var point in Children(cache).Where(e => e.Name.LocalName == "pt"))
        {
            if (Attr(point, "idx") is not { } idxText
                || !int.TryParse(idxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                || index < 0 || index >= MaxPointsPerSeries)
            {
                continue;
            }

            points.Add((index, Child(point, "v")?.Value ?? string.Empty));
            maxIndex = Math.Max(maxIndex, index);
        }

        // The count: the points present, extended to the stated count when that is small enough to believe.
        var count = maxIndex + 1;
        if (Val(Child(cache, "ptCount")) is { } stated && stated > count)
        {
            count = (int)Math.Min(stated, Math.Max(count, TrustedPointCount));
        }

        Count(count);
        var format = Child(cache, "formatCode")?.Value is { Length: > 0 } code ? code : null;
        var numbers = new double?[count];
        string?[]? texts = text ? new string?[count] : null;
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

        return new ChartData(numbers, texts, format);
    }

    /// <summary>Reads an axis.</summary>
    private ChartAxis Axis(XElement element)
    {
        var scaling = Child(element, "scaling");
        var axis = new ChartAxis
        {
            Id = (long)(Val(Child(element, "axId")) ?? 0),
            Kind = element.Name.LocalName switch
            {
                "valAx" => AxisKind.Value,
                "dateAx" => AxisKind.Date,
                "serAx" => AxisKind.Series,
                _ => AxisKind.Category,
            },
            Position = Attr(Child(element, "axPos"), "val") switch
            {
                "l" => AxisPosition.Left,
                "r" => AxisPosition.Right,
                "t" => AxisPosition.Top,
                _ => AxisPosition.Bottom,
            },
            Deleted = Bool(Child(element, "delete")) ?? false,
            CrossAxisId = (long)(Val(Child(element, "crossAx")) ?? 0),
            Reversed = Attr(Child(scaling, "orientation"), "val") == "maxMin",
            Min = Val(Child(scaling, "min")),
            Max = Val(Child(scaling, "max")),
            LogBase = Val(Child(scaling, "logBase")) is { } logBase && logBase is >= 2 and <= 1000 ? logBase : null,
            MajorUnit = Val(Child(element, "majorUnit")) is { } major && major > 0 ? major : null,
            MinorUnit = Val(Child(element, "minorUnit")) is { } minor && minor > 0 ? minor : null,
            MajorTicks = Ticks(Child(element, "majorTickMark")),
            MinorTicks = Ticks(Child(element, "minorTickMark")),
            Labels = Attr(Child(element, "tickLblPos"), "val") switch
            {
                "low" => TickLabelPosition.Low,
                "high" => TickLabelPosition.High,
                "none" => TickLabelPosition.None,
                _ => TickLabelPosition.NextTo,
            },
            Line = Shape(Child(element, "spPr")),
            Text = TextFormat(Child(element, "txPr")) ?? new ChartTextFormat(),
            CrossBetween = Attr(Child(element, "crossBetween"), "val") != "midCat",
            LabelSkip = Val(Child(element, "tickLblSkip")) is { } skip && skip >= 1 ? (int)Math.Min(skip, int.MaxValue) : null,
            BaseTimeUnit = Attr(Child(element, "baseTimeUnit"), "val"),
            MajorTimeUnit = Attr(Child(element, "majorTimeUnit"), "val"),
        };
        if (Child(element, "majorTickMark") is null)
        {
            axis.MajorTicks = TickMark.Cross;
        }

        if (Child(element, "crosses") is { } crosses)
        {
            axis.Crosses = Attr(crosses, "val") switch
            {
                "min" => AxisCrosses.Min,
                "max" => AxisCrosses.Max,
                _ => AxisCrosses.AutoZero,
            };
        }
        else if (Val(Child(element, "crossesAt")) is { } at)
        {
            axis.CrossesAt = at;
        }

        if (Child(element, "numFmt") is { } format)
        {
            axis.FormatCode = Attr(format, "formatCode") is { Length: > 0 } code ? code : null;
            axis.SourceLinked = Bool(format, "sourceLinked") ?? false;
        }

        if (Child(element, "majorGridlines") is { } majorGrid)
        {
            axis.MajorGridlines = Shape(Child(majorGrid, "spPr")) ?? new ChartShape();
        }

        if (Child(element, "minorGridlines") is { } minorGrid)
        {
            axis.MinorGridlines = Shape(Child(minorGrid, "spPr")) ?? new ChartShape();
        }

        if (Child(element, "title") is { } title)
        {
            axis.Title = Title(title);
            axis.Title.Text ??= "Axis Title";
        }

        if (axis.Min is { } min && axis.Max is { } max && min >= max)
        {
            (axis.Min, axis.Max) = (null, null);
        }

        return axis;
    }

    private static TickMark Ticks(XElement? element) => Attr(element, "val") switch
    {
        "none" => TickMark.None,
        "in" => TickMark.Inside,
        "out" => TickMark.Outside,
        _ => TickMark.Cross,
    };

    /// <summary>Reads a legend.</summary>
    private ChartLegend Legend(XElement element)
    {
        var legend = new ChartLegend
        {
            Position = Attr(Child(element, "legendPos"), "val") switch
            {
                "l" => LegendPosition.Left,
                "t" => LegendPosition.Top,
                "b" => LegendPosition.Bottom,
                "tr" => LegendPosition.TopRight,
                _ => LegendPosition.Right,
            },
            Overlay = Bool(Child(element, "overlay")) ?? false,
            Layout = Layout(Child(element, "layout")),
            Text = TextFormat(Child(element, "txPr")) ?? new ChartTextFormat(),
            Shape = Shape(Child(element, "spPr")),
        };
        foreach (var entry in Children(element).Where(e => e.Name.LocalName == "legendEntry"))
        {
            if (Bool(Child(entry, "delete")) == true && Val(Child(entry, "idx")) is { } index && index is >= 0 and < MaxPointsPerSeries)
            {
                legend.Deleted.Add((int)index);
            }
        }

        return legend;
    }

    /// <summary>Reads a title (of the chart or an axis); <see cref="ChartTitle.Text"/> is <c>null</c> for an automatic one.</summary>
    private ChartTitle Title(XElement element)
    {
        var title = new ChartTitle
        {
            Overlay = Bool(Child(element, "overlay")) ?? false,
            Layout = Layout(Child(element, "layout")),
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
        else if (tx is not null && SeriesName(tx) is { } referenced)
        {
            title.Text = referenced;
        }

        title.Format = format;
        return title;
    }

    /// <summary>The text of rich text (<c>c:rich</c>) and the format of its first paragraph and run.</summary>
    private (string Text, ChartTextFormat Format) RichText(XElement rich)
    {
        var text = new StringBuilder();
        ChartTextFormat? format = Body(Child(rich, "bodyPr"));
        var first = true;
        foreach (var paragraph in Children(rich).Where(e => e.Name.LocalName == "p"))
        {
            if (!first)
            {
                text.Append('\n');
            }

            if (first && RunFormat(Child(Child(paragraph, "pPr"), "defRPr")) is { } defaults)
            {
                format = defaults.Over(format);
            }

            foreach (var run in Children(paragraph))
            {
                switch (run.Name.LocalName)
                {
                    case "r" or "fld":
                        if (first && text.Length == 0 && RunFormat(Child(run, "rPr")) is { } runFormat)
                        {
                            format = runFormat.Over(format);
                        }

                        text.Append(Child(run, "t")?.Value);
                        break;
                    case "br":
                        text.Append('\n');
                        break;
                }
            }

            first = false;
        }

        return (text.ToString(), format ?? new ChartTextFormat());
    }

    /// <summary>A text format from text properties (<c>c:txPr</c>), or <c>null</c>.</summary>
    private ChartTextFormat? TextFormat(XElement? txPr)
    {
        if (txPr is null)
        {
            return null;
        }

        var format = Body(Child(txPr, "bodyPr"));
        var paragraph = Children(txPr).FirstOrDefault(e => e.Name.LocalName == "p");
        if (RunFormat(Child(Child(paragraph, "pPr"), "defRPr")) is { } run)
        {
            format = run.Over(format);
        }

        return format ?? new ChartTextFormat();
    }

    /// <summary>The rotation of a text body (<c>a:bodyPr</c>), or <c>null</c>.</summary>
    private static ChartTextFormat? Body(XElement? bodyPr)
    {
        if (bodyPr is null)
        {
            return null;
        }

        double? rotation = null;
        if (DrawingMLColors.Number(bodyPr, "rot") is { } rot && rot is > -21600000 and < 21600000)
        {
            // Clockwise in 60000ths of a degree; -60000000 (out of range) means automatic.
            rotation = -rot / 60000;
        }

        var vertical = false;
        switch (Attr(bodyPr, "vert"))
        {
            case "vert" or "eaVert" or "wordArtVert" or "mongolianVert" or "wordArtVertRtl":
                rotation = -90;
                vertical = true;
                break;
            case "vert270":
                rotation = 90;
                vertical = true;
                break;
        }

        return new ChartTextFormat { Rotation = rotation, Vertical = vertical };
    }

    /// <summary>A run format (<c>a:defRPr</c>, <c>a:rPr</c>), or <c>null</c>.</summary>
    private ChartTextFormat? RunFormat(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var size = DrawingMLColors.Number(element, "sz") is { } sz && sz is >= 100 and <= 400000 ? sz / 100 : (double?)null;
        var typeface = Attr(Child(element, "latin"), "typeface") is { Length: > 0 } face ? face : null;
        return new ChartTextFormat
        {
            Size = size,
            Bold = Bool(element, "b"),
            Italic = Bool(element, "i"),
            Color = Fill(element) is { Color: { } color } ? color : null,
            Typeface = typeface,
        };
    }

    /// <summary>Reads data labels (<c>c:dLbls</c>), or <c>null</c>.</summary>
    private ChartDataLabels? DataLabels(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var labels = DataLabel(element);
        foreach (var point in Children(element).Where(e => e.Name.LocalName == "dLbl"))
        {
            if (Val(Child(point, "idx")) is { } index && index is >= 0 and < MaxPointsPerSeries)
            {
                labels.Points[(int)index] = DataLabel(point);
            }
        }

        return labels;
    }

    /// <summary>The settings of data labels or of one label.</summary>
    private ChartDataLabels DataLabel(XElement element) => new()
    {
        Delete = Bool(Child(element, "delete")) ?? false,
        ShowValue = Bool(Child(element, "showVal")) ?? false,
        ShowCategory = Bool(Child(element, "showCatName")) ?? false,
        ShowSeries = Bool(Child(element, "showSerName")) ?? false,
        ShowPercent = Bool(Child(element, "showPercent")) ?? false,
        ShowBubbleSize = Bool(Child(element, "showBubbleSize")) ?? false,
        Position = Attr(Child(element, "dLblPos"), "val"),
        FormatCode = Child(element, "numFmt") is { } format && Bool(format, "sourceLinked") != true && Attr(format, "formatCode") is { Length: > 0 } code ? code : null,
        Separator = Child(element, "separator")?.Value,
        Format = TextFormat(Child(element, "txPr")) ?? new ChartTextFormat(),
    };

    /// <summary>A manual layout (<c>c:layout</c>), or <c>null</c> for an automatic one.</summary>
    private static ManualLayout? Layout(XElement? layout)
    {
        if (Child(layout, "manualLayout") is not { } manual)
        {
            return null;
        }

        static double? Fraction(XElement? element) => Val(element) is { } value && value is > -10 and < 10 ? value : null;
        var result = new ManualLayout
        {
            Inner = Attr(Child(manual, "layoutTarget"), "val") == "inner",
            XFactor = Attr(Child(manual, "xMode"), "val") == "factor",
            YFactor = Attr(Child(manual, "yMode"), "val") == "factor",
            WidthFactor = Attr(Child(manual, "wMode"), "val") == "factor",
            HeightFactor = Attr(Child(manual, "hMode"), "val") == "factor",
            X = Fraction(Child(manual, "x")),
            Y = Fraction(Child(manual, "y")),
            Width = Fraction(Child(manual, "w")),
            Height = Fraction(Child(manual, "h")),
        };
        return result.X is null && result.Y is null && result.Width is null && result.Height is null ? null : result;
    }

    /// <summary>A fill and outline (<c>c:spPr</c>), or <c>null</c>.</summary>
    private ChartShape? Shape(XElement? spPr)
    {
        if (spPr is null)
        {
            return null;
        }

        var ln = Child(spPr, "ln");
        double? width = DrawingMLColors.Number(ln, "w") is { } w && w is >= 0 and <= 20116800 ? w / 12700 : null;
        double[]? dash = null;
        if (Attr(Child(ln, "prstDash"), "val") is { } preset)
        {
            dash = preset switch
            {
                "dash" => [4, 3],
                "sysDash" => [3, 1],
                "sysDot" => [1, 1],
                "dot" => [1, 3],
                "dashDot" => [4, 3, 1, 3],
                "lgDash" => [8, 3],
                "lgDashDot" => [8, 3, 1, 3],
                "lgDashDotDot" => [8, 3, 1, 3, 1, 3],
                "sysDashDot" => [3, 1, 1, 1],
                "sysDashDotDot" => [3, 1, 1, 1, 1, 1],
                _ => null,
            };
        }

        return new ChartShape
        {
            Fill = Fill(spPr),
            Line = Fill(ln),
            LineWidth = width,
            Dash = dash,
        };
    }

    /// <summary>
    /// The paint of the first fill child (<c>a:noFill</c>, <c>a:solidFill</c>, a gradient's middle stop, a pattern's
    /// foreground), or <c>null</c> when there is none (or it is a picture fill).
    /// </summary>
    private Paint? Fill(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        foreach (var child in Children(element))
        {
            switch (child.Name.LocalName)
            {
                case "noFill":
                    return Paint.None;
                case "solidFill":
                    return new Paint(DrawingMLColors.Child(child, _scheme, null) ?? PdfColor.Black);
                case "gradFill":
                {
                    var stops = Children(Child(child, "gsLst")).Where(e => e.Name.LocalName == "gs").ToList();
                    if (stops.Count > 0)
                    {
                        return new Paint(DrawingMLColors.Child(stops[stops.Count / 2], _scheme, null) ?? PdfColor.Black);
                    }

                    break;
                }

                case "pattFill":
                    return new Paint(DrawingMLColors.Child(Child(child, "fgClr"), _scheme, null) ?? PdfColor.Black);
            }
        }

        return null;
    }

    /// <summary>
    /// The children of an element, with markup compatibility blocks (<c>mc:AlternateContent</c>) replaced by their
    /// fallback's children (or their first choice's when there is no fallback).
    /// </summary>
    internal static IEnumerable<XElement> Children(XElement? element)
    {
        if (element is null)
        {
            yield break;
        }

        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName == "AlternateContent")
            {
                var branch = child.Elements().FirstOrDefault(e => e.Name.LocalName == "Fallback")
                    ?? child.Elements().FirstOrDefault(e => e.Name.LocalName == "Choice");
                foreach (var inner in branch?.Elements() ?? [])
                {
                    yield return inner;
                }
            }
            else
            {
                yield return child;
            }
        }
    }

    /// <summary>The first child with a local name, or <c>null</c>.</summary>
    internal static XElement? Child(XElement? element, string localName) => Children(element).FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Attr(XElement? element, string name) => (string?)element?.Attribute(name);

    /// <summary>The number in an element's <c>val</c> attribute, or <c>null</c>.</summary>
    private static double? Val(XElement? element) => DrawingMLColors.Number(element, "val");

    /// <summary>A boolean element (<c>val</c> defaults to true), or <c>null</c> when it is missing.</summary>
    private static bool? Bool(XElement? element) => element is null ? null : Bool(element, "val") ?? true;

    /// <summary>A boolean attribute, or <c>null</c>.</summary>
    private static bool? Bool(XElement? element, string name) => Attr(element, name) switch
    {
        "1" or "true" or "on" => true,
        "0" or "false" or "off" => false,
        _ => null,
    };
}

/// <summary>The values of the cells a chart refers to, from a workbook: numbers (<see cref="double"/>), text or <c>null</c>.</summary>
/// <param name="Values">The cells' values in order.</param>
/// <param name="FormatCode">The number format of the first cell, or <c>null</c>.</param>
/// <param name="Columns">The number of columns of the cells (the values are row by row), for references to several columns or rows.</param>
internal sealed record ChartReference(IReadOnlyList<object?> Values, string? FormatCode = null, int Columns = 1);

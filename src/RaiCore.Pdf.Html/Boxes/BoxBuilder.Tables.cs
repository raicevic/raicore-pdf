using System.Globalization;
using AngleSharp.Dom;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using VerticalAlignment = RaiCore.Pdf.Layout.Blocks.VerticalAlignment;

namespace RaiCore.Pdf.Html.Boxes;

/// <summary>HTML tables: thead/tbody/tfoot, captions, col widths, spans and the legacy presentational attributes.</summary>
internal sealed partial class BoxBuilder
{
    private List<Block> BuildTable(IElement element, ComputedStyle style, double containingWidth)
    {
        // Table cells have no definite height, so percentage heights inside them are auto.
        var outerHeight = _containingHeight;
        _containingHeight = null;
        var outerDecorations = EnterDecoration(style, 0);
        try
        {
            return [.. BuildTableBlocks(element, style, containingWidth)];
        }
        finally
        {
            _containingHeight = outerHeight;
            _decorations = outerDecorations;
        }
    }

    private IEnumerable<Block> BuildTableBlocks(IElement element, ComputedStyle style, double containingWidth)
    {
        // Legacy attributes still common in invoices and e-mail templates.
        var borderAttribute = Pixels(element.GetAttribute("border"));
        var spacingAttribute = Pixels(element.GetAttribute("cellspacing"));
        var paddingAttribute = Pixels(element.GetAttribute("cellpadding"));
        if (style.Width.IsAuto && Dimension(element.GetAttribute("width")) is { } widthAttribute)
        {
            style = style with { Width = widthAttribute };
        }

        if (borderAttribute is > 0 && style.BorderWidth.All(w => w == 0))
        {
            style = style with
            {
                BorderWidth = [borderAttribute.Value, borderAttribute.Value, borderAttribute.Value, borderAttribute.Value],
                BorderColor = [Gray, Gray, Gray, Gray],
                BorderStyle = [BorderStyle.Solid, BorderStyle.Solid, BorderStyle.Solid, BorderStyle.Solid],
            };
        }

        var (box, contentWidth) = BoxStyleFor(style, containingWidth);
        var captions = new List<Block>();
        var table = new TableBlock(box)
        {
            Id = IdFor(element),
            BorderCollapse = style.BorderCollapse ? BorderCollapse.Collapse : BorderCollapse.Separate,
            HorizontalSpacing = spacingAttribute ?? style.BorderSpacingX,
            VerticalSpacing = spacingAttribute ?? style.BorderSpacingY,
            LayoutMode = style.TableLayoutFixed ? TableLayoutMode.Fixed : TableLayoutMode.Auto,
            Alignment = ShrinkToFitAlignment(style),
            RightToLeft = style.Rtl,
        };

        var header = new List<TableRow>();
        var body = new List<TableRow>();
        var footer = new List<TableRow>();
        var cellDefaults = new CellDefaults(borderAttribute is > 0 ? 1 * CssValues.PointsPerPixel : null, paddingAttribute);
        foreach (var child in element.Children)
        {
            var childStyle = Style(child);
            if (childStyle.Display == CssDisplay.None)
            {
                continue;
            }

            switch (child.LocalName)
            {
                case "caption":
                    if (BuildBlockForTable(child, childStyle, contentWidth) is { } caption)
                    {
                        captions.Add(caption);
                    }

                    break;
                case "colgroup":
                case "col":
                    AddColumns(table, child, contentWidth);
                    break;
                case "thead":
                    header.AddRange(Rows(child, contentWidth, cellDefaults, isHeader: true));
                    break;
                case "tfoot":
                    footer.AddRange(Rows(child, contentWidth, cellDefaults, isHeader: false, isFooter: true));
                    break;
                case "tbody":
                    body.AddRange(Rows(child, contentWidth, cellDefaults, isHeader: false));
                    break;
                case "tr":
                    body.AddRange(Rows(element, contentWidth, cellDefaults, isHeader: false, only: child));
                    break;
            }
        }

        foreach (var row in header.Concat(body).Concat(footer))
        {
            table.Add(row);
        }

        return captions.Append(table);
    }

    private Block? BuildBlockForTable(IElement element, ComputedStyle style, double width) =>
        BuildBlock(element, style with { Display = CssDisplay.Block }, width, marker: null);

    private IEnumerable<TableRow> Rows(IElement group, double tableWidth, CellDefaults defaults, bool isHeader, IElement? only = null, bool isFooter = false)
    {
        foreach (var tr in only is null ? group.Children.Where(c => c.LocalName == "tr") : [only])
        {
            var rowStyle = Style(tr);
            if (rowStyle.Display == CssDisplay.None)
            {
                continue;
            }

            var cells = new List<TableCell>();
            foreach (var td in tr.Children.Where(c => c.LocalName is "td" or "th"))
            {
                var cellStyle = Style(td);
                if (cellStyle.Display != CssDisplay.None)
                {
                    cells.Add(BuildCell(td, cellStyle, tableWidth, defaults, isHeader));
                }
            }

            yield return new TableRow(cells)
            {
                IsHeader = isHeader,
                IsFooter = isFooter,
                Background = rowStyle.Background,
                MinHeight = rowStyle.Height.Kind == CssLengthKind.Points ? rowStyle.Height.Value : 0,
                BreakInside = rowStyle.BreakInside,
            };
        }
    }

    private TableCell BuildCell(IElement td, ComputedStyle style, double tableWidth, CellDefaults defaults, bool headerGroup)
    {
        // Legacy attributes apply only where the CSS keeps the user-agent defaults.
        if (defaults.Padding is { } padding && style.Padding.All(p => p.Kind == CssLengthKind.Points && Math.Abs(p.Value - CssValues.PointsPerPixel) < 1e-6))
        {
            style = style with { Padding = [new(padding, CssLengthKind.Points), new(padding, CssLengthKind.Points), new(padding, CssLengthKind.Points), new(padding, CssLengthKind.Points)] };
        }

        if (defaults.Border is { } border && style.BorderWidth.All(w => w == 0))
        {
            style = style with
            {
                BorderWidth = [border, border, border, border],
                BorderColor = [Gray, Gray, Gray, Gray],
                BorderStyle = [BorderStyle.Solid, BorderStyle.Solid, BorderStyle.Solid, BorderStyle.Solid],
            };
        }

        if (td.GetAttribute("align") is { } align && style.TextAlign == TextAlignment.Start)
        {
            style = style with { TextAlign = align.ToUpperInvariant() switch { "CENTER" => TextAlignment.Center, "RIGHT" => TextAlignment.Right, "JUSTIFY" => TextAlignment.Justify, _ => TextAlignment.Left } };
        }

        var valign = td.GetAttribute("valign")?.ToUpperInvariant() ?? style.VerticalAlign.ToUpperInvariant();
        var (box, contentWidth) = BoxStyleFor(style with { Margin = [CssLength.Zero, CssLength.Zero, CssLength.Zero, CssLength.Zero], Width = CssLength.Auto, MinWidth = CssLength.Auto, MaxWidth = CssLength.Auto }, tableWidth);

        var content = new List<Block>();
        var collector = new InlineCollector(this, style, contentWidth);
        WithPageWidthFluid(false, () =>
        {
            AddChildren(td, style, contentWidth, content, collector);
        });
        Flush(collector, content);

        var width = style.Width.IsAuto ? Dimension(td.GetAttribute("width")) : style.Width;
        return new TableCell(content)
        {
            Tag = CellTag(td, headerGroup),
            Style = new BoxStyle { Padding = box.Padding, Border = box.Border, Background = box.Background, BackgroundClip = box.BackgroundClip, BorderRadius = box.BorderRadius },
            ColumnSpan = Math.Clamp(Integer(td.GetAttribute("colspan")) ?? 1, 1, MaxColumnSpan),
            RowSpan = Math.Clamp(Integer(td.GetAttribute("rowspan")) ?? 1, 1, MaxRowSpan),
            VerticalAlignment = valign switch
            {
                "TOP" or "BASELINE" or "TEXT-TOP" => VerticalAlignment.Top,
                "BOTTOM" or "TEXT-BOTTOM" => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Middle,
            },
            Width = TableWidthFor(width, tableWidth),
        };
    }

    /// <summary>A cell or column width; calc() with a percentage is resolved against the table's content width.</summary>
    private static TableWidth? TableWidthFor(CssLength? length, double tableWidth) => length switch
    {
        { Kind: CssLengthKind.Points } points => TableWidth.Points(points.Value),
        { Kind: CssLengthKind.Percent } percent => TableWidth.Percent(percent.Value),
        { Kind: CssLengthKind.Calc } calc => TableWidth.Points(Math.Max(0, calc.Resolve(tableWidth))),
        _ => null,
    };

    private void AddColumns(TableBlock table, IElement element, double tableWidth)
    {
        IEnumerable<IElement> cols = element.LocalName == "col" ? [element] : element.Children.Where(c => c.LocalName == "col");
        if (element.LocalName == "colgroup" && !element.Children.Any(c => c.LocalName == "col"))
        {
            cols = [element]; // <colgroup span width> without <col>s.
        }

        foreach (var col in cols)
        {
            var style = Style(col);
            var length = style.Width.IsAuto ? Dimension(col.GetAttribute("width")) : style.Width;
            var width = TableWidthFor(length, tableWidth);
            for (var i = 0; i < Math.Clamp(Integer(col.GetAttribute("span")) ?? 1, 1, MaxColumnSpan); i++)
            {
                table.ColumnWidths.Add(width);
            }
        }
    }

    /// <summary>The largest <c>colspan</c> and <c>span</c>: larger values count as this (HTML's table processing model).</summary>
    private const int MaxColumnSpan = 1000;

    /// <summary>The largest <c>rowspan</c>: larger values count as this (HTML's table processing model).</summary>
    private const int MaxRowSpan = 65534;

    private static readonly PdfColor Gray = PdfColor.FromRgb(128, 128, 128);

    /// <summary>HTML length attribute: "120" (pixels) or "50%".</summary>
    private static CssLength? Dimension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.EndsWith('%') && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) && double.IsFinite(percent))
        {
            return new CssLength(percent, CssLengthKind.Percent);
        }

        return Pixels(value) is { } points ? new CssLength(points, CssLengthKind.Points) : null;
    }

    private static double? Pixels(string? value) =>
        double.TryParse(value?.Trim().TrimEnd('p', 'x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels) && pixels >= 0 && double.IsFinite(pixels)
            ? pixels * CssValues.PointsPerPixel
            : null;

    private static int? Integer(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private sealed record CellDefaults(double? Border, double? Padding);
}

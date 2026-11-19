using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Sheets;

/// <summary>
/// Lays out a <see cref="Sheet"/> as tables on pages with the layout engine: column widths from the measured text
/// (<see cref="ColumnFitter"/>), header rows repeated on every page, numbers and dates right-aligned, long words broken
/// rather than clipped, and columns split into bands that each fit the page width when they cannot all fit.
/// </summary>
internal static class SheetRenderer
{
    /// <summary>The cell padding left and right, in ems.</summary>
    private const double PaddingX = 0.4;

    /// <summary>The cell padding at the top and bottom, in ems.</summary>
    private const double PaddingY = 0.2;

    /// <summary>The least width a column of text gets before the columns are split into bands, in ems (about six characters).</summary>
    private const double FloorEms = 6;

    /// <summary>
    /// The widest word a column is widened for, in ems (about twenty characters): longer ones (identifiers, links) are
    /// broken when the columns do not all fit.
    /// </summary>
    private const double WordEms = 12;

    /// <summary>The width of the gridlines, in points.</summary>
    private const double GridWidth = 0.5;

    /// <summary>Room left at the page's right margin, in points, so that rounding never pushes a table over it.</summary>
    private const double Slack = 1;

    /// <summary>Added to each measured width, in points, so that the widest text fits its column on one line.</summary>
    private const double Tolerance = 0.01;

    private static readonly PdfColor GridColor = PdfColor.FromRgb(160, 160, 160);

    private static readonly PdfColor HeaderBackground = PdfColor.FromRgb(232, 232, 232);

    /// <summary>Renders a sheet to a new document.</summary>
    /// <param name="sheet">The sheet.</param>
    /// <param name="options">The options.</param>
    /// <param name="creator">The creator the document names.</param>
    /// <param name="checkpoint">Called regularly while the sheet is measured; throws to stop (time limit, cancellation).</param>
    public static PdfDocument Render(Sheet sheet, SheetOptions options, string creator, Action? checkpoint = null)
    {
        var flow = Layout(sheet, options, checkpoint);
        var document = new PdfDocument { Tagged = options.Tagged };
        flow.RenderTo(document);
        document.Info.Title = options.Title;
        document.Info.Creator = creator;
        return document;
    }

    /// <summary>Lays out a sheet: one table per band of columns.</summary>
    public static FlowDocument Layout(Sheet sheet, SheetOptions options, Action? checkpoint = null)
    {
        Validate(options);
        var page = options.Landscape ? options.Page with { Size = options.Page.Size.Landscape } : options.Page;
        var available = Math.Max(1, page.ContentWidth - Slack);
        var (regular, bold) = ResolveFonts(options);
        var grid = options.Gridlines ? GridWidth : 0;

        // Measured once at the font size asked for: widths are proportional to the font size.
        var measured = Measure(sheet, new TextStyle(regular, options.FontSize), new TextStyle(bold, options.FontSize), checkpoint);
        var size = options.FitToWidth ? FittedFontSize(measured, options, available, grid) : options.FontSize;
        var scale = size / options.FontSize;
        var fixedPart = (2 * PaddingX * size) + grid + Tolerance;
        var needs = measured.Select(m => new ColumnNeeds(
            Math.Min(m.Min * scale, WordEms * size) + fixedPart,
            (m.Max * scale) + fixedPart,
            Math.Max(Math.Min(m.Max * scale, FloorEms * size), m.Unbreakable * scale) + fixedPart,
            m.Mean * scale)).ToArray();

        var flow = new FlowDocument(page);
        var styles = new CellStyles(new TextStyle(regular, size), new TextStyle(bold, size), size, grid);
        var rightColumns = measured.Select(m => m.Numeric).ToArray();
        var bands = ColumnFitter.Fit(needs, available);
        foreach (var band in bands)
        {
            flow.Add(Table(sheet, band, styles, rightColumns, breakBefore: band.Start > 0));
        }

        if (sheet.Rows.Count == 0)
        {
            flow.Add(new ParagraphBlock(new Paragraph(styles.Body)));
        }

        return flow;
    }

    private static void Validate(SheetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Page, nameof(options));
        ArgumentNullException.ThrowIfNull(options.FontFamily, nameof(options));
        ArgumentNullException.ThrowIfNull(options.FallbackFontFamilies, nameof(options));
        if (!double.IsFinite(options.FontSize) || options.FontSize <= 0)
        {
            throw new ArgumentException($"The font size must be positive, not {options.FontSize}.", nameof(options));
        }

        if (!double.IsFinite(options.MinimumFontSize) || options.MinimumFontSize <= 0)
        {
            throw new ArgumentException($"The minimum font size must be positive, not {options.MinimumFontSize}.", nameof(options));
        }
    }

    /// <summary>The regular and bold fonts: the family, the fallbacks, then the bundled Liberation Sans.</summary>
    private static (PdfFont[] Regular, PdfFont[] Bold) ResolveFonts(SheetOptions options)
    {
        var collection = options.Fonts ?? BundledFonts.CreateCollection();
        PdfFont[] Resolve(int weight) =>
        [
            .. options.FallbackFontFamilies.Prepend(options.FontFamily)
                .Select(family => collection.Find(family, weight))
                .Append(BundledFonts.CreateCollection().Find("Liberation Sans", weight))
                .OfType<PdfFont>()
                .Distinct(),
        ];

        return (Resolve(400), Resolve(700));
    }

    /// <summary>
    /// The font size at which the columns fit <paramref name="available"/> points on one line each, but no larger than
    /// the size asked for and no smaller than the minimum.
    /// </summary>
    private static double FittedFontSize(IReadOnlyList<ColumnMeasure> measured, SheetOptions options, double available, double grid)
    {
        // Each column takes (text + padding) * size / FontSize + grid + tolerance.
        var proportional = measured.Sum(m => m.Max + (2 * PaddingX * options.FontSize));
        var room = available - (measured.Count * (grid + Tolerance));
        if (proportional <= 0 || room <= 0)
        {
            return options.FontSize;
        }

        var size = options.FontSize * room / proportional;
        return Math.Min(options.FontSize, Math.Max(size, Math.Min(options.MinimumFontSize, options.FontSize)));
    }

    /// <summary>What the text of a column needs at the font size asked for, in points, without padding.</summary>
    /// <param name="Min">The widest word (header included).</param>
    /// <param name="Max">The widest line.</param>
    /// <param name="Unbreakable">The widest number or date, which is never broken.</param>
    /// <param name="Mean">The mean width of its data cells on one line (of its header cells if it has no data).</param>
    /// <param name="Numeric">Whether most of the column's data cells are numbers or dates (its header is right-aligned too).</param>
    private readonly record struct ColumnMeasure(double Min, double Max, double Unbreakable, double Mean, bool Numeric);

    private static List<ColumnMeasure> Measure(Sheet sheet, TextStyle body, TextStyle header, Action? checkpoint)
    {
        var cache = new Dictionary<(string, bool), (double Min, double Max)>();
        var result = new List<ColumnMeasure>(sheet.ColumnCount);
        var measuredCells = 0L;
        for (var column = 0; column < sheet.ColumnCount; column++)
        {
            double min = 0, max = 0, unbreakable = 0, headerTotal = 0, bodyTotal = 0;
            int numbers = 0, texts = 0, headers = 0;
            for (var row = 0; row < sheet.Rows.Count; row++)
            {
                var cell = sheet[row, column];
                if (cell.Kind == CellKind.Empty)
                {
                    continue;
                }

                var isHeader = row < sheet.HeaderRows;
                var text = DisplayText(cell.Text);
                if (!cache.TryGetValue((text, isHeader), out var widths))
                {
                    widths = new Paragraph(isHeader ? header : body).Add(text).MeasureIntrinsicWidths();
                    cache[(text, isHeader)] = widths;
                }

                min = Math.Max(min, widths.Min);
                max = Math.Max(max, widths.Max);
                if (isHeader)
                {
                    headerTotal += widths.Max;
                    headers++;
                }
                else if (cell.Kind is CellKind.Number or CellKind.Date)
                {
                    unbreakable = Math.Max(unbreakable, widths.Max);
                    bodyTotal += widths.Max;
                    numbers++;
                }
                else
                {
                    bodyTotal += widths.Max;
                    texts++;
                }

                if (++measuredCells % 4096 == 0)
                {
                    checkpoint?.Invoke();
                }
            }

            var mean = numbers + texts > 0 ? bodyTotal / (numbers + texts) : headers > 0 ? headerTotal / headers : 0;
            result.Add(new ColumnMeasure(min, max, unbreakable, mean, numbers > texts));
        }

        return result;
    }

    /// <summary>The fonts, sizes and boxes of the cells at the final font size.</summary>
    private sealed class CellStyles
    {
        public CellStyles(TextStyle body, TextStyle header, double size, double grid)
        {
            Body = body;
            Header = header;
            Box = new BoxStyle
            {
                Padding = Thickness.Symmetric(PaddingY * size, PaddingX * size),
                Border = grid > 0 ? Borders.All(grid, GridColor) : Borders.None,
            };
            RowHeight = body.LineHeight.Resolve(body.Fonts[0], size) + (2 * PaddingY * size);
        }

        public TextStyle Body { get; }

        public TextStyle Header { get; }

        public BoxStyle Box { get; }

        /// <summary>The height of a row of one line, which empty rows keep.</summary>
        public double RowHeight { get; }
    }

    private static TableBlock Table(Sheet sheet, ColumnBand band, CellStyles styles, bool[] rightColumns, bool breakBefore)
    {
        var table = new TableBlock(new BoxStyle
        {
            Width = band.Widths.Sum(),
            BreakBefore = breakBefore ? PageBreak.Page : PageBreak.Auto,
        })
        {
            LayoutMode = TableLayoutMode.Fixed,
            BorderCollapse = BorderCollapse.Collapse,
            ColumnWidths = [.. band.Widths.Select(w => (Length?)w)],
        };

        for (var row = 0; row < sheet.Rows.Count; row++)
        {
            var isHeader = row < sheet.HeaderRows;
            var cells = new TableCell[band.Count];
            for (var i = 0; i < band.Count; i++)
            {
                var column = band.Start + i;
                cells[i] = Cell(sheet[row, column], isHeader, rightColumns[column], styles);
            }

            table.Add(new TableRow(cells)
            {
                IsHeader = isHeader,
                Background = isHeader ? HeaderBackground : null,
                MinHeight = styles.RowHeight,
            });
        }

        return table;
    }

    private static TableCell Cell(SheetCell cell, bool isHeader, bool rightColumn, CellStyles styles)
    {
        var text = DisplayText(cell.Text);
        if (cell.Kind == CellKind.Empty || text.Length == 0)
        {
            return new TableCell() { Style = styles.Box };
        }

        var right = isHeader ? rightColumn && cell.Alignment == CellAlignment.General : cell.IsRightAligned;
        var paragraph = new Paragraph(isHeader ? styles.Header : styles.Body)
        {
            Alignment = right ? TextAlignment.Right
                : cell.Alignment == CellAlignment.Center ? TextAlignment.Center
                : cell.Alignment == CellAlignment.Left ? TextAlignment.Left
                : TextAlignment.Start,
            Direction = right ? ParagraphDirection.LeftToRight : ParagraphDirection.Auto,
            BreakLongWords = true,
        }.Add(text);
        return new TableCell(paragraph) { Style = styles.Box };
    }

    /// <summary>
    /// A cell's text as shown: line breaks as line feeds, tabs as spaces, other control characters left out.
    /// </summary>
    internal static string DisplayText(string text)
    {
        var clean = true;
        foreach (var c in text)
        {
            if (char.IsControl(c) && c != '\n')
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                builder.Append('\n');
                i += i + 1 < text.Length && text[i + 1] == '\n' ? 1 : 0;
            }
            else if (c == '\t')
            {
                builder.Append(' ');
            }
            else if (c == '\n' || !char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}

using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets.Drawings;

namespace RaiCore.Pdf.Sheets;

/// <summary>
/// The layout of formatted sheets (worksheets): each cell in its own font, colors and borders, merged cells, the sheet's
/// column widths and row heights, its print scale, and text that runs on over empty cells beside it.
/// </summary>
internal static partial class SheetRenderer
{
    /// <summary>The cell padding left and right of formatted cells, in ems (spreadsheets leave about two pixels).</summary>
    private const double FormattedPaddingX = 0.2;

    /// <summary>The cell padding at the top and bottom of formatted cells, in ems.</summary>
    private const double FormattedPaddingY = 0.1;

    /// <summary>The indent of one level, in ems.</summary>
    private const double IndentEms = 0.8;

    /// <summary>The width an empty column fitted to its content keeps, in ems.</summary>
    private const double EmptyColumnEms = 2;

    /// <summary>The heading's font size, relative to the sheet's default size.</summary>
    private const double HeadingScale = 1.3;

    /// <summary>
    /// Renders sheets to one document, each from a new page with its own orientation. The pages of all sheets together
    /// are bounded by <see cref="ResourceLimits.MaxPages"/>.
    /// </summary>
    public static PdfDocument Render(IEnumerable<Sheet> sheets, SheetOptions options, string creator, string? title, Action? checkpoint = null)
    {
        Validate(options);
        var document = new PdfDocument { Tagged = options.Tagged };
        var count = 0;
        foreach (var sheet in sheets)
        {
            count++;
            var flow = Layout(sheet, options, checkpoint);
            var problems = LayoutProblems.Collect(flow, options.Diagnostics, document.Pages.Count + 1);
            flow.RenderTo(document);
            problems?.Report(sheet.Name, options.Diagnostics);
            var limit = options.Limits.MaxPages;
            if (document.Pages.Count > limit)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxPages), limit,
                    $"The document has more pages than ResourceLimits.MaxPages allows ({limit}).");
            }
        }

        if (count == 0)
        {
            Layout(new Sheet([], 0) { Formatting = new SheetFormatting() }, options, checkpoint).RenderTo(document);
        }

        document.Info.Title = options.Title ?? title;
        document.Info.Creator = creator;
        return document;
    }

    private static FlowDocument LayoutFormatted(Sheet sheet, SheetFormatting format, SheetOptions options, Action? checkpoint)
    {
        var page = options.Landscape || format.Landscape ? options.Page with { Size = options.Page.Size.Landscape } : options.Page;
        var available = Math.Max(1, page.ContentWidth - Slack);
        var grid = options.Gridlines ? GridWidth : 0;
        var context = new FormattedContext(sheet, format, options, grid);

        // Measured at 100%; widths are proportional to the scale.
        var measured = MeasureFormatted(context, checkpoint);
        var size = format.DefaultFontSize;
        var fixedPart = (2 * FormattedPaddingX * size) + grid + Tolerance;
        var natural = new double[sheet.ColumnCount];
        for (var c = 0; c < natural.Length; c++)
        {
            natural[c] = context.ColumnWidth(c) is { } width
                ? Math.Max(width, measured[c].Unbreakable + fixedPart)
                : Math.Max(measured[c].Max, EmptyColumnEms * size) + fixedPart;
        }

        var scale = format.Scale;
        if (options.FitToWidth || format.FitToWidth)
        {
            // The padding scales with the text; the gridlines and the rounding tolerance of fitted columns do not.
            var minimum = Math.Min(1, options.MinimumFontSize / size);
            var unscaled = Enumerable.Range(0, natural.Length).Sum(c => context.ColumnWidth(c) is null ? grid + Tolerance : 0);
            var proportional = natural.Sum() - unscaled;
            scale = proportional <= 0 ? 1 : Math.Clamp((available - unscaled - Tolerance) / proportional, minimum, 1);
        }

        context.Scale = scale;
        var scaledSize = size * scale;
        var scaledFixed = (2 * FormattedPaddingX * scaledSize) + grid + Tolerance;
        var needs = new ColumnNeeds[natural.Length];
        for (var c = 0; c < needs.Length; c++)
        {
            var m = measured[c];
            if (context.ColumnWidth(c) is not null)
            {
                var width = natural[c] * scale;
                needs[c] = new ColumnNeeds(width, width, width, width);
            }
            else
            {
                var max = (Math.Max(m.Max, EmptyColumnEms * size) * scale) + scaledFixed;
                needs[c] = new ColumnNeeds(
                    Math.Min(Math.Min(m.Min * scale, WordEms * scaledSize) + scaledFixed, max),
                    max,
                    Math.Min(Math.Max(Math.Min(m.Max * scale, FloorEms * scaledSize), m.Unbreakable * scale) + scaledFixed, max),
                    m.Mean * scale);
            }
        }

        var flow = new FlowDocument(page);
        if (format.HeaderFooter is { IsEmpty: false } headerFooter)
        {
            flow.PageDecorator = (canvas, info) => DrawHeaderFooter(canvas, info, headerFooter, context);
        }

        var bookmark = format.Bookmark is { } name ? new Bookmark(name) : null;
        if (format.Heading is { } heading)
        {
            var style = new TextStyle(context.Fonts(null, bold: true, italic: false), scaledSize * HeadingScale);
            flow.Add(new ParagraphBlock(new Paragraph(style).Add(SheetRenderer.DisplayText(heading)), new BoxStyle
            {
                Margin = new Thickness(0, 0, scaledSize * 0.8, 0),
            })
            {
                Bookmark = bookmark,
                Tag = new StructureTag("H1"),
            });
            bookmark = null;
        }

        // Page breaks set by hand between columns start a band: the columns between them are fitted on their own.
        var bands = new List<ColumnBand>();
        var groupStart = 0;
        foreach (var end in format.ColumnBreaks.Where(b => b > 0 && b < needs.Length).Append(needs.Length))
        {
            if (end > groupStart)
            {
                bands.AddRange(ColumnFitter.Fit(needs[groupStart..end], available).Select(b => b with { Start = b.Start + groupStart }));
                groupStart = end;
            }
        }

        context.ColumnSizes = [.. bands.SelectMany(b => b.Widths)];
        context.NaturalWidths = natural;
        foreach (var band in bands)
        {
            var breakBefore = band.Start > 0;
            if (format.LeadRows > 0)
            {
                flow.Add(FormattedTable(context, band, 0, Math.Min(format.LeadRows, sheet.Rows.Count), 0, breakBefore, bookmark));
                breakBefore = false;
                bookmark = null;
            }

            if (format.LeadRows < sheet.Rows.Count)
            {
                // Page breaks set by hand between rows end a table; the next starts a page, with the header rows again.
                var start = format.LeadRows;
                var headerRows = sheet.HeaderRows;
                foreach (var end in format.RowBreaks.Where(b => b > start + headerRows && b < sheet.Rows.Count).Append(sheet.Rows.Count))
                {
                    if (start == format.LeadRows)
                    {
                        flow.Add(FormattedTable(context, band, start, end, headerRows, breakBefore, bookmark));
                    }
                    else
                    {
                        flow.Add(FormattedTable(context, band, start, end, headerRows, true, null, headerFrom: format.LeadRows));
                    }

                    bookmark = null;
                    start = end;
                }
            }
        }

        if (format.Comments is { Printing: CommentPrinting.AtEnd, List.Count: > 0 } comments)
        {
            flow.Add(CommentList(context, comments.List, available));
        }

        if (flow.Blocks.Count == 0)
        {
            flow.Add(new ParagraphBlock(new Paragraph(new TextStyle(context.Fonts(null, false, false), scaledSize))) { Bookmark = bookmark });
        }

        return flow;
    }

    /// <summary>
    /// Draws a page's header and footer: each section (left, center, right) across the page between the margins, the
    /// header from its distance below the page's top, the footer ending at its distance above the bottom, in the sheet's
    /// fonts at its scale (unless they do not scale with it).
    /// </summary>
    private static void DrawHeaderFooter(PdfCanvas canvas, PageInfo info, SheetHeaderFooter headerFooter, FormattedContext context)
    {
        var (header, footer) = headerFooter.For(info.Number);
        var (date, culture, file) = context.Format.PrintContext;
        var fields = new HeaderFooterContext(info.Number + headerFooter.FirstPageNumber - 1, info.Count, context.Sheet.Name, file, date, culture);
        var setup = info.Setup;
        var margins = setup.UsedMargins;
        var width = Math.Max(1, setup.Size.Width - margins.Left - margins.Right);
        var scale = headerFooter.ScaleWithDocument ? context.Scale : 1;
        foreach (var (text, isHeader) in new[] { (header, true), (footer, false) })
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var sections = HeaderFooterText.Paragraphs(HeaderFooterText.Parse(text, fields), context.Fonts, context.Format.DefaultFontSize, scale);
            foreach (var paragraph in sections.OfType<Paragraph>())
            {
                Block[] blocks = [new ParagraphBlock(paragraph)];
                var top = isHeader ? setup.Size.Height - headerFooter.HeaderMargin : headerFooter.FooterMargin + FlowDocument.MeasureBlocks(blocks, width);
                FlowDocument.DrawBlocks(canvas, blocks, margins.Left, top, width);
            }
        }
    }

    private static List<ColumnMeasure> MeasureFormatted(FormattedContext context, Action? checkpoint)
    {
        var sheet = context.Sheet;
        var result = new List<ColumnMeasure>(sheet.ColumnCount);
        var measuredCells = 0L;
        for (var column = 0; column < sheet.ColumnCount; column++)
        {
            double min = 0, max = 0, unbreakable = 0, total = 0;
            int numbers = 0, count = 0;
            for (var row = 0; row < sheet.Rows.Count; row++)
            {
                var cell = sheet[row, column];
                if (cell.Kind == CellKind.Empty || context.SpansColumns(row, column))
                {
                    continue;
                }

                var format = cell.Format ?? CellFormat.Default;
                var style = context.Style(format, 1);
                var widths = context.MeasureCell(cell, format, style, 1);

                // The column's padding is at the default size: a cell in a larger font needs its own, wider one.
                var padding = Math.Max(0, 2 * FormattedPaddingX * (style.FontSize - context.Format.DefaultFontSize));
                var indent = (format.Indent * IndentEms * style.FontSize) + padding;
                min = Math.Max(min, widths.Min + indent);
                max = Math.Max(max, widths.Max + indent);
                total += widths.Max + indent;
                count++;
                if (cell.Kind is CellKind.Number or CellKind.Date)
                {
                    unbreakable = Math.Max(unbreakable, widths.Max + indent);
                    numbers++;
                }

                if (++measuredCells % 4096 == 0)
                {
                    checkpoint?.Invoke();
                }
            }

            result.Add(new ColumnMeasure(min, max, unbreakable, count > 0 ? total / count : 0, numbers > count - numbers));
        }

        return result;
    }

    /// <summary>
    /// One table of a band of columns: rows <paramref name="start"/> to <paramref name="end"/>, the first
    /// <paramref name="headerRows"/> of which repeat on every page. Merged cells are cut at the band's and the header's
    /// edges; text that does not wrap runs on over empty cells beside it, as in a spreadsheet. With
    /// <paramref name="headerFrom"/>, the header rows are those from there, repeated above rows [<paramref name="start"/>,
    /// <paramref name="end"/>) (after a page break set by hand).
    /// </summary>
    private static TableBlock FormattedTable(FormattedContext context, ColumnBand band, int start, int end, int headerRows, bool breakBefore, Bookmark? bookmark, int? headerFrom = null)
    {
        // The table's rows: the header rows and the body, each a group that merged cells are cut at.
        var header = headerFrom is { } from ? (Start: from, End: from + headerRows) : (Start: start, End: start + Math.Min(headerRows, end - start));
        var body = headerFrom is null ? (Start: header.End, End: end) : (Start: start, End: end);
        int[] rows = [.. Enumerable.Range(header.Start, header.End - header.Start), .. Enumerable.Range(body.Start, Math.Max(0, body.End - body.Start))];
        var sheet = context.Sheet;

        // Header rows repeated after a page break set by hand show no drawings, as on the pages a table continues on.
        var drawingRows = headerFrom is null ? rows : [.. rows.Select((r, i) => i < header.End - header.Start ? -1 : r)];
        var marks = new List<CellMark>();
        var tableRows = new List<TableRow>(rows.Length);

        var spans = new int[band.Count];
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var isHeader = index < header.End - header.Start;
            var (groupStart, groupEnd) = isHeader ? header : body;
            PlanRow(context, band, row, groupStart, spans);
            var cells = new List<TableCell>(band.Count);
            for (var i = 0; i < band.Count; i++)
            {
                if (spans[i] <= 0)
                {
                    continue;
                }

                var column = band.Start + i;
                if (context.MergeAt(row, column) is { } merge)
                {
                    // The part of the merged range in this band and row group, with the content only in its first part.
                    var rowSpan = Math.Min(merge.Row + merge.RowSpan, groupEnd) - row;
                    var anchor = sheet[merge.Row, merge.Column];
                    var content = merge.Row == row && merge.Column == column ? anchor : anchor with { Text = string.Empty, Kind = CellKind.Empty };
                    cells.Add(FormattedCell(context, content, band, i, spans[i], rowSpan));
                    Mark(content, rowSpan);
                }
                else
                {
                    cells.Add(FormattedCell(context, sheet[row, column], band, i, spans[i], 1));
                    Mark(sheet[row, column], 1);
                }

                void Mark(SheetCell content, int rowSpan)
                {
                    var comment = context.CommentAt(row, column);
                    if (IsMarked(content, comment))
                    {
                        marks.Add(new CellMark(index, i, spans[i], rowSpan, content, comment));
                    }
                }
            }

            tableRows.Add(new TableRow(cells)
            {
                IsHeader = isHeader,
                MinHeight = context.RowHeight(row),
            });
        }

        var table = new TableBlock(new BoxStyle
        {
            Width = band.Widths.Sum(),
            BreakBefore = breakBefore ? PageBreak.Page : PageBreak.Auto,
        })
        {
            LayoutMode = TableLayoutMode.Fixed,
            BorderCollapse = BorderCollapse.Collapse,
            ColumnWidths = [.. band.Widths.Select(w => (Length?)w)],
            Direction = context.Format.RightToLeft ? TextDirection.RightToLeft : TextDirection.LeftToRight,
            Bookmark = bookmark,
            Overlay = TableOverlay(context, band, drawingRows, marks),
        };
        foreach (var tableRow in tableRows)
        {
            table.Add(tableRow);
        }

        return table;
    }

    /// <summary>
    /// Plans the cells of a row in a band: <paramref name="spans"/>[i] is the number of columns the cell starting at
    /// column i spans, or 0 where column i is covered (by a merged range or by text running on from beside it).
    /// </summary>
    private static void PlanRow(FormattedContext context, ColumnBand band, int row, int groupStart, int[] spans)
    {
        var sheet = context.Sheet;
        Array.Fill(spans, 1);
        for (var i = 0; i < band.Count; i++)
        {
            var column = band.Start + i;
            if (context.MergeAt(row, column) is not { } merge)
            {
                continue;
            }

            // A merged range starts a cell where its first row and column are, or where the row group or band cuts it.
            var firstRow = Math.Max(merge.Row, groupStart);
            var firstColumn = Math.Max(merge.Column, band.Start);
            spans[i] = row == firstRow && column == firstColumn
                ? Math.Min(merge.Column + merge.ColumnSpan, band.Start + band.Count) - column
                : 0;
        }

        for (var i = 0; i < band.Count; i++)
        {
            var cell = sheet[row, band.Start + i];
            // Text that wraps stays in its column, and so does text with a fill or borders, which would move with it.
            var format = cell.Format ?? CellFormat.Default;
            if (spans[i] != 1 || cell.Kind != CellKind.Text || context.MergeAt(row, band.Start + i) is not null || format.Wrap || format.IsVisible || format.ShrinkToFit
                || format.Rotation != 0)
            {
                continue;
            }

            var needed = context.TextWidth(cell) - band.Widths[i];
            if (needed <= 0)
            {
                continue;
            }

            // Runs on to the right for text at the start, to the left for text at the right, to both sides if centered.
            var rightToLeft = context.Format.RightToLeft;
            var toEnd = cell.Alignment switch
            {
                CellAlignment.Right => rightToLeft,
                CellAlignment.Left => !rightToLeft,
                _ => true,
            };
            int first = i, last = i;
            if (cell.Alignment == CellAlignment.Center)
            {
                while (needed > 0 && first > 0 && last + 1 < band.Count && IsFree(context, row, band.Start + first - 1, spans[first - 1])
                    && IsFree(context, row, band.Start + last + 1, spans[last + 1]))
                {
                    first--;
                    last++;
                    needed -= band.Widths[first] + band.Widths[last];
                }
            }
            else if (toEnd)
            {
                while (needed > 0 && last + 1 < band.Count && IsFree(context, row, band.Start + last + 1, spans[last + 1]))
                {
                    last++;
                    needed -= band.Widths[last];
                }
            }
            else
            {
                while (needed > 0 && first > 0 && IsFree(context, row, band.Start + first - 1, spans[first - 1]))
                {
                    first--;
                    needed -= band.Widths[first];
                }
            }

            if (first == last)
            {
                continue;
            }

            for (var k = first; k <= last; k++)
            {
                spans[k] = 0;
            }

            spans[first] = last - first + 1;
            if (first != i)
            {
                // The cell moves to the first column of its span: its text, from the column it was in.
                context.MoveText(row, band.Start + first, band.Start + i);
            }
        }
    }

    /// <summary>Whether text may run on over a cell: it is empty, shows no fill or border, and is not merged.</summary>
    private static bool IsFree(FormattedContext context, int row, int column, int span)
    {
        var cell = context.Sheet[row, column];
        return span == 1 && cell.Kind == CellKind.Empty && !(cell.Format?.IsVisible ?? false) && context.MergeAt(row, column) is null;
    }

    private static TableCell FormattedCell(FormattedContext context, SheetCell cell, ColumnBand band, int index, int columnSpan, int rowSpan)
    {
        var column = band.Start + index;
        if (context.TakeMovedText(column) is { } moved)
        {
            cell = moved;
        }

        var format = cell.Format ?? CellFormat.Default;
        var box = context.Box(format, cell.Alignment);

        // Text set to shrink to fit (and not to wrap) that is wider than its columns is shown smaller, to fit them.
        var factor = 1.0;
        if (format.ShrinkToFit && !format.Wrap && cell.Kind != CellKind.Empty && context.TextWidth(cell) is var needed and > 0)
        {
            var room = band.Widths.Skip(index).Take(columnSpan).Sum();
            factor = needed > room ? Math.Max(0.05, room / needed) : 1;
        }

        var style = context.Style(format, context.Scale * factor);

        // Rotated text is drawn over the cell once it is placed; a data bar or icon set may hide the value.
        var paragraph = DisplayText(cell.Text).Length == 0 || cell.Kind == CellKind.Empty || format.Rotation != 0 || format.HideText ? null
            : context.CellParagraph(cell, format, style, context.Scale * factor);
        return paragraph is null
            ? new TableCell() { Style = box, ColumnSpan = columnSpan, RowSpan = rowSpan, VerticalAlignment = format.Vertical }
            : new TableCell(paragraph) { Style = box, ColumnSpan = columnSpan, RowSpan = rowSpan, VerticalAlignment = format.Vertical };
    }

    /// <summary>The generic family of a font the collection may lack, from its name: serif, monospace or none.</summary>
    internal static string? GenericFamily(string name)
    {
        if (name.Contains("Mono", StringComparison.OrdinalIgnoreCase) || name.Contains("Courier", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Consolas", StringComparison.OrdinalIgnoreCase) || name.Contains("Console", StringComparison.OrdinalIgnoreCase))
        {
            return "monospace";
        }

        if (name.Contains("Sans", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] serif = ["Times", "Cambria", "Georgia", "Garamond", "Antiqua", "Palatino", "Century", "Bookman", "Baskerville", "Serif"];
        return serif.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)) ? "serif" : null;
    }

    /// <summary>The fonts, styles, boxes and merged cells of one formatted sheet, shared while it is laid out.</summary>
    private sealed class FormattedContext
    {
        private readonly SheetOptions _options;
        private readonly double _grid;
        private readonly FontCollection _collection;
        private readonly Dictionary<(string?, bool, bool), PdfFont[]> _fonts = [];
        private readonly Dictionary<(string?, double, bool, bool, CellUnderline, bool, PdfColor?), TextStyle> _styles = [];
        private readonly Dictionary<(CellFormat, CellAlignment), BoxStyle> _boxes = new(new FormatComparer());
        private readonly Dictionary<long, CellMerge> _merges = [];
        private readonly Dictionary<(int Row, double Scale), double> _rotatedHeights = [];
        private Dictionary<long, SheetComment>? _comments;
        private readonly Dictionary<int, SheetCell> _moved = [];
        private readonly Dictionary<(string, TextStyle), (double Min, double Max)> _widths = [];
        private double _digitWidth;
        private DrawingPainter? _painter;

        public FormattedContext(Sheet sheet, SheetFormatting format, SheetOptions options, double grid)
        {
            Sheet = sheet;
            Format = format;
            _options = options;
            _grid = grid;
            _collection = options.Fonts ?? BundledFonts.CreateCollection();
            foreach (var merge in format.Merges)
            {
                for (var r = merge.Row; r < merge.Row + merge.RowSpan; r++)
                {
                    for (var c = merge.Column; c < merge.Column + merge.ColumnSpan; c++)
                    {
                        _merges[Key(r, c)] = merge;
                    }
                }
            }
        }

        public Sheet Sheet { get; }

        public SheetFormatting Format { get; }

        /// <summary>The scale the table is laid out at (1 while it is measured).</summary>
        public double Scale { get; set; } = 1;

        /// <summary>The width of every column as laid out (in its band), in points.</summary>
        public double[] ColumnSizes { get; set; } = [];

        /// <summary>The width of every column the sheet asks for (or its content needs) at 100%, in points.</summary>
        public double[] NaturalWidths { get; set; } = [];

        /// <summary>Draws the items of the sheet's drawings with the sheet's fonts.</summary>
        public DrawingPainter Painter => _painter ??= new DrawingPainter((name, bold, italic) => Fonts(name, bold, italic), ChartFont);

        /// <summary>The family of chart text: the typeface, with the theme's fonts (<c>+mn-lt</c>, <c>+mj-lt</c>) resolved.</summary>
        private string? ChartFont(string? typeface) => typeface switch
        {
            null or "" or "+mn-lt" or "+mn-ea" or "+mn-cs" => Format.MinorFont ?? Format.DefaultFontName,
            "+mj-lt" or "+mj-ea" or "+mj-cs" => Format.MajorFont ?? Format.DefaultFontName,
            _ => typeface,
        };

        private static long Key(int row, int column) => ((long)row << 32) | (uint)column;

        /// <summary>The merged range a cell is part of, or <c>null</c>.</summary>
        public CellMerge? MergeAt(int row, int column) => _merges.Count > 0 && _merges.TryGetValue(Key(row, column), out var merge) ? merge : null;

        /// <summary>Whether a cell is merged with cells in other columns (it does not tell its column's width).</summary>
        public bool SpansColumns(int row, int column) => MergeAt(row, column) is { ColumnSpan: > 1 };

        /// <summary>
        /// The width the sheet gives a column in points at 100% (its width in characters times the widest digit of the
        /// default font), or <c>null</c> where it is fitted to its content.
        /// </summary>
        public double? ColumnWidth(int column)
        {
            if (column >= Format.ColumnWidths.Count || Format.ColumnWidths[column] is not { } characters)
            {
                return null;
            }

            if (_digitWidth == 0)
            {
                var style = Style(CellFormat.Default, 1);
                _digitWidth = "0123456789".Max(d => Measure(d.ToString(), style).Max);
            }

            return characters * _digitWidth;
        }

        /// <summary>The least height of a row at the scale.</summary>
        public double RowHeight(int row)
        {
            if (row < Format.RowHeights.Count && Format.RowHeights[row] is { } height)
            {
                return height * Scale;
            }

            var style = Style(CellFormat.Default, Scale);
            return Math.Max(style.LineHeight.Resolve(style.Fonts[0], style.FontSize) + (2 * FormattedPaddingY * style.FontSize), RotatedHeight(row));
        }

        /// <summary>The height the rotated text of a row needs (its turned bounds and padding), as Excel fits the row to it; 0 without any.</summary>
        private double RotatedHeight(int row)
        {
            if (_rotatedHeights.TryGetValue((row, Scale), out var needed))
            {
                return needed;
            }

            foreach (var cell in Sheet.Rows[row])
            {
                if (cell.Format is { Rotation: not 0, HideText: false } format && cell.Kind != CellKind.Empty)
                {
                    var (_, width, height) = RotatedText(cell, format);
                    var angle = format.Rotation * Math.PI / 180;
                    var size = (format.FontSize ?? Format.DefaultFontSize) * Scale;
                    needed = Math.Max(needed, (width * Math.Abs(Math.Sin(angle))) + (height * Math.Abs(Math.Cos(angle))) + (2 * FormattedPaddingY * size));
                }
            }

            _rotatedHeights[(row, Scale)] = needed;
            return needed;
        }

        /// <summary>
        /// The paragraph of a cell's text in a style at a scale: its runs of rich text each in its own font, stacked
        /// text one character a line.
        /// </summary>
        public Paragraph CellParagraph(SheetCell cell, CellFormat format, TextStyle style, double scale)
        {
            var paragraph = new Paragraph(style)
            {
                Alignment = cell.IsRightAligned ? TextAlignment.Right
                    : cell.Alignment == CellAlignment.Center ? TextAlignment.Center
                    : cell.Alignment == CellAlignment.Left ? TextAlignment.Left
                    : TextAlignment.Start,
                Direction = cell.IsRightAligned ? ParagraphDirection.LeftToRight : ParagraphDirection.Auto,
                BreakLongWords = true,
            };
            if (cell.Runs is { Count: > 0 } runs)
            {
                foreach (var run in runs)
                {
                    paragraph.Add(Shown(run.Text, format), run.Font is { } font ? Style(font.Apply(format), scale) : style);
                }
            }
            else
            {
                paragraph.Add(Shown(cell.Text, format));
            }

            return paragraph;
        }

        /// <summary>A text as a cell shows it: control characters cleaned out, stacked one character a line.</summary>
        private static string Shown(string text, CellFormat format)
        {
            text = DisplayText(text);
            if (!format.Stacked)
            {
                return text;
            }

            var characters = new List<string>();
            var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text.Replace("\n", string.Empty, StringComparison.Ordinal));
            while (elements.MoveNext())
            {
                characters.Add(elements.GetTextElement());
            }

            return string.Join('\n', characters);
        }

        /// <summary>The widest word and line of a cell's text at a scale (its rich text and stacking taken into account).</summary>
        public (double Min, double Max) MeasureCell(SheetCell cell, CellFormat format, TextStyle style, double scale)
        {
            var widths = cell.Runs is null && !format.Stacked ? Measure(DisplayText(cell.Text), style) : CellParagraph(cell, format, style, scale).MeasureIntrinsicWidths();
            if (format.Rotation == 0)
            {
                return widths;
            }

            // Rotated text takes the width of its turned bounds.
            var angle = format.Rotation * Math.PI / 180;
            var line = style.LineHeight.Resolve(style.Fonts[0], style.FontSize);
            var width = (widths.Max * Math.Abs(Math.Cos(angle))) + (line * Math.Abs(Math.Sin(angle)));
            return (width, width);
        }

        /// <summary>A cell's rotated text laid out on one line (its own lines kept) at the scale: the blocks, their width and height.</summary>
        public (Block[] Blocks, double Width, double Height) RotatedText(SheetCell cell, CellFormat format)
        {
            var paragraph = CellParagraph(cell, format, Style(format, Scale), Scale);
            var width = paragraph.MeasureIntrinsicWidths().Max + 1;
            Block[] blocks = [new ParagraphBlock(paragraph)];
            return (blocks, width, FlowDocument.MeasureBlocks(blocks, width));
        }

        /// <summary>The comment printed as a note at a cell, or <c>null</c>.</summary>
        public SheetComment? CommentAt(int row, int column)
        {
            if (Format.Comments.Printing != CommentPrinting.Notes)
            {
                return null;
            }

            _comments ??= Format.Comments.List.GroupBy(c => Key(c.Row, c.Column)).ToDictionary(g => g.Key, g => g.First());
            return _comments.GetValueOrDefault(Key(row, column));
        }

        /// <summary>The width a cell's text needs on one line at the scale, with its padding and indent.</summary>
        public double TextWidth(SheetCell cell)
        {
            var format = cell.Format ?? CellFormat.Default;
            var style = Style(format, Scale);
            var width = MeasureCell(cell, format, style, Scale).Max;
            return width + (((2 * FormattedPaddingX) + (format.Indent * IndentEms)) * style.FontSize) + _grid + Tolerance;
        }

        /// <summary>The widest word and line of a text in a style, measured once.</summary>
        public (double Min, double Max) Measure(string text, TextStyle style)
        {
            if (!_widths.TryGetValue((text, style), out var widths))
            {
                widths = new Paragraph(style).Add(text).MeasureIntrinsicWidths();
                _widths[(text, style)] = widths;
            }

            return widths;
        }

        /// <summary>Notes that the cell starting at column <paramref name="to"/> shows the text of column <paramref name="from"/>.</summary>
        public void MoveText(int row, int to, int from) => _moved[to] = Sheet[row, from];

        /// <summary>The text moved to a column for the row being built, once.</summary>
        public SheetCell? TakeMovedText(int column) => _moved.Remove(column, out var cell) ? cell : null;

        /// <summary>The fonts of a family (the workbook's name for it) and style, with the fallbacks.</summary>
        public PdfFont[] Fonts(string? name, bool bold, bool italic)
        {
            if (_fonts.TryGetValue((name, bold, italic), out var fonts))
            {
                return fonts;
            }

            var weight = bold ? 700 : 400;
            var families = new List<string>();
            if ((name ?? Format.DefaultFontName) is { Length: > 0 } family)
            {
                families.Add(family);
                if (GenericFamily(family) is { } generic)
                {
                    families.Add(generic);
                }
            }

            families.Add(_options.FontFamily);
            families.AddRange(_options.FallbackFontFamilies);
            fonts =
            [
                .. families.Select(f => _collection.Find(f, weight, italic))
                    .Append(BundledFonts.CreateCollection().Find("Liberation Sans", weight, italic))
                    .OfType<PdfFont>()
                    .Distinct(),
            ];
            _fonts[(name, bold, italic)] = fonts;
            return fonts;
        }

        /// <summary>The text style of a format at a scale.</summary>
        public TextStyle Style(CellFormat format, double scale)
        {
            var size = (format.FontSize ?? Format.DefaultFontSize) * scale;
            var key = (format.FontName, size, format.Bold, format.Italic, format.Underline, format.Strikethrough, format.Color);
            if (_styles.TryGetValue(key, out var style))
            {
                return style;
            }

            var color = format.Color ?? PdfColor.Black;
            var decorations = new List<TextDecoration>();
            if (format.Underline != CellUnderline.None)
            {
                decorations.Add(new TextDecoration(TextDecorationLines.Underline, color)
                {
                    Style = format.Underline == CellUnderline.Double ? TextDecorationStyle.Double : TextDecorationStyle.Solid,
                });
            }

            if (format.Strikethrough)
            {
                decorations.Add(new TextDecoration(TextDecorationLines.LineThrough, color));
            }

            style = new TextStyle(Fonts(format.FontName, format.Bold, format.Italic), size)
            {
                Color = color,
                Decorations = decorations,
            };
            _styles[key] = style;
            return style;
        }

        /// <summary>The box of a cell: padding (with the indent), borders over the gridlines, and fill.</summary>
        public BoxStyle Box(CellFormat format, CellAlignment alignment)
        {
            if (_boxes.TryGetValue((format, alignment), out var box))
            {
                return box;
            }

            var size = (format.FontSize ?? Format.DefaultFontSize) * Scale;
            var padX = FormattedPaddingX * size;
            var indent = format.Indent * IndentEms * size;
            var indentRight = alignment == CellAlignment.Right;
            // An icon of conditional formatting takes the start of the cell (the left, or the right of right-to-left sheets).
            var icon = format.Icon is null ? 0 : size * 1.25;
            var (iconLeft, iconRight) = Format.RightToLeft ? (0.0, icon) : (icon, 0.0);
            box = new BoxStyle
            {
                Padding = new Thickness(FormattedPaddingY * size, padX + (indentRight ? indent : 0) + iconRight, FormattedPaddingY * size, padX + (indentRight ? 0 : indent) + iconLeft),
                Border = new Borders(Side(format.Top), Side(format.Right), Side(format.Bottom), Side(format.Left)),
                Background = format.Fill,
                BackgroundLayers = format.DataBar is { } bar ? [DataBarLayer(Format.RightToLeft ? bar with { Start = 1 - bar.End, End = 1 - bar.Start } : bar)] : [],
            };
            _boxes[(format, alignment)] = box;
            return box;
        }

        private BorderSide Side(CellBorder border) => border.IsVisible
            ? new BorderSide(border.Width, border.Color, border.Style)
            : _grid > 0 ? new BorderSide(_grid, GridColor) : default;

        /// <summary>Formats compared by reference (they are shared per workbook style), alignments by value.</summary>
        private sealed class FormatComparer : IEqualityComparer<(CellFormat, CellAlignment)>
        {
            public bool Equals((CellFormat, CellAlignment) x, (CellFormat, CellAlignment) y) => ReferenceEquals(x.Item1, y.Item1) && x.Item2 == y.Item2;

            public int GetHashCode((CellFormat, CellAlignment) obj) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Item1), obj.Item2);
        }
    }
}

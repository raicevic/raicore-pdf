using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Sheets;

/// <summary>
/// What is drawn over a worksheet's cells once its table is placed (with the drawings, <see cref="DrawingOverlay"/>):
/// rotated text, the icons of icon sets, and comments as PDF notes; the data bars of conditional formatting (behind the
/// text, as the cells' backgrounds); and the list of comments printed after the sheet.
/// </summary>
internal static partial class SheetRenderer
{
    /// <summary>The size of a comment's note icon, in points.</summary>
    private const double NoteSize = 9;

    /// <summary>
    /// A cell with something drawn over it: its row in the table, its first column in the band, the columns and rows it
    /// spans, its content, and its comment when comments are printed as notes.
    /// </summary>
    private sealed record CellMark(int TableRow, int Index, int ColumnSpan, int RowSpan, SheetCell Cell, SheetComment? Comment);

    /// <summary>Whether a cell shows something over it: rotated text, an icon, or a comment as a note.</summary>
    private static bool IsMarked(SheetCell cell, SheetComment? comment) =>
        comment is not null || (cell.Format is { } format && (format.Icon is not null || (format.Rotation != 0 && cell.Kind != CellKind.Empty && !format.HideText)));

    /// <summary>The overlay of a table: its drawings, and what is drawn over its marked cells.</summary>
    private static Func<TablePlacement, IEnumerable<(int Page, Action<PdfCanvas, double> Draw)>>? TableOverlay(FormattedContext context, ColumnBand band,
        int[] drawingRows, List<CellMark> marks)
    {
        var drawings = DrawingOverlay(context, band, drawingRows);
        if (marks.Count == 0)
        {
            return drawings;
        }

        return placement => (drawings?.Invoke(placement) ?? []).Concat(PlaceMarks(context, band, marks, placement));
    }

    private static IEnumerable<(int Page, Action<PdfCanvas, double> Draw)> PlaceMarks(FormattedContext context, ColumnBand band, List<CellMark> marks, TablePlacement placement)
    {
        var edges = placement.ColumnEdges;
        if (edges.Count != band.Count + 1)
        {
            yield break;
        }

        var pieces = placement.Rows.GroupBy(p => p.Row).ToDictionary(g => g.Key, g => g.ToList());
        var rightToLeft = context.Format.RightToLeft;
        foreach (var mark in marks)
        {
            if (!pieces.TryGetValue(mark.TableRow, out var placed))
            {
                continue;
            }

            var (first, last) = (mark.Index, Math.Min(mark.Index + mark.ColumnSpan, band.Count));
            var (left, right) = rightToLeft ? (edges[band.Count - last], edges[band.Count - first]) : (edges[first], edges[last]);
            foreach (var piece in placed.Where((p, i) => i == 0 || placed[i - 1].Page != p.Page))
            {
                // The cell's height: its row's piece, and those of the rows it spans below on the same page.
                var bottom = piece.Top + piece.Height;
                for (var r = mark.TableRow + 1; r < mark.TableRow + mark.RowSpan; r++)
                {
                    if (pieces.TryGetValue(r, out var below) && below.FirstOrDefault(p => p.Page == piece.Page && p.Repeated == piece.Repeated) is { Height: > 0 } next)
                    {
                        bottom = Math.Max(bottom, next.Top + next.Height);
                    }
                }

                var (top, height) = (piece.Top, bottom - piece.Top);
                var withNote = !piece.Repeated && mark.Comment is not null;
                yield return (piece.Page, (canvas, pageHeight) => DrawMark(canvas, context, mark, left, pageHeight - top - height, right - left, height, withNote));
            }
        }
    }

    /// <summary>Draws over a cell (its rectangle in page coordinates, from its bottom left corner): its icon, rotated text and note.</summary>
    private static void DrawMark(PdfCanvas canvas, FormattedContext context, CellMark mark, double x, double y, double width, double height, bool withNote)
    {
        var format = mark.Cell.Format ?? CellFormat.Default;
        var size = (format.FontSize ?? context.Format.DefaultFontSize) * context.Scale;
        var (padX, padY) = (FormattedPaddingX * size, FormattedPaddingY * size);
        if (format.Icon is { } icon && height > 2)
        {
            // At the start of the cell, as high as a line of its text, aligned as the text is.
            var side = Math.Min(size * 1.05, height - 2);
            var iconY = format.Vertical switch
            {
                VerticalAlignment.Top => y + height - padY - side,
                VerticalAlignment.Middle => y + ((height - side) / 2),
                _ => y + padY,
            };
            SheetIcons.Draw(canvas, icon, context.Format.RightToLeft ? x + width - padX - side : x + padX, iconY, side);
        }

        if (format.Rotation != 0 && mark.Cell.Kind != CellKind.Empty && !format.HideText)
        {
            DrawRotated(canvas, context, mark.Cell, format, x, y, width, height, padX, padY);
        }

        if (withNote && mark.Comment is { } comment)
        {
            // At the top right corner of the cell (top left, right to left), where Excel shows its red mark.
            var side = Math.Min(NoteSize, Math.Min(width, height));
            var noteX = context.Format.RightToLeft ? x : x + width - side;
            canvas.AddNote(noteX, y + height - side, side, side, comment.Text).Author = comment.Author;
        }
    }

    /// <summary>
    /// A cell's text turned by its rotation (counterclockwise) about its center, the turned text's bounds aligned in the
    /// cell as its text is (horizontally by its alignment, vertically by its vertical alignment), clipped to the cell.
    /// </summary>
    private static void DrawRotated(PdfCanvas canvas, FormattedContext context, SheetCell cell, CellFormat format, double x, double y, double width, double height, double padX, double padY)
    {
        var (blocks, w, h) = context.RotatedText(cell, format);
        var angle = format.Rotation * Math.PI / 180;
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        var (boundsWidth, boundsHeight) = ((w * Math.Abs(cos)) + (h * Math.Abs(sin)), (w * Math.Abs(sin)) + (h * Math.Abs(cos)));
        var cx = cell.Alignment == CellAlignment.Center ? x + (width / 2)
            : cell.IsRightAligned != context.Format.RightToLeft ? x + width - padX - (boundsWidth / 2)
            : x + padX + (boundsWidth / 2);
        var cy = format.Vertical switch
        {
            VerticalAlignment.Top => y + height - padY - (boundsHeight / 2),
            VerticalAlignment.Middle => y + (height / 2),
            _ => y + padY + (boundsHeight / 2),
        };
        canvas.SaveState();
        canvas.Rectangle(x, y, width, height).Clip().EndPath();
        canvas.Transform(cos, sin, -sin, cos, cx, cy);
        FlowDocument.DrawBlocks(canvas, blocks, -w / 2, h / 2, w);
        canvas.RestoreState();
    }

    /// <summary>The width of a data bar's border and of its axis line, in points (a pixel of Excel's).</summary>
    internal const double DataBarLine = 0.75;

    /// <summary>The height of a data bar's bars, in percent of the cell's height.</summary>
    private const double DataBarHeight = 76;

    /// <summary>
    /// A data bar as a cell's background layers (the first on top): its axis as a dashed line across the cell, and a bar
    /// between its start and end across the cell, most of its height, in its color (fading toward white along a gradient
    /// bar), inside its border. <paramref name="dashes"/> gives the dash of an axis line of a color (one image a
    /// document shares).
    /// </summary>
    internal static IReadOnlyList<BackgroundLayer> DataBarLayers(CellDataBar bar, Func<PdfColor, PdfImage> dashes)
    {
        var (start, end) = (Math.Clamp(Math.Min(bar.Start, bar.End), 0, 1), Math.Clamp(Math.Max(bar.Start, bar.End), 0, 1));
        var length = end - start;
        var color = bar.Color;
        var faded = new PdfColor(color.Red + ((1 - color.Red) * 0.75), color.Green + ((1 - color.Green) * 0.75), color.Blue + ((1 - color.Blue) * 0.75));
        var layers = new List<BackgroundLayer>(3);
        if (bar.Axis is { } axis)
        {
            // A position of p% puts the image's point p% along at the box's point p% along: points move the line's
            // middle to the axis.
            var at = Math.Clamp(axis, 0, 1);
            layers.Add(new BackgroundLayer(new ImageBackground(dashes(bar.AxisColor ?? PdfColor.Black)))
            {
                RepeatX = BackgroundRepeat.NoRepeat,
                Size = new BackgroundSize(BackgroundSizeKind.Explicit, new Length(DataBarLine), new Length(2 * DataBarLine)),
                PositionX = new BackgroundPosition(new Length(DataBarLine * (at - 0.5), at * 100)),
            });
        }

        // A position of p% puts the bar's point p% along at the cell's point p% along: the bar's start at start of the
        // cell's width. A border is a bar of its color under the bar, which is smaller by its width on each side.
        var position = length < 1 ? start / (1 - length) : 0;
        var fill = new LinearGradient(90, [new GradientStop(color), new GradientStop(bar.Gradient ? faded : color)]);
        var inset = bar.Border is null ? 0 : DataBarLine;
        layers.Add(BarLayer(fill, inset));
        if (bar.Border is { } border)
        {
            layers.Add(BarLayer(new LinearGradient(90, [new GradientStop(border), new GradientStop(border)]), 0));
        }

        return layers;

        BackgroundLayer BarLayer(BackgroundImage image, double inset) => new(image)
        {
            RepeatX = BackgroundRepeat.NoRepeat,
            RepeatY = BackgroundRepeat.NoRepeat,
            Size = new BackgroundSize(BackgroundSizeKind.Explicit, new Length(-2 * inset, length * 100), new Length(-2 * inset, DataBarHeight)),
            PositionX = new BackgroundPosition(new Length(inset * (1 - (2 * position)), position * 100)),
            PositionY = BackgroundPosition.Center,
        };
    }

    /// <summary>The dash of a data bar's axis line: a square of its color over the top half of its height.</summary>
    internal static PdfImage AxisDash(PdfColor color) =>
        PdfImage.FromDrawing(DataBarLine, 2 * DataBarLine, canvas => canvas.SetFillColor(color).Rectangle(0, DataBarLine, DataBarLine, DataBarLine).Fill());

    /// <summary>
    /// The comments printed after a sheet, on a page of their own: for each, its cell and its text, as Excel prints them
    /// ("At end of sheet"), in the sheet's default font.
    /// </summary>
    private static TableBlock CommentList(FormattedContext context, IReadOnlyList<SheetComment> comments, double width)
    {
        var size = context.Format.DefaultFontSize * context.Scale;
        var style = new TextStyle(context.Fonts(context.Format.DefaultFontName, false, false), size);
        var label = size * 5.5;
        var table = new TableBlock(new BoxStyle { Width = width, BreakBefore = PageBreak.Page })
        {
            LayoutMode = TableLayoutMode.Fixed,
            ColumnWidths = [label, Math.Max(1, width - label)],
        };
        var space = new BoxStyle { Padding = new Thickness(0, size * 0.3, size, 0) };
        var tight = new BoxStyle { Padding = new Thickness(0, size * 0.3, 0, 0) };
        foreach (var comment in comments)
        {
            table.Add(new TableRow(
                new TableCell(new Paragraph(style).Add("Cell:")) { Style = tight },
                new TableCell(new Paragraph(style).Add(comment.Reference)) { Style = tight }));
            table.Add(new TableRow(
                new TableCell(new Paragraph(style).Add("Comment:")) { Style = space },
                new TableCell(new Paragraph(style) { BreakLongWords = true }.Add(DisplayText(comment.Text))) { Style = space }));
        }

        return table;
    }
}

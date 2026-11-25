using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets.Drawings;

namespace RaiCore.Pdf.Sheets;

/// <summary>
/// The layout of chart sheets: a page of their own, on which their drawings (the chart, with an absolute anchor) are
/// stretched over the page's content area (below the sheet's name, when it is shown), as spreadsheets print a chart sheet
/// on a full page; the chart is laid out again at that size, so its text keeps its size. Headers and footers are drawn
/// as on worksheets.
/// </summary>
internal static partial class SheetRenderer
{
    private static FlowDocument LayoutChartSheet(Sheet sheet, SheetFormatting format, SheetOptions options, PageSetup page)
    {
        var context = new FormattedContext(sheet, format, options, 0);
        var flow = new FlowDocument(page);
        var bookmark = format.Bookmark is { } name ? new Bookmark(name) : null;
        var size = format.DefaultFontSize;
        Block first;
        if (format.Heading is { } heading)
        {
            var style = new TextStyle(context.Fonts(null, bold: true, italic: false), size * HeadingScale);
            first = new ParagraphBlock(new Paragraph(style).Add(DisplayText(heading)), new BoxStyle
            {
                Margin = new Thickness(0, 0, size * 0.8, 0),
            })
            {
                Bookmark = bookmark,
                Tag = new StructureTag("H1"),
            };
        }
        else
        {
            first = new ParagraphBlock(new Paragraph(new TextStyle(context.Fonts(null, false, false), size))) { Bookmark = bookmark };
        }

        flow.Add(first);
        var top = format.Heading is null ? 0 : FlowDocument.MeasureBlocks([first], Math.Max(1, page.ContentWidth));
        flow.PageDecorator = (canvas, info) =>
        {
            if (info.Number == 1)
            {
                DrawChartSheet(canvas, info.Setup, top, context);
            }

            if (format.HeaderFooter is { IsEmpty: false } headerFooter)
            {
                DrawHeaderFooter(canvas, info, headerFooter, context);
            }
        };
        return flow;
    }

    /// <summary>
    /// Draws a chart sheet's drawings: the box they cover together stretched over the page's content area, from
    /// <paramref name="top"/> points below its top.
    /// </summary>
    private static void DrawChartSheet(PdfCanvas canvas, PageSetup setup, double top, FormattedContext context)
    {
        var drawings = context.Format.Drawings;
        var margins = setup.UsedMargins;
        var (areaLeft, areaTop) = (margins.Left, margins.Top + top);
        var areaWidth = setup.Size.Width - margins.Left - margins.Right;
        var areaHeight = setup.Size.Height - areaTop - margins.Bottom;
        if (drawings.Count == 0 || areaWidth < 1 || areaHeight < 1)
        {
            return;
        }

        var boxes = drawings.Select(ChartSheetBox).ToList();
        var (left, upper) = (boxes.Min(b => b.X), boxes.Min(b => b.Y));
        var (right, lower) = (boxes.Max(b => b.X + b.Width), boxes.Max(b => b.Y + b.Height));
        var sx = right > left ? areaWidth / (right - left) : 1;
        var sy = lower > upper ? areaHeight / (lower - upper) : 1;
        var count = 0;
        foreach (var (drawing, box) in drawings.Zip(boxes))
        {
            if (++count % 1024 == 0)
            {
                ResourceLimits.Checkpoint();
            }

            var (w, h) = (box.Width * sx, box.Height * sy);
            var x = areaLeft + ((box.X - left) * sx);
            var y = setup.Size.Height - areaTop - ((box.Y - upper) * sy) - h;
            canvas.SaveState();
            canvas.Rectangle(areaLeft, setup.Size.Height - areaTop - areaHeight, areaWidth, areaHeight).Clip().EndPath();
            canvas.Transform(1, 0, 0, 1, x, y);
            context.Painter.Draw(canvas, drawing.Item, w, h);
            canvas.RestoreState();
        }
    }

    /// <summary>
    /// A chart sheet's drawing's box in points (y downwards): where its absolute anchor puts it, with its size; other
    /// anchors (which chart sheets do not have) at the origin with their size.
    /// </summary>
    private static PdfRectangle ChartSheetBox(SheetDrawing drawing)
    {
        static double Finite(double value) => double.IsFinite(value) ? Math.Clamp(value, -1e7, 1e7) : 0;
        var (x, y) = drawing.Kind == AnchorKind.Absolute ? (Finite(drawing.Left.Points), Finite(drawing.Top.Points)) : (0, 0);
        return new PdfRectangle(x, y, Math.Max(0, Finite(drawing.Width)), Math.Max(0, Finite(drawing.Height)));
    }
}

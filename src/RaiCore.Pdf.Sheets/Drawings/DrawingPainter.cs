using RaiCore.Pdf.Charts;
using RaiCore.Pdf.DrawingML;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Sheets.Drawings;

/// <summary>
/// Draws the items of a sheet's drawings (pictures, shapes with their text, charts and groups) into boxes on a canvas,
/// at 100%: the caller scales the canvas by the sheet's print scale.
/// </summary>
/// <param name="fonts">The fonts of a family (or <c>null</c> for the sheet's default), bold and italic, in fallback order.</param>
/// <param name="chartFont">The family of chart text for a chart's typeface (theme fonts such as <c>+mn-lt</c> resolved).</param>
internal sealed class DrawingPainter(Func<string?, bool, bool, PdfFont[]> fonts, Func<string?, string?> chartFont)
{
    /// <summary>The deepest groups drawn (the readers bound them already; this keeps drawing safe on its own).</summary>
    private const int MaxDepth = 64;

    /// <summary>
    /// Draws an item into the box from (0, 0) to (<paramref name="width"/>, <paramref name="height"/>) of the canvas's
    /// current coordinates (y upwards, the box's bottom-left corner at the origin).
    /// </summary>
    public void Draw(PdfCanvas canvas, DrawingItem item, double width, double height) => Draw(canvas, item, width, height, 0);

    private void Draw(PdfCanvas canvas, DrawingItem item, double width, double height, int depth)
    {
        // Nothing as small as a point in both directions shows (Excel keeps empty text boxes of no size).
        if (depth > MaxDepth || !double.IsFinite(width) || !double.IsFinite(height) || width < 0 || height < 0 || (width < 1 && height < 1))
        {
            return;
        }

        canvas.SaveState();
        if (item.Rotation != 0 || item.FlipH || item.FlipV)
        {
            // About the box's center: mirrored, then turned clockwise (y points up here, so the angle is negated).
            var (cx, cy) = (width / 2, height / 2);
            var radians = -item.Rotation * Math.PI / 180;
            var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
            var (sx, sy) = (item.FlipH ? -1.0 : 1.0, item.FlipV ? -1.0 : 1.0);
            canvas.Transform(1, 0, 0, 1, cx, cy);
            canvas.Transform(cos * sx, sin * sx, -sin * sy, cos * sy, 0, 0);
            canvas.Transform(1, 0, 0, 1, -cx, -cy);
        }

        switch (item)
        {
            case PictureItem picture:
                DrawPicture(canvas, picture, width, height);
                break;
            case ShapeItem shape:
                DrawShape(canvas, shape, width, height);
                break;
            case ChartItem chart when width > 1 && height > 1:
                ChartRenderer.Draw(canvas, chart.Chart, 0, 0, width, height, new ChartRenderOptions
                {
                    Fonts = (typeface, bold, italic) => fonts(chartFont(typeface), bold, italic),
                    DefaultFontSize = 10,
                    OpaqueChartArea = true,
                });
                break;
            case GroupItem group:
                DrawGroup(canvas, group, width, height, depth);
                break;
        }

        canvas.RestoreState();
    }

    private void DrawGroup(PdfCanvas canvas, GroupItem group, double width, double height, int depth)
    {
        // Without coordinates of its own, a group's are those its children cover.
        var bounds = group.ChildBounds is { Width: > 0, Height: > 0 } own ? own : Union(group.Children.Select(c => c.Box));
        var sx = bounds.Width > 0 ? width / bounds.Width : 1;
        var sy = bounds.Height > 0 ? height / bounds.Height : 1;
        foreach (var (child, box) in group.Children)
        {
            var (w, h) = (box.Width * sx, box.Height * sy);
            var left = (box.X - bounds.X) * sx;
            var top = (box.Y - bounds.Y) * sy;
            if (!double.IsFinite(left + top + w + h))
            {
                continue;
            }

            canvas.SaveState();
            canvas.Transform(1, 0, 0, 1, left, height - top - h);
            Draw(canvas, child, w, h, depth + 1);
            canvas.RestoreState();
        }
    }

    /// <summary>The smallest rectangle around rectangles.</summary>
    private static PdfRectangle Union(IEnumerable<PdfRectangle> boxes)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var box in boxes)
        {
            (left, top) = (Math.Min(left, box.X), Math.Min(top, box.Y));
            (right, bottom) = (Math.Max(right, box.X + box.Width), Math.Max(bottom, box.Y + box.Height));
        }

        return right > left && bottom > top ? new PdfRectangle(left, top, right - left, bottom - top) : default;
    }

    /// <summary>Draws a picture: the cropped image clipped to its box (or geometry), then its border.</summary>
    private static void DrawPicture(PdfCanvas canvas, PictureItem picture, double width, double height)
    {
        if (width > 0 && height > 0)
        {
            var (l, t, r, b) = picture.Crop;
            var shownX = 1 - l - r;
            var shownY = 1 - t - b;
            if (shownX > 1e-6 && shownY > 1e-6)
            {
                // The whole image is scaled so that the part left after the crop fills the box.
                var (w, h) = (width / shownX, height / shownY);
                canvas.SaveState();
                if (picture.Geometry is { } geometry)
                {
                    Emit(canvas, geometry.Build(width, height).Where(p => p.Filled).SelectMany(p => p.Segments), height);
                }
                else
                {
                    canvas.Rectangle(0, 0, width, height);
                }

                canvas.Clip().EndPath();
                canvas.DrawImage(picture.Image, -l * w, -b * h, w, h);
                canvas.RestoreState();
            }
        }

        if (picture.Line is { } line)
        {
            var paths = (picture.Geometry ?? Geometry.Rectangle).Build(width, height);
            foreach (var path in paths.Where(p => p.Stroked))
            {
                Stroke(canvas, path, line, height);
            }
        }
    }

    private void DrawShape(PdfCanvas canvas, ShapeItem shape, double width, double height)
    {
        if (shape.Fill is not null || shape.Line is not null)
        {
            var paths = shape.Geometry.Build(width, height);
            if (shape.Fill is { Alpha: > 0 } fill && !shape.Geometry.IsLine)
            {
                foreach (var path in paths.Where(p => p.Filled && p.Segments.Count > 0))
                {
                    canvas.SetFillColor(fill);
                    Emit(canvas, path.Segments, height);
                    canvas.Fill();
                }
            }

            if (shape.Line is { } line)
            {
                foreach (var path in paths.Where(p => p.Stroked))
                {
                    Stroke(canvas, path, line, height);
                }
            }
        }

        if (shape.Text is { } text && shape.Geometry.TextBounds(width, height) is { Width: >= 1, Height: >= 1 } bounds)
        {
            DrawText(canvas, text, bounds, height);
        }
    }

    /// <summary>Strokes a path with a line's width, color and dashes.</summary>
    private static void Stroke(PdfCanvas canvas, GeometryPath path, ShapeLine line, double height)
    {
        if (line.Color.Alpha <= 0 || path.Segments.Count < 2)
        {
            return;
        }

        canvas.SaveState();
        canvas.SetStrokeColor(line.Color);
        canvas.SetLineWidth(line.Width);
        if (line.Dash.Count > 0 && line.Width > 0)
        {
            canvas.SetDash([.. line.Dash.Select(d => d * line.Width)]);
        }

        Emit(canvas, path.Segments, height);
        canvas.Stroke();
        canvas.RestoreState();
    }

    /// <summary>Adds segments in a box's coordinates (y downwards from its top) to the current path (y upwards).</summary>
    private static void Emit(PdfCanvas canvas, IEnumerable<Segment> segments, double height)
    {
        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Move:
                    canvas.MoveTo(segment.X, height - segment.Y);
                    break;
                case SegmentKind.Line:
                    canvas.LineTo(segment.X, height - segment.Y);
                    break;
                case SegmentKind.Cubic:
                    canvas.CurveTo(segment.X1, height - segment.Y1, segment.X2, height - segment.Y2, segment.X, height - segment.Y);
                    break;
                case SegmentKind.Close:
                    canvas.ClosePath();
                    break;
            }
        }
    }

    /// <summary>Lays out and draws a shape's text in its text rectangle (y downwards in the shape), inside the insets.</summary>
    private void DrawText(PdfCanvas canvas, ShapeText text, (double X, double Y, double Width, double Height) bounds, double height)
    {
        var insets = text.Insets;
        var width = Math.Max(1, bounds.Width - insets.Left - insets.Right);
        var blocks = new List<Block>();
        foreach (var paragraph in text.Paragraphs)
        {
            var size = paragraph.Runs.Count > 0 ? paragraph.Runs.Max(r => r.Size) : 11;
            var first = paragraph.Runs.Count > 0 ? paragraph.Runs[0] : null;
            var style = new TextStyle(fonts(first?.Font, first?.Bold ?? false, first?.Italic ?? false), size);
            var built = new Paragraph(style) { Alignment = paragraph.Alignment, BreakLongWords = true };
            foreach (var run in paragraph.Runs)
            {
                built.Add(SheetRenderer.DisplayText(run.Text), new TextStyle(fonts(run.Font, run.Bold, run.Italic), run.Size)
                {
                    Color = run.Color,
                    Decorations = run.Underline ? [new TextDecoration(TextDecorationLines.Underline, run.Color)] : [],
                });
            }

            blocks.Add(new ParagraphBlock(built));
        }

        if (blocks.Count == 0)
        {
            return;
        }

        // Text that does not wrap is laid out as wide as it needs (it overflows the shape, as in Excel).
        var layoutWidth = text.Wrap ? width : Math.Max(width, blocks.Max(b => ((ParagraphBlock)b).Paragraph.MeasureIntrinsicWidths().Max) + 1);
        var needed = FlowDocument.MeasureBlocks(blocks, layoutWidth);
        var room = bounds.Height - insets.Top - insets.Bottom;
        var offset = text.Anchor switch
        {
            VerticalAlignment.Middle => (room - needed) / 2,
            VerticalAlignment.Bottom => room - needed,
            _ => 0,
        };
        var left = bounds.X + insets.Left - (text.Wrap ? 0 : AlignmentShift(blocks, layoutWidth - width));
        FlowDocument.DrawBlocks(canvas, blocks, left, height - (bounds.Y + insets.Top + offset), layoutWidth);
    }

    /// <summary>How far text laid out wider than its box moves left so that it stays aligned as its paragraphs ask.</summary>
    private static double AlignmentShift(List<Block> blocks, double extra) =>
        ((ParagraphBlock)blocks[0]).Paragraph.Alignment switch
        {
            TextAlignment.Center => extra / 2,
            TextAlignment.Right or TextAlignment.End => extra,
            _ => 0,
        };
}

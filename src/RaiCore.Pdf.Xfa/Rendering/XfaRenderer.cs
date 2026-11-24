using RaiCore.Pdf.Xfa.Form;
using RaiCore.Pdf.Xfa.Layout;
using RaiCore.Pdf.Xfa.Template;

namespace RaiCore.Pdf.Xfa.Rendering;

/// <summary>
/// Draws laid out pages: each page area's own objects, then the form content placed in its content areas, with fills,
/// borders, captions, text, images, shapes and check marks. Coordinates are in points, y downwards from the page's top
/// left, mapped to the PDF page.
/// </summary>
/// <remarks>Partial: shapes and borders in <c>XfaRenderer.Shapes.cs</c>.</remarks>
internal sealed partial class XfaRenderer(PdfDocument document)
{
    /// <summary>Draws the pages; <paramref name="boilerplate"/> gives each page area's own objects, laid out.</summary>
    public void Render(IReadOnlyList<LaidPage> pages, Func<FormNode, IReadOnlyList<LayoutBox>> boilerplate)
    {
        foreach (var page in pages)
        {
            ResourceLimits.Checkpoint();
            var pdfPage = document.AddPage(new PdfPageSize(page.Width, page.Height));
            var canvas = pdfPage.Canvas;
            var toPdf = new Affine(1, 0, 0, -1, 0, page.Height);
            foreach (var box in boilerplate(page.PageArea))
            {
                Draw(canvas, box, toPdf);
            }

            foreach (var (box, x, y) in page.Items)
            {
                Draw(canvas, box, Affine.Translate(x - box.X, y - box.Y).Then(toPdf));
            }
        }
    }

    /// <summary>Draws a box placed in its parent's content region (<paramref name="parent"/> maps that to the page).</summary>
    private static void Draw(PdfCanvas canvas, LayoutBox box, Affine parent)
    {
        using var nesting = ResourceLimits.EnterNesting();
        var toPage = box.Frame.Then(parent);
        if (!toPage.IsFinite)
        {
            return;
        }

        if (box.Node is null)
        {
            foreach (var child in box.Children)
            {
                Draw(canvas, child, toPage);
            }

            return;
        }

        if (!box.Node.IsVisible)
        {
            return;
        }

        if (box.Content is { } content)
        {
            DrawWidget(canvas, box, content, toPage);
            return;
        }

        if (Props.ReadBorder(box.Node.Template.Child("border")) is { } border)
        {
            DrawBorder(canvas, border, 0, 0, box.W, box.H, toPage);
        }

        var inner = Affine.Translate(box.Margin.Left, box.Margin.Top).Then(toPage);
        foreach (var child in box.Children)
        {
            Draw(canvas, child, inner);
        }
    }

    private static void DrawWidget(PdfCanvas canvas, LayoutBox box, WidgetContent content, Affine toPage)
    {
        if (content.Border is { } border)
        {
            DrawBorder(canvas, border, 0, 0, box.W, box.H, toPage);
        }

        var m = content.Margin;
        var (ix, iy, iw, ih) = (m.Left, m.Top, Math.Max(box.W - m.Horizontal, 0), Math.Max(box.H - m.Vertical, 0));
        var reserve = Math.Min(content.CaptionReserve, content.CaptionPlacement is "top" or "bottom" ? ih : iw);
        (double X, double Y, double W, double H) caption, value;
        switch (content.CaptionPlacement)
        {
            case "full":
                caption = (ix, iy, iw, ih);
                value = (ix, iy, 0, 0);
                break;
            case "right":
                value = (ix, iy, iw - reserve, ih);
                caption = (ix + iw - reserve, iy, reserve, ih);
                break;
            case "top":
                caption = (ix, iy, iw, reserve);
                value = (ix, iy + reserve, iw, ih - reserve);
                break;
            case "bottom":
                value = (ix, iy, iw, ih - reserve);
                caption = (ix, iy + ih - reserve, iw, reserve);
                break;
            default:
                caption = (ix, iy, reserve, ih);
                value = (ix + reserve, iy, iw - reserve, ih);
                break;
        }

        if (content.Caption is { } captionText)
        {
            var cm = content.CaptionMargin;
            DrawText(canvas, captionText, caption.X + cm.Left, caption.Y + cm.Top, caption.W - cm.Horizontal, caption.H - cm.Vertical, content.CaptionPara.VAlign, toPage, clip: false);
        }

        if (value.W <= 0 && value.H <= 0 && content.Kind != WidgetKind.CheckButton)
        {
            return;
        }

        if (content.UiBorder is { } uiBorder && content.Kind != WidgetKind.CheckButton)
        {
            DrawBorder(canvas, uiBorder, value.X, value.Y, value.W, value.H, toPage);
        }

        var um = content.UiMargin;
        var area = (X: value.X + um.Left, Y: value.Y + um.Top, W: Math.Max(value.W - um.Horizontal, 0), H: Math.Max(value.H - um.Vertical, 0));
        switch (content.Kind)
        {
            case WidgetKind.Text when content.CombText is { } comb && content.CombCells > 0:
                DrawComb(canvas, content, comb, area, toPage);
                break;
            case WidgetKind.Text when content.Text is { } text && (content.TextTop > 0 || content.TextBottom is not null):
                DrawTextWindow(canvas, text, content, area, toPage);
                break;
            case WidgetKind.Text when content.Text is { } text:
                DrawText(canvas, text, area.X, area.Y, area.W, area.H, content.Para.VAlign, toPage, clip: box.Node!.Kind == NodeKind.Field);
                break;
            case WidgetKind.Image when content.Image is { } image:
                DrawImage(canvas, image, content.Aspect, area, toPage);
                break;
            case WidgetKind.Line:
                DrawLine(canvas, content.Shape!, area, toPage);
                break;
            case WidgetKind.Rectangle:
                DrawRectangle(canvas, content.Shape!, area, toPage);
                break;
            case WidgetKind.Arc:
                DrawArc(canvas, content.Shape!, area, toPage);
                break;
            case WidgetKind.CheckButton:
                DrawCheck(canvas, content, value, toPage);
                break;
        }
    }

    /// <summary>Draws a text block in a region (y downwards), aligned vertically, optionally clipped to the region.</summary>
    private static void DrawText(PdfCanvas canvas, TextBlock block, double x, double y, double w, double h, string vAlign, Affine toPage, bool clip)
    {
        if (block.Paragraphs.All(p => p.Layout is null || p.Layout.Lines.Count == 0))
        {
            return;
        }

        // Text coordinates: y upwards from the region's bottom left.
        var toText = new Affine(1, 0, 0, -1, x, y + h).Then(toPage);
        var offset = vAlign switch
        {
            "middle" => (h - block.Height) / 2,
            "bottom" => h - block.Height,
            _ => 0,
        };
        canvas.SaveState();
        canvas.Transform(toText.A, toText.B, toText.C, toText.D, toText.E, toText.F);
        if (clip)
        {
            canvas.Rectangle(-1, -1, Math.Max(w, 0) + 2, Math.Max(h, 0) + 2).Clip().EndPath();
        }

        foreach (var paragraph in block.Paragraphs)
        {
            var top = h - offset - paragraph.Top;
            if (paragraph.Label is { Lines.Count: > 0 } label && paragraph.Layout is { Lines.Count: > 0 } first)
            {
                var baseline = top - first.Lines[0].Top - first.Lines[0].Baseline;
                label.Draw(canvas, paragraph.LabelX, baseline + label.Lines[0].Baseline);
            }

            paragraph.Layout?.Draw(canvas, paragraph.X, top);
        }

        canvas.RestoreState();
    }

    /// <summary>Draws the lines of a part of a split text: the block moved up to its window's top, clipped to the window.</summary>
    private static void DrawTextWindow(PdfCanvas canvas, TextBlock block, WidgetContent content, (double X, double Y, double W, double H) area, Affine toPage)
    {
        var height = (content.TextBottom ?? block.Height) - content.TextTop;
        var toText = new Affine(1, 0, 0, -1, area.X, area.Y + height).Then(toPage);
        canvas.SaveState();
        canvas.Transform(toText.A, toText.B, toText.C, toText.D, toText.E, toText.F);
        canvas.Rectangle(-1, 0, Math.Max(area.W, 0) + 2, Math.Max(height, 0)).Clip().EndPath();
        foreach (var paragraph in block.Paragraphs)
        {
            var top = height + content.TextTop - paragraph.Top;
            if (paragraph.Label is { Lines.Count: > 0 } label && paragraph.Layout is { Lines.Count: > 0 } first)
            {
                var baseline = top - first.Lines[0].Top - first.Lines[0].Baseline;
                label.Draw(canvas, paragraph.LabelX, baseline + label.Lines[0].Baseline);
            }

            paragraph.Layout?.Draw(canvas, paragraph.X, top);
        }

        canvas.RestoreState();
    }

    /// <summary>Draws a comb field's characters, one centered in each cell.</summary>
    private static void DrawComb(PdfCanvas canvas, WidgetContent content, string text, (double X, double Y, double W, double H) area, Affine toPage)
    {
        var cell = area.W / content.CombCells;
        if (content.CombStyle is not { } style || cell <= 0)
        {
            return;
        }

        var toText = new Affine(1, 0, 0, -1, area.X, area.Y + area.H).Then(toPage);
        canvas.SaveState();
        canvas.Transform(toText.A, toText.B, toText.C, toText.D, toText.E, toText.F);
        for (var i = 0; i < Math.Min(text.Length, content.CombCells); i++)
        {
            var glyph = new RaiCore.Pdf.Layout.Paragraph(style).Add(text[i].ToString()).Layout(cell + 1000);
            var top = content.Para.VAlign switch
            {
                "middle" => (area.H - glyph.Height) / 2,
                "bottom" => area.H - glyph.Height,
                _ => 0,
            };
            glyph.Draw(canvas, (i * cell) + ((cell - glyph.ContentWidth) / 2), area.H - top);
        }

        canvas.RestoreState();
    }

    /// <summary>Draws an image in a region by its aspect rule.</summary>
    private static void DrawImage(PdfCanvas canvas, PdfImage image, string aspect, (double X, double Y, double W, double H) area, Affine toPage)
    {
        var (iw, ih) = (image.PixelWidth * 0.75, image.PixelHeight * 0.75);
        if (iw <= 0 || ih <= 0 || area.W <= 0 || area.H <= 0)
        {
            return;
        }

        double w, h;
        switch (aspect)
        {
            case "none":
                (w, h) = (area.W, area.H);
                break;
            case "actual":
                (w, h) = (iw, ih);
                break;
            case "width":
                (w, h) = (area.W, ih * area.W / iw);
                break;
            case "height":
                (w, h) = (iw * area.H / ih, area.H);
                break;
            default:
                var scale = Math.Min(area.W / iw, area.H / ih);
                (w, h) = (iw * scale, ih * scale);
                break;
        }

        // Images sit at the top left of their region (XFA's default), in the region's coordinates with y upwards.
        var toImage = new Affine(1, 0, 0, -1, area.X, area.Y + h).Then(toPage);
        canvas.SaveState();
        canvas.Transform(toImage.A, toImage.B, toImage.C, toImage.D, toImage.E, toImage.F);
        canvas.Rectangle(0, h - area.H, area.W, area.H).Clip().EndPath();
        canvas.DrawImage(image, 0, 0, w, h);
        canvas.RestoreState();
    }
}

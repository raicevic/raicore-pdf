using RaiCore.Pdf.Slides.Model;

namespace RaiCore.Pdf.Slides.Rendering;

/// <summary>
/// Draws a <see cref="Presentation"/>: each slide on a page of the slide's size, its background, then its shapes in
/// z-order through their transformations (groups nesting theirs), with the canvas's paths, fills, strokes, images and
/// the layout engine's text.
/// </summary>
/// <remarks>Partial: tables in <c>SlideRenderer.Tables.cs</c>.</remarks>
internal sealed partial class SlideRenderer
{
    /// <summary>The bands a gradient is drawn in.</summary>
    private const int GradientSteps = 64;

    private readonly PdfDocument _document;
    private readonly TextRenderer _text;
    private readonly SlideFonts _fonts;
    private readonly Action<int, string, string>? _report;
    private readonly Action? _checkpoint;
    private readonly Dictionary<byte[], (PdfImage? Image, string? Problem)> _images = new(ReferenceEqualityComparer.Instance);
    private int _slide;

    private SlideRenderer(PdfDocument document, SlideFonts fonts, Action<int, string, string>? report, Action? checkpoint)
    {
        _document = document;
        _text = new TextRenderer(fonts);
        _fonts = fonts;
        _report = report;
        _checkpoint = checkpoint;
    }

    /// <summary>Renders the slides of a presentation to a new document.</summary>
    public static PdfDocument Render(Presentation presentation, PptxConverterOptions options, Action<int, string, string>? report, Action? checkpoint)
    {
        var document = new PdfDocument { Tagged = options.Tagged };
        document.Info.Title = options.Title ?? presentation.Title;
        document.Info.Author = presentation.Author;
        document.Info.Creator = Pptx.PptxConverter.Creator;
        var renderer = new SlideRenderer(document, new SlideFonts(options), report, checkpoint);
        foreach (var slide in presentation.Slides)
        {
            renderer.Render(slide, presentation.Width, presentation.Height);
        }

        // A PDF has at least one page: a presentation without slides to convert is one blank page of its slide size.
        if (presentation.Slides.Count == 0)
        {
            document.AddPage(new PdfPageSize(presentation.Width, presentation.Height));
        }

        return document;
    }

    /// <summary>Renders one slide on a new page.</summary>
    private void Render(Slide slide, double width, double height)
    {
        _slide = slide.Number;
        _checkpoint?.Invoke();
        var page = _document.AddPage(new PdfPageSize(width, height));
        var canvas = page.Canvas;
        var section = _document.Tagged ? _document.StructureRoot.AddChild("Sect") : null;
        if (section is not null)
        {
            section.Title = $"Slide {slide.Number}";
        }

        // Slide coordinates have y downwards from the top; the page's upwards from the bottom.
        var toPage = new Affine(1, 0, 0, -1, 0, height);
        if (slide.Background is { } background)
        {
            Paint(canvas, background, toPage, width, height, b => b.Polygon(0, 0, width, 0, width, height, 0, height));
        }

        foreach (var shape in slide.Shapes)
        {
            Draw(canvas, shape, toPage, section);
        }
    }

    /// <summary>Reports an issue (the converter keeps one of each per slide).</summary>
    private void Report(string subject, string message) => _report?.Invoke(_slide, subject, message);

    /// <summary>Draws a shape in its parent's coordinates (<paramref name="parent"/> maps them to the page).</summary>
    private void Draw(PdfCanvas canvas, Shape shape, Affine parent, PdfStructureElement? tags)
    {
        _checkpoint?.Invoke();
        if (shape.Background)
        {
            // The master's and layout's graphics are artifacts, children of a group included.
            tags = null;
        }

        var box = shape.Box;
        var toPage = Affine.Of(box).Then(parent);
        if (!toPage.IsFinite)
        {
            return;
        }

        switch (shape)
        {
            case GroupShape group:
            {
                // The children's coordinate space is mapped onto the group's box.
                var sx = group.ChildWidth > 0 ? box.Width / group.ChildWidth : 1;
                var sy = group.ChildHeight > 0 ? box.Height / group.ChildHeight : 1;
                var toGroup = Affine.Translate(-group.ChildX, -group.ChildY).Then(Affine.Scale(sx, sy)).Then(toPage);
                foreach (var child in group.Children)
                {
                    Draw(canvas, child, toGroup, tags);
                }

                break;
            }

            case AutoShape auto:
                DrawShape(canvas, auto, toPage, tags);
                break;
            case PictureShape picture:
                DrawPicture(canvas, picture, toPage, tags);
                break;
            case TableShape table:
                DrawTable(canvas, table, toPage, tags);
                break;
            case ChartFrame chart:
                DrawChart(canvas, chart, toPage, tags);
                break;
        }

        if (shape.Link is { } link)
        {
            var (x, y, w, h) = Bounds(toPage, box.Width, box.Height);
            canvas.AddLink(x, y, w, h, link);
        }
    }

    /// <summary>The bounding box on the page of a shape's box.</summary>
    private static (double X, double Y, double Width, double Height) Bounds(Affine toPage, double width, double height)
    {
        Span<(double X, double Y)> corners = [toPage.Map(0, 0), toPage.Map(width, 0), toPage.Map(width, height), toPage.Map(0, height)];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in corners)
        {
            (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
        }

        return (minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Draws a shape: its geometry filled and stroked, then its text.</summary>
    private void DrawShape(PdfCanvas canvas, AutoShape shape, Affine toPage, PdfStructureElement? tags)
    {
        var (w, h) = (shape.Box.Width, shape.Box.Height);
        if (shape.Fill is not null || shape.Line is not null)
        {
            var paths = shape.Geometry.Build(w, h);
            if (shape.Fill is { } fill)
            {
                foreach (var path in paths.Where(p => p.Filled))
                {
                    Paint(canvas, fill, toPage, w, h, b => b.Segments.AddRange(path.Segments));
                }
            }

            if (shape.Line is { } line)
            {
                foreach (var path in paths.Where(p => p.Stroked))
                {
                    Stroke(canvas, path, line, toPage);
                }
            }
        }

        if (shape.Text is { } text)
        {
            _text.Draw(canvas, text, toPage, shape.Geometry.TextBounds(w, h), tags, shape.Placeholder == PlaceholderKind.Title);
        }
    }

    /// <summary>Adds a path's segments, mapped to the page, to the canvas's current path.</summary>
    private static void Emit(PdfCanvas canvas, IReadOnlyList<Segment> segments, Affine toPage)
    {
        foreach (var segment in segments)
        {
            var (x, y) = toPage.Map(segment.X, segment.Y);
            switch (segment.Kind)
            {
                case SegmentKind.Move:
                    canvas.MoveTo(x, y);
                    break;
                case SegmentKind.Line:
                    canvas.LineTo(x, y);
                    break;
                case SegmentKind.Cubic:
                {
                    var (x1, y1) = toPage.Map(segment.X1, segment.Y1);
                    var (x2, y2) = toPage.Map(segment.X2, segment.Y2);
                    canvas.CurveTo(x1, y1, x2, y2, x, y);
                    break;
                }

                case SegmentKind.Close:
                    canvas.ClosePath();
                    break;
            }
        }
    }

    /// <summary>
    /// Paints an area (a path built in the shape's coordinates) with a fill: one color, a gradient (drawn in bands
    /// clipped to the area) or a picture stretched over the shape's box.
    /// </summary>
    private void Paint(PdfCanvas canvas, Fill fill, Affine toPage, double width, double height, Action<PathBuilder> area)
    {
        var builder = new PathBuilder();
        area(builder);
        if (builder.Segments.Count == 0 || builder.Segments[0].Kind != SegmentKind.Move)
        {
            return;
        }

        switch (fill)
        {
            case SolidFill solid:
                if (solid.Color.Alpha > 0)
                {
                    canvas.SetFillColor(solid.Color);
                    Emit(canvas, builder.Segments, toPage);
                    canvas.Fill();
                }

                break;
            case GradientFill gradient:
            {
                // Translucent bands would show where they overlap (and seams where they do not): a translucent
                // gradient is drawn opaque in a group of its stops' average opacity.
                var alpha = gradient.Stops.Average(s => s.Color.Alpha);
                if (alpha <= 0)
                {
                    break;
                }

                var grouped = alpha < 1;
                if (grouped)
                {
                    canvas.BeginGroup(alpha);
                    gradient = gradient with { Stops = [.. gradient.Stops.Select(s => s with { Color = s.Color.WithAlpha(1) })] };
                }

                canvas.SaveState();
                Emit(canvas, builder.Segments, toPage);
                canvas.Clip();
                canvas.EndPath();
                PaintGradient(canvas, gradient, toPage, width, height);
                canvas.RestoreState();
                if (grouped)
                {
                    canvas.EndGroup();
                }

                break;
            }
            case PictureFill picture:
                if (Image(picture.Data, picture.Source) is { } image)
                {
                    canvas.SaveState();
                    Emit(canvas, builder.Segments, toPage);
                    canvas.Clip();
                    canvas.EndPath();
                    var toImage = new Affine(1, 0, 0, -1, 0, height).Then(toPage);
                    canvas.Transform(toImage.A, toImage.B, toImage.C, toImage.D, toImage.E, toImage.F);
                    canvas.DrawImage(image, 0, 0, width, height);
                    canvas.RestoreState();
                }

                break;
        }
    }

    /// <summary>Paints a gradient over a shape's box (the clip limits it to the area) in bands of its colors.</summary>
    private static void PaintGradient(PdfCanvas canvas, GradientFill gradient, Affine toPage, double width, double height)
    {
        var (cx, cy) = (width / 2, height / 2);
        if (gradient.Radial)
        {
            // From the corners (position 1) inwards to the center (position 0).
            var radius = Math.Sqrt((cx * cx) + (cy * cy));
            for (var i = GradientSteps; i >= 1; i--)
            {
                var f = (double)i / GradientSteps;
                canvas.SetFillColor(gradient.ColorAt(f - (0.5 / GradientSteps)));
                var r = radius * f;
                Emit(canvas, new PathBuilder().Ellipse(cx - r, cy - r, 2 * r, 2 * r).Segments, toPage);
                canvas.Fill();
            }

            return;
        }

        var radians = gradient.Angle * Math.PI / 180;
        var (dx, dy) = (Math.Cos(radians), Math.Sin(radians));
        var (nx, ny) = (-dy, dx);
        var half = (Math.Abs(cx * dx) + Math.Abs(cy * dy)) * 1.0001;
        var reach = width + height + 1;
        var band = 2 * half / GradientSteps;
        for (var i = 0; i < GradientSteps; i++)
        {
            var t0 = -half + (i * band);

            // Each band overlaps the next a little, so that no seam shows between them.
            var t1 = t0 + (band * (i == GradientSteps - 1 ? 1 : 1.5));
            canvas.SetFillColor(gradient.ColorAt((i + 0.5) / GradientSteps));
            var b = new PathBuilder().Polygon(
                cx + (dx * t0) - (nx * reach), cy + (dy * t0) - (ny * reach),
                cx + (dx * t1) - (nx * reach), cy + (dy * t1) - (ny * reach),
                cx + (dx * t1) + (nx * reach), cy + (dy * t1) + (ny * reach),
                cx + (dx * t0) + (nx * reach), cy + (dy * t0) + (ny * reach));
            Emit(canvas, b.Segments, toPage);
            canvas.Fill();
        }
    }

    /// <summary>Strokes a path with a line's width, color, dashes, caps and joins, and draws its line ends.</summary>
    private static void Stroke(PdfCanvas canvas, GeometryPath path, Outline line, Affine toPage)
    {
        if (line.Color.Alpha <= 0 || path.Segments.Count < 2)
        {
            return;
        }

        canvas.SaveState();
        canvas.SetStrokeColor(line.Color);
        canvas.SetLineWidth(line.Width);
        canvas.SetLineCap(line.Cap);
        canvas.SetLineJoin(line.Join);
        if (line.Dash.Count > 0 && line.Width > 0)
        {
            canvas.SetDash([.. line.Dash.Select(d => d * line.Width)]);
        }

        Emit(canvas, path.Segments, toPage);
        canvas.Stroke();
        canvas.RestoreState();
        if (path.IsOpen && (line.Head is not null || line.Tail is not null))
        {
            DrawLineEnds(canvas, path, line, toPage);
        }
    }

    /// <summary>Draws the arrowheads (and other line ends) of an open path, in page coordinates.</summary>
    private static void DrawLineEnds(PdfCanvas canvas, GeometryPath path, Outline line, Affine toPage)
    {
        var segments = path.Segments;
        (double X, double Y) Point(Segment s) => toPage.Map(s.X, s.Y);

        if (line.Head is { } head && segments.Count > 1)
        {
            var start = Point(segments[0]);
            var next = segments[1].Kind == SegmentKind.Cubic ? toPage.Map(segments[1].X1, segments[1].Y1) : Point(segments[1]);
            if (next == start && segments[1].Kind == SegmentKind.Cubic)
            {
                next = toPage.Map(segments[1].X2, segments[1].Y2);
            }

            DrawLineEnd(canvas, head, line, start, next);
        }

        if (line.Tail is { } tail && segments.Count > 1)
        {
            var last = segments[^1];
            var end = Point(last);
            var previous = last.Kind == SegmentKind.Cubic ? toPage.Map(last.X2, last.Y2) : Point(segments[^2]);
            if (previous == end && last.Kind == SegmentKind.Cubic)
            {
                previous = toPage.Map(last.X1, last.Y1);
            }

            DrawLineEnd(canvas, tail, line, end, previous);
        }
    }

    /// <summary>Draws one line end at <paramref name="tip"/>, pointing away from <paramref name="from"/>.</summary>
    private static void DrawLineEnd(PdfCanvas canvas, Model.LineEnd end, Outline line, (double X, double Y) tip, (double X, double Y) from)
    {
        var (dx, dy) = (tip.X - from.X, tip.Y - from.Y);
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance < 1e-9 || !double.IsFinite(distance))
        {
            return;
        }

        (dx, dy) = (dx / distance, dy / distance);
        var (nx, ny) = (-dy, dx);
        var thickness = Math.Max(line.Width, 0.75);
        var length = thickness * end.Length;
        var half = thickness * end.Width / 2;
        var (bx, by) = (tip.X - (dx * length), tip.Y - (dy * length));
        canvas.SaveState();
        canvas.SetFillColor(line.Color);
        canvas.SetStrokeColor(line.Color);
        switch (end.Kind)
        {
            case LineEndKind.Arrow:
                canvas.SetLineWidth(line.Width);
                canvas.SetLineJoin(LineJoin.Miter);
                canvas.MoveTo(bx + (nx * half), by + (ny * half)).LineTo(tip.X, tip.Y).LineTo(bx - (nx * half), by - (ny * half)).Stroke();
                break;
            case LineEndKind.Stealth:
            {
                var (mx, my) = (tip.X - (dx * length * 0.6), tip.Y - (dy * length * 0.6));
                canvas.MoveTo(tip.X, tip.Y).LineTo(bx + (nx * half), by + (ny * half)).LineTo(mx, my).LineTo(bx - (nx * half), by - (ny * half)).ClosePath().Fill();
                break;
            }

            case LineEndKind.Diamond:
            {
                var (cx, cy) = (tip.X - (dx * length / 2), tip.Y - (dy * length / 2));
                canvas.MoveTo(tip.X, tip.Y).LineTo(cx + (nx * half), cy + (ny * half)).LineTo(bx, by).LineTo(cx - (nx * half), cy - (ny * half)).ClosePath().Fill();
                break;
            }

            case LineEndKind.Oval:
            {
                var (cx, cy) = (tip.X - (dx * length / 2), tip.Y - (dy * length / 2));
                for (var i = 0; i <= 24; i++)
                {
                    var a = 2 * Math.PI * i / 24;
                    var (px, py) = (cx + (dx * Math.Cos(a) * length / 2) + (nx * Math.Sin(a) * half), cy + (dy * Math.Cos(a) * length / 2) + (ny * Math.Sin(a) * half));
                    if (i == 0)
                    {
                        canvas.MoveTo(px, py);
                    }
                    else
                    {
                        canvas.LineTo(px, py);
                    }
                }

                canvas.ClosePath().Fill();
                break;
            }

            default:
                canvas.MoveTo(tip.X, tip.Y).LineTo(bx + (nx * half), by + (ny * half)).LineTo(bx - (nx * half), by - (ny * half)).ClosePath().Fill();
                break;
        }

        canvas.RestoreState();
    }

    /// <summary>An image loaded once per conversion; <c>null</c> (reported on each slide it is on) when it cannot be read.</summary>
    private PdfImage? Image(byte[] data, string? source)
    {
        if (!_images.TryGetValue(data, out var loaded))
        {
            try
            {
                loaded = (PdfImage.Load(data), null);
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException or ArgumentException
                or ResourceLimitExceededException { LimitName: nameof(ResourceLimits.MaxImagePixels) })
            {
                var format = data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x01, 0x00, 0x00, 0x00]) ? "EMF"
                    : data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xD7, 0xCD, 0xC6, 0x9A]) ? "WMF" : null;
                loaded = (null, format is not null
                    ? $"The {format} picture '{source}' is not drawn: {format} is not supported."
                    : $"The picture '{source}' is not drawn: {ex.Message}");
            }

            _images[data] = loaded;
        }

        if (loaded.Problem is { } problem)
        {
            Report("picture", problem);
        }

        return loaded.Image;
    }

    /// <summary>Draws a picture: cropped, clipped to its geometry, with its border.</summary>
    private void DrawPicture(PdfCanvas canvas, PictureShape picture, Affine toPage, PdfStructureElement? tags)
    {
        var (w, h) = (picture.Box.Width, picture.Box.Height);
        if (picture.Fill is { } fill)
        {
            foreach (var path in picture.Geometry.Build(w, h).Where(p => p.Filled))
            {
                Paint(canvas, fill, toPage, w, h, b => b.Segments.AddRange(path.Segments));
            }
        }

        if (picture.Data is { } data && w > 0 && h > 0 && Image(data, picture.Source) is { } image)
        {
            var crop = picture.Crop;
            var visibleWidth = 1 - crop.Left - crop.Right;
            var visibleHeight = 1 - crop.Top - crop.Bottom;
            if (visibleWidth > 1e-6 && visibleHeight > 1e-6)
            {
                var figure = tags is not null && picture.AltText is { Length: > 0 } alt ? tags.AddChild("Figure") : null;
                if (figure is not null)
                {
                    figure.AlternateText = picture.AltText;
                    var (bx, by, bw, bh) = Bounds(toPage, w, h);
                    figure.BoundingBox = new PdfRectangle(bx, by, bw, bh);
                    canvas.BeginTag(figure);
                }

                if (picture.Opacity < 1)
                {
                    canvas.BeginGroup(picture.Opacity);
                }

                canvas.SaveState();
                foreach (var path in picture.Geometry.Build(w, h).Where(p => p.Filled))
                {
                    Emit(canvas, path.Segments, toPage);
                }

                canvas.Clip();
                canvas.EndPath();

                // The image's own coordinates: y up from the box's bottom; the cropped parts fall outside the box.
                var toImage = new Affine(1, 0, 0, -1, 0, h).Then(toPage);
                canvas.Transform(toImage.A, toImage.B, toImage.C, toImage.D, toImage.E, toImage.F);
                var (fullWidth, fullHeight) = (w / visibleWidth, h / visibleHeight);
                canvas.DrawImage(image, -crop.Left * fullWidth, -crop.Bottom * fullHeight, fullWidth, fullHeight);
                canvas.RestoreState();
                if (picture.Opacity < 1)
                {
                    canvas.EndGroup();
                }

                if (figure is not null)
                {
                    canvas.EndTag();
                }
            }
        }

        if (picture.Line is { } line)
        {
            foreach (var path in picture.Geometry.Build(w, h).Where(p => p.Stroked))
            {
                Stroke(canvas, path, line, toPage);
            }
        }
    }
}

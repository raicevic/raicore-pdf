namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>
/// Background layers (CSS Backgrounds 3): images and gradients sized and positioned in the origin box, tiled if they
/// repeat, and painted inside the clip box, the last layer first (under the others). A box split across pages is
/// painted as if it were whole, each fragment showing its slice.
/// </summary>
internal sealed partial class Paginator
{
    /// <summary>The most tiles a repeated background layer draws in one fragment; beyond that it is drawn once.</summary>
    private const int MaxTiles = 10000;

    /// <summary>
    /// Paints a box's background layers over its fragment from (<paramref name="x"/>, <paramref name="y"/>) (PDF bottom
    /// left) at <paramref name="width"/> by <paramref name="height"/>, which starts <paramref name="offset"/> below the
    /// top of the whole box, <paramref name="wholeHeight"/> tall.
    /// </summary>
    private static void PaintBackgroundLayers(
        PdfCanvas canvas, BoxStyle style, RoundedCorners corners, double x, double y, double width, double height, bool first, bool last,
        double offset, double wholeHeight)
    {
        var wholeTop = y + height + offset; // PDF y of the whole box's top.
        for (var i = style.BackgroundLayers.Count - 1; i >= 0; i--)
        {
            var layer = style.BackgroundLayers[i];
            canvas.SaveState();
            if (AddBackgroundArea(canvas, style with { BackgroundClip = layer.Clip }, corners, x, y, width, height, first, last))
            {
                canvas.Clip();

                // The origin box, of the whole box.
                var inset = layer.Origin switch
                {
                    BackgroundClip.BorderBox => Thickness.Zero,
                    BackgroundClip.ContentBox => new Thickness(
                        style.Border.Top.Width + style.Padding.Top, style.Border.Right.Width + style.Padding.Right,
                        style.Border.Bottom.Width + style.Padding.Bottom, style.Border.Left.Width + style.Padding.Left),
                    _ => style.Border.Widths,
                };
                var origin = (X: x + inset.Left, Top: wholeTop - inset.Top, Width: Math.Max(0, width - inset.Horizontal), Height: Math.Max(0, wholeHeight - inset.Vertical));
                PaintBackgroundLayer(canvas, layer, origin, (x, y, width, height));
            }

            canvas.RestoreState();
        }
    }

    /// <summary>Paints one layer, tiled over the clip area (here in PDF coordinates, y up) as it repeats.</summary>
    private static void PaintBackgroundLayer(
        PdfCanvas canvas, BackgroundLayer layer, (double X, double Top, double Width, double Height) origin, (double X, double Y, double Width, double Height) clip)
    {
        var (w, h) = TileSize(layer, origin.Width, origin.Height);
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var xs = Tiles(layer.RepeatX, layer.PositionX, origin.X, origin.Width, ref w, clip.X, clip.X + clip.Width);
        var ys = Tiles(layer.RepeatY, layer.PositionY, 0, origin.Height, ref h, origin.Top - clip.Y - clip.Height, origin.Top - clip.Y);
        if (xs.Count * (long)ys.Count > MaxTiles)
        {
            xs = [xs[0]];
            ys = [ys[0]];
        }

        foreach (var top in ys)
        {
            foreach (var left in xs)
            {
                var bottom = origin.Top - top - h;
                switch (layer.Image)
                {
                    case ImageBackground image:
                        canvas.DrawImage(image.Image, left, bottom, w, h);
                        break;
                    case Gradient gradient:
                        canvas.SaveState().Rectangle(left, bottom, w, h).Clip();
                        PaintGradient(canvas, gradient, left, bottom, w, h);
                        canvas.RestoreState();
                        break;
                }
            }
        }
    }

    /// <summary>The size of one tile (CSS <c>background-size</c>), before <c>round</c> adjusts it.</summary>
    private static (double Width, double Height) TileSize(BackgroundLayer layer, double areaWidth, double areaHeight)
    {
        // Images have a natural size and ratio; gradients have neither (auto fills the origin box).
        (double Width, double Height)? natural = layer.Image is ImageBackground image ? (image.Image.NaturalWidth, image.Image.NaturalHeight) : null;
        var size = layer.Size;
        switch (size.Kind)
        {
            case BackgroundSizeKind.Cover or BackgroundSizeKind.Contain when natural is { } n && n.Width > 0 && n.Height > 0:
                var scale = size.Kind == BackgroundSizeKind.Cover
                    ? Math.Max(areaWidth / n.Width, areaHeight / n.Height)
                    : Math.Min(areaWidth / n.Width, areaHeight / n.Height);
                return (n.Width * scale, n.Height * scale);
            case BackgroundSizeKind.Explicit:
                var width = size.Width?.Resolve(areaWidth);
                var height = size.Height?.Resolve(areaHeight);
                return (width, height, natural) switch
                {
                    ({ } fw, { } fh, _) => (fw, fh),
                    ({ } fw, null, { } n) when n.Width > 0 => (fw, fw * n.Height / n.Width),
                    (null, { } fh, { } n) when n.Height > 0 => (fh * n.Width / n.Height, fh),
                    ({ } fw, null, _) => (fw, areaHeight),
                    (null, { } fh, _) => (areaWidth, fh),
                    _ => natural ?? (areaWidth, areaHeight),
                };
            case BackgroundSizeKind.Auto when natural is { } n:
                return n;
            default:
                return (areaWidth, areaHeight);
        }
    }

    /// <summary>
    /// Where the tiles start along one axis (from the origin's start edge for y, absolute for x), covering the clip range
    /// [<paramref name="clipStart"/>, <paramref name="clipEnd"/>) as the layer repeats. <c>round</c> scales the tile.
    /// </summary>
    private static List<double> Tiles(
        BackgroundRepeat repeat, BackgroundPosition position, double originStart, double area, ref double size, double clipStart, double clipEnd)
    {
        if (repeat == BackgroundRepeat.Round && area > 0)
        {
            size = area / Math.Max(1, Math.Round(area / size));
        }

        var tile = size;
        if (repeat == BackgroundRepeat.Space && area > 0)
        {
            var count = Math.Floor(area / tile);
            if (count >= 2)
            {
                var step = tile + ((area - (count * tile)) / (count - 1));
                return Cover(originStart, step);
            }

            if (count < 1)
            {
                return [];
            }

            repeat = BackgroundRepeat.NoRepeat; // One tile: positioned like no-repeat.
        }

        var start = originStart + position.Resolve(area, tile);
        return repeat == BackgroundRepeat.NoRepeat ? [start] : Cover(start, tile);

        List<double> Cover(double at, double step)
        {
            var tiles = new List<double>();
            var first = at - (Math.Ceiling((at - clipStart) / step) * step);
            for (var t = first; t < clipEnd && tiles.Count <= MaxTiles; t += step)
            {
                if (t + tile > clipStart)
                {
                    tiles.Add(t);
                }
            }

            return tiles;
        }
    }

    /// <summary>Paints a gradient over the (already clipped) rectangle with its bottom-left corner at (x, y).</summary>
    private static void PaintGradient(PdfCanvas canvas, Gradient gradient, double x, double y, double width, double height)
    {
        switch (gradient)
        {
            case LinearGradient linear:
            {
                // The direction, with y down (CSS): toward a corner it is perpendicular to the diagonal between the other
                // two corners; an angle points up at 0 and turns clockwise.
                var (dx, dy) = linear.Corner is { } corner
                    ? Normalize(corner.X * height, corner.Y * width)
                    : (Math.Sin(linear.Angle * Math.PI / 180), -Math.Cos(linear.Angle * Math.PI / 180));
                var length = Math.Abs(width * dx) + Math.Abs(height * dy);
                var stops = ResolveStops(gradient, length);
                if (stops is null)
                {
                    return;
                }

                var (cx, cy) = (x + (width / 2), y + (height / 2));
                var (from, to) = (stops.Value.From - (length / 2), stops.Value.To - (length / 2));
                var geometry = new AxialGeometry(cx + (dx * from), cy - (dy * from), cx + (dx * to), cy - (dy * to));
                canvas.PaintShading(geometry, stops.Value.Stops);
                break;
            }

            case RadialGradient radial:
            {
                var cx = radial.CenterX.Offset.IsPercent ? radial.CenterX.Offset.Value / 100 * width : radial.CenterX.Offset.Value;
                var cyDown = radial.CenterY.Offset.IsPercent ? radial.CenterY.Offset.Value / 100 * height : radial.CenterY.Offset.Value;
                cx = radial.CenterX.FromEnd ? width - cx : cx;
                cyDown = radial.CenterY.FromEnd ? height - cyDown : cyDown;
                var (rx, ry) = Radii(radial, cx, cyDown, width, height);
                if (rx <= 0 || ry <= 0)
                {
                    return;
                }

                var stops = ResolveStops(gradient, rx);
                if (stops is null)
                {
                    return;
                }

                // An ellipse is a circle of radius rx with the y axis scaled around the center.
                var (centerX, centerY) = (x + cx, y + height - cyDown);
                var scale = ry / rx;
                canvas.SaveState().Transform(1, 0, 0, scale, 0, centerY * (1 - scale));
                var (r0, r1) = (Math.Max(0, stops.Value.From), Math.Max(0, stops.Value.To));
                canvas.PaintShading(new RadialGeometry(centerX, centerY, r0, centerX, centerY, r1), stops.Value.Stops);
                canvas.RestoreState();
                break;
            }
        }

        static (double, double) Normalize(double a, double b)
        {
            var length = Math.Sqrt((a * a) + (b * b));
            return length > 0 ? (a / length, b / length) : (0, 1);
        }
    }

    /// <summary>A radial gradient's radii (an ellipse's, or a circle's twice) from its size or extent keyword.</summary>
    private static (double X, double Y) Radii(RadialGradient radial, double cx, double cy, double width, double height)
    {
        var circle = radial.Shape == RadialShape.Circle;
        if (radial.RadiusX is { } rx)
        {
            var x = rx.Resolve(width);
            return circle ? (x, x) : (x, radial.RadiusY?.Resolve(height) ?? x);
        }

        var (left, right, top, bottom) = (Math.Abs(cx), Math.Abs(width - cx), Math.Abs(cy), Math.Abs(height - cy));
        double[] corners =
        [
            Math.Sqrt((cx * cx) + (cy * cy)), Math.Sqrt(((width - cx) * (width - cx)) + (cy * cy)),
            Math.Sqrt((cx * cx) + ((height - cy) * (height - cy))), Math.Sqrt(((width - cx) * (width - cx)) + ((height - cy) * (height - cy))),
        ];
        return (radial.Extent, circle) switch
        {
            (RadialExtent.ClosestSide, true) => Both(Math.Min(Math.Min(left, right), Math.Min(top, bottom))),
            (RadialExtent.FarthestSide, true) => Both(Math.Max(Math.Max(left, right), Math.Max(top, bottom))),
            (RadialExtent.ClosestCorner, true) => Both(corners.Min()),
            (_, true) => Both(corners.Max()),
            (RadialExtent.ClosestSide, false) => (Math.Min(left, right), Math.Min(top, bottom)),
            (RadialExtent.FarthestSide, false) => (Math.Max(left, right), Math.Max(top, bottom)),
            // Through the corner, with the aspect ratio the matching -side ellipse has.
            (RadialExtent.ClosestCorner, false) => (Math.Min(left, right) * Math.Sqrt(2), Math.Min(top, bottom) * Math.Sqrt(2)),
            _ => (Math.Max(left, right) * Math.Sqrt(2), Math.Max(top, bottom) * Math.Sqrt(2)),
        };

        static (double, double) Both(double r) => (r, r);
    }

    /// <summary>
    /// The stops along a gradient line of <paramref name="length"/> (CSS Images 3 section 3.5.3): missing positions
    /// spread evenly, positions never decreasing, repeated for repeating gradients. Returns the range the stops span and
    /// their offsets within it (0 to 1), or <c>null</c> when nothing is painted.
    /// </summary>
    private static (double From, double To, List<(double Offset, PdfColor Color)> Stops)? ResolveStops(Gradient gradient, double length)
    {
        var count = gradient.Stops.Count;
        var positions = new double?[count];
        for (var i = 0; i < count; i++)
        {
            positions[i] = gradient.Stops[i].Position?.Resolve(length);
        }

        positions[0] ??= 0;
        positions[^1] ??= Math.Max(length, positions.Where(p => p is not null).Max() ?? length);
        // A position before an earlier one moves up to the largest before it (stops without one are filled in below).
        var largest = positions[0]!.Value;
        for (var i = 1; i < count; i++)
        {
            if (positions[i] is { } p)
            {
                largest = Math.Max(largest, p);
                positions[i] = largest;
            }
        }

        for (var i = 1; i < count; i++)
        {
            if (positions[i] is not null)
            {
                continue;
            }

            var next = i + 1;
            while (positions[next] is null)
            {
                next++;
            }

            var (from, to) = (positions[i - 1]!.Value, positions[next]!.Value);
            for (var j = i; j < next; j++)
            {
                positions[j] = from + ((to - from) * (j - i + 1) / (next - i + 1));
            }
        }

        var stops = positions.Select((p, i) => (Position: p!.Value, gradient.Stops[i].Color)).ToList();
        if (gradient.Repeating)
        {
            var period = stops[^1].Position - stops[0].Position;
            if (period <= 0)
            {
                stops = [(0, stops[^1].Color), (length, stops[^1].Color)];
            }
            else
            {
                // Copies of the stops from before 0 to past the end of the line.
                var first = Math.Floor((0 - stops[0].Position) / period);
                var last = Math.Ceiling((length - stops[0].Position) / period);
                var repeated = new List<(double Position, PdfColor Color)>();
                for (var k = first; k <= last && repeated.Count < 4000; k++)
                {
                    repeated.AddRange(stops.Select(s => (s.Position + (k * period), s.Color)));
                }

                stops = repeated;
            }
        }

        // The range covers the whole line and a little more, so that a hard stop at either end (or past it) keeps its
        // color: the shading's first and last colors continue beyond it.
        var (start, end) = (Math.Min(0, stops[0].Position) - 1, Math.Max(length, stops[^1].Position) + 1);

        return (start, end, [.. stops.Select(s => ((s.Position - start) / (end - start), s.Color))]);
    }
}

using RaiCore.Pdf.Images;

namespace RaiCore.Pdf.Layout;

/// <summary>
/// A shadow cast by a box (CSS <c>box-shadow</c>): its border box (following its rounded corners) moved by an offset,
/// grown by a spread, blurred and filled with a color. An outer shadow is drawn outside the box only, under its
/// background; an <see cref="Inset"/> shadow inside its padding box, over its background and under its border.
/// </summary>
/// <param name="OffsetX">How far the shadow is moved to the right, in points (negative: to the left).</param>
/// <param name="OffsetY">How far the shadow is moved down, in points (negative: up).</param>
/// <param name="Blur">
/// The blur radius in points (CSS: the shadow's edge fades over twice this length, a Gaussian blur with a standard
/// deviation of half of it); 0 for a sharp edge.
/// </param>
/// <param name="Spread">How far the shadow's shape is grown on every side, in points (negative: shrunk).</param>
/// <param name="Color">The shadow's color, usually translucent.</param>
public readonly record struct BoxShadow(double OffsetX, double OffsetY, double Blur, double Spread, PdfColor Color)
{
    /// <summary>Whether the shadow is cast inside the box (CSS <c>inset</c>) instead of outside it.</summary>
    public bool Inset { get; init; }
}

/// <summary>
/// Draws box shadows. A sharp shadow is a filled path. A blurred one is an image of the shadow's color with a soft mask:
/// the shape's coverage blurred with a Gaussian (separable, in both directions), at a resolution that follows the blur
/// (blurred edges are smooth, so a coarse grid that the viewer interpolates is enough).
/// </summary>
internal static class ShadowPainter
{
    /// <summary>The most pixels one blurred shadow image has; the grid gets coarser beyond it.</summary>
    private const int MaxPixels = 1_000_000;

    /// <summary>
    /// Draws a box's outer shadows (the first one on top) around its border box at (<paramref name="x"/>,
    /// <paramref name="y"/>) (bottom-left, PDF coordinates); nothing is drawn inside the border box.
    /// </summary>
    public static void PaintOuter(PdfCanvas canvas, IReadOnlyList<BoxShadow> shadows, double x, double y, double width, double height, RoundedCorners corners)
    {
        for (var i = shadows.Count - 1; i >= 0; i--)
        {
            var shadow = shadows[i];
            if (shadow.Inset || shadow.Color.Alpha <= 0)
            {
                continue;
            }

            // The shape: the border box moved by the offset (y up in PDF coordinates) and grown by the spread.
            var shape = (X: x + shadow.OffsetX - shadow.Spread, Y: y - shadow.OffsetY - shadow.Spread, W: width + (2 * shadow.Spread), H: height + (2 * shadow.Spread));
            if (shape.W <= 0 || shape.H <= 0)
            {
                continue;
            }

            var shapeCorners = corners.Spread(shadow.Spread);
            canvas.SaveState();

            // Outside the border box only: an even-odd clip of a frame around everything the shadow reaches.
            var reach = (shadow.Blur * 1.5) + 1;
            var (left, bottom) = (Math.Min(x, shape.X) - reach, Math.Min(y, shape.Y) - reach);
            var (right, top) = (Math.Max(x + width, shape.X + shape.W) + reach, Math.Max(y + height, shape.Y + shape.H) + reach);
            canvas.Rectangle(left, bottom, right - left, top - bottom);
            corners.AddPath(canvas, x, y, width, height);
            canvas.Clip(evenOdd: true);

            if (shadow.Blur <= 0)
            {
                canvas.SetFillColor(shadow.Color);
                shapeCorners.AddPath(canvas, shape.X, shape.Y, shape.W, shape.H);
                canvas.Fill();
            }
            else
            {
                var margin = shadow.Blur * 1.5; // 3 standard deviations: the rest of the blur is invisible.
                DrawBlurred(canvas, shadow, shape.X - margin, shape.Y - margin, shape.W + (2 * margin), shape.H + (2 * margin),
                    (px, py) => shapeCorners.Contains(px, py, shape.X, shape.Y, shape.W, shape.H), inside: true);
            }

            canvas.RestoreState();
        }
    }

    /// <summary>
    /// Draws a box's inset shadows (the first one on top) inside its padding box at (<paramref name="x"/>,
    /// <paramref name="y"/>) (bottom-left, PDF coordinates) with its rounded <paramref name="corners"/>.
    /// </summary>
    public static void PaintInset(PdfCanvas canvas, IReadOnlyList<BoxShadow> shadows, double x, double y, double width, double height, RoundedCorners corners)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        for (var i = shadows.Count - 1; i >= 0; i--)
        {
            var shadow = shadows[i];
            if (!shadow.Inset || shadow.Color.Alpha <= 0)
            {
                continue;
            }

            // The hole: the padding box moved by the offset and shrunk by the spread; the shadow is the rest of the box.
            var hole = (X: x + shadow.OffsetX + shadow.Spread, Y: y - shadow.OffsetY + shadow.Spread, W: width - (2 * shadow.Spread), H: height - (2 * shadow.Spread));
            var holeCorners = corners.Spread(-shadow.Spread);
            canvas.SaveState();
            corners.Clip(canvas, x, y, width, height);

            if (shadow.Blur <= 0)
            {
                canvas.SetFillColor(shadow.Color);
                canvas.Rectangle(x - 1, y - 1, width + 2, height + 2);
                holeCorners.AddPath(canvas, hole.X, hole.Y, hole.W, hole.H);
                canvas.FillEvenOdd();
            }
            else
            {
                DrawBlurred(canvas, shadow, x, y, width, height,
                    (px, py) => holeCorners.Contains(px, py, hole.X, hole.Y, hole.W, hole.H), inside: false);
            }

            canvas.RestoreState();
        }
    }

    /// <summary>
    /// Draws the area (<paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>, <paramref name="height"/>)
    /// in the shadow's color, masked by the blurred coverage of a shape (<paramref name="inside"/>), or of everything
    /// but the shape (an inset shadow, whose shape is the hole).
    /// </summary>
    private static void DrawBlurred(PdfCanvas canvas, BoxShadow shadow, double x, double y, double width, double height, Func<double, double, bool> inShape, bool inside)
    {
        var sigma = shadow.Blur / 2;
        var step = Math.Clamp(sigma / 5, 0.2, 2.0);
        if (width / step * (height / step) > MaxPixels)
        {
            step = Math.Sqrt(width * height / MaxPixels);
        }

        var columns = Math.Max(1, (int)Math.Ceiling(width / step));
        var rows = Math.Max(1, (int)Math.Ceiling(height / step));
        (step, var stepY) = (width / columns, height / rows);

        // Coverage at the pixel centers (row 0 at the top), the area outside the drawn one counting as what lies
        // beyond: nothing for an outer shadow, all shadow for an inset one.
        var coverage = new float[columns * rows];
        for (var row = 0; row < rows; row++)
        {
            var py = y + height - ((row + 0.5) * stepY);
            for (var column = 0; column < columns; column++)
            {
                var covered = inShape(x + ((column + 0.5) * step), py);
                coverage[(row * columns) + column] = covered == inside ? 1 : 0;
            }
        }

        var outside = inside ? 0f : 1f;
        Blur(coverage, columns, rows, sigma / step, outside, horizontal: true);
        Blur(coverage, columns, rows, sigma / stepY, outside, horizontal: false);

        var alpha = new byte[columns * rows];
        var opacity = shadow.Color.Alpha;
        for (var i = 0; i < alpha.Length; i++)
        {
            alpha[i] = (byte)Math.Clamp(Math.Round(coverage[i] * opacity * 255), 0, 255);
        }

        var (r, g, b) = ((byte)Math.Round(shadow.Color.Red * 255), (byte)Math.Round(shadow.Color.Green * 255), (byte)Math.Round(shadow.Color.Blue * 255));
        var rgb = new byte[columns * rows * 3];
        for (var i = 0; i < rgb.Length; i += 3)
        {
            (rgb[i], rgb[i + 1], rgb[i + 2]) = (r, g, b);
        }

        var image = PdfImage.FromDecoded(new DecodedImage(columns, rows, ImageColorSpace.Rgb, 8, rgb, ImageEncoding.Raw) { Alpha = alpha });
        canvas.DrawImage(image, x, y, width, height);
    }

    /// <summary>A Gaussian blur along rows or columns, with values beyond the edges taken as <paramref name="outside"/>.</summary>
    private static void Blur(float[] values, int columns, int rows, double sigma, float outside, bool horizontal)
    {
        var radius = (int)Math.Ceiling(sigma * 3);
        if (radius < 1)
        {
            return;
        }

        var kernel = new float[(2 * radius) + 1];
        var sum = 0.0;
        for (var k = -radius; k <= radius; k++)
        {
            var weight = Math.Exp(-(k * k) / (2 * sigma * sigma));
            kernel[k + radius] = (float)weight;
            sum += weight;
        }

        for (var k = 0; k < kernel.Length; k++)
        {
            kernel[k] /= (float)sum;
        }

        var (lines, length) = horizontal ? (rows, columns) : (columns, rows);
        var line = new float[length];
        for (var l = 0; l < lines; l++)
        {
            ResourceLimits.Checkpoint();
            for (var i = 0; i < length; i++)
            {
                line[i] = values[horizontal ? (l * columns) + i : (i * columns) + l];
            }

            for (var i = 0; i < length; i++)
            {
                var total = 0f;
                for (var k = -radius; k <= radius; k++)
                {
                    var j = i + k;
                    total += kernel[k + radius] * (j < 0 || j >= length ? outside : line[j]);
                }

                values[horizontal ? (l * columns) + i : (i * columns) + l] = total;
            }
        }
    }
}

namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>
/// The corner radii of one box in points (see <see cref="CornerRadii.Resolve"/>), and the paths of rounded rectangles
/// with them. A corner with either radius 0 is square. Corners are indexed clockwise from the top left (0 top left,
/// 1 top right, 2 bottom right, 3 bottom left), so side <c>i</c> (0 top, 1 right, 2 bottom, 3 left) runs from corner
/// <c>i</c> to corner <c>i + 1</c>.
/// </summary>
internal readonly record struct RoundedCorners(
    double TopLeftX, double TopLeftY, double TopRightX, double TopRightY,
    double BottomRightX, double BottomRightY, double BottomLeftX, double BottomLeftY)
{
    /// <summary>The magic number for approximating a quarter ellipse with a cubic Bezier curve.</summary>
    internal const double Kappa = 0.5522847498307936;

    /// <summary>Whether every corner is square.</summary>
    public bool IsSquare => !Rounded(0) && !Rounded(1) && !Rounded(2) && !Rounded(3);

    /// <summary>The horizontal and vertical radius of a corner (0 top left, clockwise).</summary>
    public (double X, double Y) this[int corner] => (corner & 3) switch
    {
        0 => (TopLeftX, TopLeftY),
        1 => (TopRightX, TopRightY),
        2 => (BottomRightX, BottomRightY),
        _ => (BottomLeftX, BottomLeftY),
    };

    /// <summary>Whether a corner (0 top left, clockwise) is rounded.</summary>
    public bool Rounded(int corner) => this[corner] is { X: > 0, Y: > 0 };

    /// <summary>
    /// Scales all radii down by the same factor when the radii along a side add up to more than its length
    /// (f = min(L / S) over the sides, CSS Backgrounds 3 section 5.5), so that adjacent curves do not overlap.
    /// </summary>
    public RoundedCorners Fit(double width, double height)
    {
        var factor = 1.0;
        void Limit(double length, double sum)
        {
            if (sum > 0)
            {
                factor = Math.Min(factor, Math.Max(0, length) / sum);
            }
        }

        Limit(width, TopLeftX + TopRightX);
        Limit(height, TopRightY + BottomRightY);
        Limit(width, BottomLeftX + BottomRightX);
        Limit(height, TopLeftY + BottomLeftY);
        if (factor >= 1)
        {
            return this;
        }

        return Square(new RoundedCorners(
            TopLeftX * factor, TopLeftY * factor, TopRightX * factor, TopRightY * factor,
            BottomRightX * factor, BottomRightY * factor, BottomLeftX * factor, BottomLeftY * factor));
    }

    /// <summary>
    /// The corners of one fragment of a box split across pages or lines (CSS <c>box-decoration-break: slice</c>): only
    /// those at the edges the fragment has (the top corners on the first page, the bottom ones on the last; the left
    /// ones on an inline box's first line, the right ones on its last), fitted to the fragment's size.
    /// </summary>
    public RoundedCorners Fragment(bool top, bool bottom, double width, double height, bool left = true, bool right = true)
    {
        var corners = this;
        if (!top)
        {
            corners = corners with { TopLeftX = 0, TopLeftY = 0, TopRightX = 0, TopRightY = 0 };
        }

        if (!bottom)
        {
            corners = corners with { BottomLeftX = 0, BottomLeftY = 0, BottomRightX = 0, BottomRightY = 0 };
        }

        if (!left)
        {
            corners = corners with { TopLeftX = 0, TopLeftY = 0, BottomLeftX = 0, BottomLeftY = 0 };
        }

        if (!right)
        {
            corners = corners with { TopRightX = 0, TopRightY = 0, BottomRightX = 0, BottomRightY = 0 };
        }

        return corners.Fit(width, height);
    }

    /// <summary>
    /// The corners of the rectangle inset by the given distances, as the padding edge follows the border edge: each
    /// radius reduced by the inset of its side, at least 0 (CSS Backgrounds 3 section 5.2).
    /// </summary>
    public RoundedCorners Inset(double top, double right, double bottom, double left) => Square(new RoundedCorners(
        Math.Max(0, TopLeftX - left), Math.Max(0, TopLeftY - top), Math.Max(0, TopRightX - right), Math.Max(0, TopRightY - top),
        Math.Max(0, BottomRightX - right), Math.Max(0, BottomRightY - bottom), Math.Max(0, BottomLeftX - left), Math.Max(0, BottomLeftY - bottom)));

    /// <summary>
    /// Adds the rectangle with its bottom-left corner at (<paramref name="x"/>, <paramref name="y"/>) (PDF coordinates)
    /// with these corners as a closed subpath, clockwise, or counterclockwise for a hole in a nonzero fill (where its
    /// edges coincide with the outer ones, they cancel out instead of leaving a hairline). Nothing is added for an
    /// empty rectangle.
    /// </summary>
    public void AddPath(PdfCanvas canvas, double x, double y, double width, double height, bool counterclockwise = false)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Clockwise from the top-left corner's end: each corner's curve (start, two controls, end), joined by the sides.
        var c = Fit(width, height);
        var (x1, y1) = (x + width, y + height);
        var k = 1 - Kappa;
        (double X, double Y)[][] curves =
        [
            [(x1 - c.TopRightX, y1), (x1 - (c.TopRightX * k), y1), (x1, y1 - (c.TopRightY * k)), (x1, y1 - c.TopRightY)],
            [(x1, y + c.BottomRightY), (x1, y + (c.BottomRightY * k)), (x1 - (c.BottomRightX * k), y), (x1 - c.BottomRightX, y)],
            [(x + c.BottomLeftX, y), (x + (c.BottomLeftX * k), y), (x, y + (c.BottomLeftY * k)), (x, y + c.BottomLeftY)],
            [(x, y1 - c.TopLeftY), (x, y1 - (c.TopLeftY * k)), (x + (c.TopLeftX * k), y1), (x + c.TopLeftX, y1)],
        ];
        if (counterclockwise)
        {
            curves = [.. curves.Reverse().Select(curve => curve.Reverse().ToArray())];
        }

        canvas.MoveTo(curves[^1][3].X, curves[^1][3].Y);
        foreach (var curve in curves)
        {
            canvas.LineTo(curve[0].X, curve[0].Y);
            if (curve[0] != curve[3])
            {
                canvas.CurveTo(curve[1].X, curve[1].Y, curve[2].X, curve[2].Y, curve[3].X, curve[3].Y);
            }
        }

        canvas.ClosePath();
    }

    /// <summary>Clips to the rounded rectangle (between <see cref="PdfCanvas.SaveState"/> and <see cref="PdfCanvas.RestoreState"/>).</summary>
    public void Clip(PdfCanvas canvas, double x, double y, double width, double height)
    {
        AddPath(canvas, x, y, width, height);
        canvas.Clip();
    }

    /// <summary>Makes corners with one radius 0 square in both directions.</summary>
    private static RoundedCorners Square(RoundedCorners c)
    {
        static (double X, double Y) Corner(double x, double y) => x > 0 && y > 0 ? (x, y) : (0, 0);
        var tl = Corner(c.TopLeftX, c.TopLeftY);
        var tr = Corner(c.TopRightX, c.TopRightY);
        var br = Corner(c.BottomRightX, c.BottomRightY);
        var bl = Corner(c.BottomLeftX, c.BottomLeftY);
        return new RoundedCorners(tl.X, tl.Y, tr.X, tr.Y, br.X, br.Y, bl.X, bl.Y);
    }
}

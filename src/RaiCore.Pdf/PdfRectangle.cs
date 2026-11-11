using System.Globalization;

namespace RaiCore.Pdf;

/// <summary>A rectangle in PDF points, such as a page's media box: its bottom-left corner, width and height.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The bottom edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct PdfRectangle(double X, double Y, double Width, double Height)
{
    /// <summary>The right edge.</summary>
    public double Right => X + Width;

    /// <summary>The top edge.</summary>
    public double Top => Y + Height;

    /// <summary>The rectangle between two corners, in any order (as PDF rectangle arrays allow).</summary>
    internal static PdfRectangle FromCorners(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>The overlap of two rectangles, or <c>null</c> when they do not overlap.</summary>
    internal PdfRectangle? Intersect(PdfRectangle other)
    {
        var left = Math.Max(X, other.X);
        var bottom = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var top = Math.Min(Top, other.Top);
        return right > left && top > bottom ? new PdfRectangle(left, bottom, right - left, top - bottom) : null;
    }

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"X = {X}, Y = {Y}, Width = {Width}, Height = {Height}");
        return true;
    }
}

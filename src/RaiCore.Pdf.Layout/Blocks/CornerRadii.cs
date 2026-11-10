namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>
/// One radius of a rounded corner: a length in points plus a percentage of the border box's width (for horizontal
/// radii) or height (for vertical radii), as CSS <c>calc(50% + 2px)</c>. Negative results count as 0.
/// </summary>
/// <param name="Points">The fixed part, in points.</param>
/// <param name="Percent">The part that is a percentage (0-100) of the border box's width or height.</param>
public readonly record struct RadiusLength(double Points, double Percent = 0)
{
    /// <summary>No radius.</summary>
    public static RadiusLength Zero { get; }

    /// <summary>A radius in points.</summary>
    public static RadiusLength FromPoints(double points) => new(points);

    /// <summary>A radius as a percentage (0-100) of the border box's width or height.</summary>
    public static RadiusLength FromPercent(double percent) => new(0, percent);

    /// <summary>The radius in points for a box dimension of <paramref name="reference"/> points, at least 0.</summary>
    internal double Resolve(double reference)
    {
        var value = Points + (Percent / 100 * reference);
        return double.IsFinite(value) && value > 0 ? value : 0;
    }
}

/// <summary>
/// The radii of one rounded corner (CSS <c>border-top-left-radius</c> and the like): the ellipse's horizontal and
/// vertical radius. The corner is square when either is 0.
/// </summary>
/// <param name="Horizontal">The horizontal radius; percentages are of the border box's width.</param>
/// <param name="Vertical">The vertical radius; percentages are of the border box's height.</param>
public readonly record struct CornerRadius(RadiusLength Horizontal, RadiusLength Vertical)
{
    /// <summary>A square corner.</summary>
    public static CornerRadius Square { get; }

    /// <summary>A circular corner of a radius in points.</summary>
    public static CornerRadius Circular(double points) => new(RadiusLength.FromPoints(points), RadiusLength.FromPoints(points));

    /// <summary>An elliptical corner of a horizontal and a vertical radius in points.</summary>
    public static CornerRadius Elliptical(double horizontal, double vertical) =>
        new(RadiusLength.FromPoints(horizontal), RadiusLength.FromPoints(vertical));

    /// <summary>A corner whose radii are a percentage (0-100) of the border box's width and height, as CSS <c>50%</c>.</summary>
    public static CornerRadius Percentage(double percent) => new(RadiusLength.FromPercent(percent), RadiusLength.FromPercent(percent));
}

/// <summary>
/// The rounded corners of a box (CSS <c>border-radius</c>): the outer edge of the border is rounded, and the padding
/// edge follows with radii reduced by the border widths. Where adjacent radii add up to more than the box's side,
/// all are scaled down by the same factor, as CSS specifies (CSS Backgrounds 3, section 5.5).
/// </summary>
/// <param name="TopLeft">The top-left corner.</param>
/// <param name="TopRight">The top-right corner.</param>
/// <param name="BottomRight">The bottom-right corner.</param>
/// <param name="BottomLeft">The bottom-left corner.</param>
public readonly record struct CornerRadii(CornerRadius TopLeft, CornerRadius TopRight, CornerRadius BottomRight, CornerRadius BottomLeft)
{
    /// <summary>Square corners.</summary>
    public static CornerRadii None { get; }

    /// <summary>The same circular radius, in points, on all four corners.</summary>
    public static CornerRadii All(double points) => All(CornerRadius.Circular(points));

    /// <summary>The same radii on all four corners.</summary>
    public static CornerRadii All(CornerRadius radius) => new(radius, radius, radius, radius);

    /// <summary>Whether all corners are square whatever the box's size.</summary>
    public bool IsSquare => !Rounded(TopLeft) && !Rounded(TopRight) && !Rounded(BottomRight) && !Rounded(BottomLeft);

    /// <summary>Whether a corner is rounded for some box size: both radii can be positive.</summary>
    private static bool Rounded(CornerRadius corner) =>
        corner.Horizontal is { Points: > 0 } or { Percent: > 0 } && corner.Vertical is { Points: > 0 } or { Percent: > 0 };

    /// <summary>
    /// The radii in points for a border box of the given size, with percentages resolved, square corners where either
    /// radius is 0, and overlapping radii scaled down.
    /// </summary>
    internal RoundedCorners Resolve(double width, double height)
    {
        static (double X, double Y) Corner(CornerRadius corner, double width, double height)
        {
            var (x, y) = (corner.Horizontal.Resolve(width), corner.Vertical.Resolve(height));
            return x > 0 && y > 0 ? (x, y) : (0, 0);
        }

        var tl = Corner(TopLeft, width, height);
        var tr = Corner(TopRight, width, height);
        var br = Corner(BottomRight, width, height);
        var bl = Corner(BottomLeft, width, height);
        return new RoundedCorners(tl.X, tl.Y, tr.X, tr.Y, br.X, br.Y, bl.X, bl.Y).Fit(width, height);
    }
}

using System.Globalization;

namespace RaiCore.Pdf.Layout;

/// <summary>
/// A length: points plus a percentage of a reference length, as CSS <c>12pt</c>, <c>40%</c> or
/// <c>calc(50% - 10pt)</c>. What the percentage refers to depends on the property: a box's widths, margins and padding
/// refer to the containing block's width, a positioned block's insets to the containing block's width (left, right) or
/// height (top, bottom), a corner radius to the border box's width or height, a table column to the table's width.
/// A number converts to a length in points, so <c>Width = 120</c> means 120 points.
/// </summary>
/// <param name="Points">The fixed part, in points.</param>
/// <param name="Percent">The part that is a percentage (0-100) of the reference length.</param>
public readonly record struct Length(double Points, double Percent = 0)
{
    /// <summary>No length.</summary>
    public static Length Zero { get; }

    /// <summary>Whether the length has a percentage part, so that it depends on a reference length.</summary>
    public bool HasPercent => Percent != 0;

    /// <summary>
    /// The length in points, for a length known to have no percentage (a style already resolved by the layout). Debug
    /// builds throw when it has one, which finds code that reads a length before resolving it.
    /// </summary>
    internal double Fixed
    {
        get
        {
#if DEBUG
            if (HasPercent)
            {
                throw new InvalidOperationException($"The length {this} has a percentage: resolve it before reading points.");
            }
#endif
            return Points;
        }
    }

    /// <summary>A length in points.</summary>
    public static Length FromPoints(double points) => new(points);

    /// <summary>A percentage (0-100) of the reference length.</summary>
    public static Length FromPercent(double percent) => new(0, percent);

    /// <summary>A length in points.</summary>
    public static implicit operator Length(double points) => new(points);

    /// <summary>The sum of two lengths.</summary>
    public static Length operator +(Length left, Length right) => new(left.Points + right.Points, left.Percent + right.Percent);

    /// <summary>The difference of two lengths.</summary>
    public static Length operator -(Length left, Length right) => new(left.Points - right.Points, left.Percent - right.Percent);

    /// <summary>The length scaled by a factor.</summary>
    public static Length operator *(Length length, double factor) => new(length.Points * factor, length.Percent * factor);

    /// <summary>The sum of two lengths.</summary>
    public static Length Add(Length left, Length right) => left + right;

    /// <summary>The difference of two lengths.</summary>
    public static Length Subtract(Length left, Length right) => left - right;

    /// <summary>The length scaled by a factor.</summary>
    public static Length Multiply(Length length, double factor) => length * factor;

    /// <summary>A length in points (the same as the implicit conversion).</summary>
    public static Length FromDouble(double points) => new(points);

    /// <summary>The length in points for a reference length (what a percentage refers to).</summary>
    public double Resolve(double reference) => Points + (Percent / 100 * reference);

    /// <summary>CSS-like text: <c>12pt</c>, <c>40%</c> or <c>calc(40% + 12pt)</c>.</summary>
    public override string ToString()
    {
        var points = Points.ToString("0.###", CultureInfo.InvariantCulture) + "pt";
        var percent = Percent.ToString("0.###", CultureInfo.InvariantCulture) + "%";
        return (Points, Percent) switch
        {
            (_, 0) => points,
            (0, _) => percent,
            _ => $"calc({percent} {(Points < 0 ? "-" : "+")} {Math.Abs(Points).ToString("0.###", CultureInfo.InvariantCulture)}pt)",
        };
    }
}

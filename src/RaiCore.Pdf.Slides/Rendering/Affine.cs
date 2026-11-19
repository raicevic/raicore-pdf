using RaiCore.Pdf.Slides.Model;

namespace RaiCore.Pdf.Slides.Rendering;

/// <summary>
/// An affine transformation as PDF writes it: a point (x, y) maps to (a x + c y + e, b x + d y + f).
/// </summary>
internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    /// <summary>The identity.</summary>
    public static Affine Identity => new(1, 0, 0, 1, 0, 0);

    /// <summary>The determinant: negative when the transformation mirrors.</summary>
    public double Determinant => (A * D) - (B * C);

    /// <summary>A translation.</summary>
    public static Affine Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    /// <summary>A scaling about the origin.</summary>
    public static Affine Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    /// <summary>A rotation by degrees, clockwise when y points down.</summary>
    public static Affine Rotate(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        return new Affine(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary>This transformation followed by <paramref name="next"/>.</summary>
    public Affine Then(Affine next) => new(
        (A * next.A) + (B * next.C),
        (A * next.B) + (B * next.D),
        (C * next.A) + (D * next.C),
        (C * next.B) + (D * next.D),
        (E * next.A) + (F * next.C) + next.E,
        (E * next.B) + (F * next.D) + next.F);

    /// <summary>Maps a point.</summary>
    public (double X, double Y) Map(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    /// <summary>
    /// The transformation from a shape's own coordinates (its box from (0, 0) to its size) to its parent's: mirrored
    /// and turned about the box's center, then moved to the box's place.
    /// </summary>
    public static Affine Of(ShapeBox box)
    {
        var (cx, cy) = (box.Width / 2, box.Height / 2);
        return Translate(-cx, -cy)
            .Then(Scale(box.FlipH ? -1 : 1, box.FlipV ? -1 : 1))
            .Then(Rotate(box.Rotation))
            .Then(Translate(box.X + cx, box.Y + cy));
    }

    /// <summary>Whether all the coefficients are finite numbers.</summary>
    public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);
}

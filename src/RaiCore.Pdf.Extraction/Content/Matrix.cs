namespace RaiCore.Pdf.Extraction.Content;

/// <summary>
/// A PDF transformation matrix <c>[a b c d e f]</c> (ISO 32000-2, 8.3.4): it maps (x, y) to
/// (a·x + c·y + e, b·x + d·y + f).
/// </summary>
internal readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
{
    public static Matrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public static Matrix Translation(double x, double y) => new(1, 0, 0, 1, x, y);

    /// <summary>Whether all six numbers are finite.</summary>
    public bool IsFinite =>
        double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);

    /// <summary>
    /// This matrix followed by <paramref name="then"/>: the PDF product <c>this × then</c>, as in <c>cm</c>, which
    /// computes the new CTM as <c>M × CTM</c>.
    /// </summary>
    public Matrix Then(Matrix then) => new(
        (A * then.A) + (B * then.C),
        (A * then.B) + (B * then.D),
        (C * then.A) + (D * then.C),
        (C * then.B) + (D * then.D),
        (E * then.A) + (F * then.C) + then.E,
        (E * then.B) + (F * then.D) + then.F);

    /// <summary>The inverse matrix, or <c>null</c> when this one cannot be inverted (it squeezes the plane to a line or point).</summary>
    public Matrix? Invert()
    {
        var determinant = (A * D) - (B * C);
        if (determinant == 0 || !double.IsFinite(1 / determinant))
        {
            return null;
        }

        var inverse = new Matrix(
            D / determinant,
            -B / determinant,
            -C / determinant,
            A / determinant,
            ((C * F) - (D * E)) / determinant,
            ((B * E) - (A * F)) / determinant);
        return inverse.IsFinite ? inverse : null;
    }

    /// <summary>Maps a point.</summary>
    public PdfPoint Transform(double x, double y) => new((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    /// <summary>Maps a vector (no translation).</summary>
    public PdfPoint TransformVector(double x, double y) => new((A * x) + (C * y), (B * x) + (D * y));

    /// <summary>The matrix from six numbers, or <c>null</c> when there are not six finite numbers.</summary>
    public static Matrix? FromNumbers(IReadOnlyList<double> numbers) =>
        numbers.Count == 6 && new Matrix(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]) is { IsFinite: true } m
            ? m
            : null;
}

/// <summary>A point (or vector), in the coordinates the context says.</summary>
internal readonly record struct PdfPoint(double X, double Y);

/// <summary>An axis-aligned rectangle: <see cref="Left"/> ≤ <see cref="Right"/> and <see cref="Top"/> ≤ <see cref="Bottom"/> (y grows downwards).</summary>
internal readonly record struct Box(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    /// <summary>The smallest box around the points.</summary>
    public static Box Around(PdfPoint a, PdfPoint b, PdfPoint c, PdfPoint d) => new(
        Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)),
        Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)),
        Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)),
        Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)));
}

namespace RaiCore.Pdf.Svg;

/// <summary>
/// An affine transformation, mapping (x, y) to (A·x + C·y + E, B·x + D·y + F) like a PDF <c>cm</c> matrix.
/// </summary>
internal readonly record struct SvgMatrix(double A, double B, double C, double D, double E, double F)
{
    public static SvgMatrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public bool IsIdentity => this == Identity;

    public bool IsFinite =>
        double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);

    /// <summary>Whether the transformation collapses the plane to a line or a point (it then draws nothing), at any scale.</summary>
    public bool IsSingular => Math.Abs((A * D) - (B * C)) <= 1e-9 * ((A * A) + (B * B) + (C * C) + (D * D));

    public static SvgMatrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    public static SvgMatrix Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    public static SvgMatrix Rotate(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var (sin, cos) = Math.SinCos(radians);
        return new(cos, sin, -sin, cos, 0, 0);
    }

    public static SvgMatrix SkewX(double degrees) => new(1, 0, Math.Tan(degrees * Math.PI / 180), 1, 0, 0);

    public static SvgMatrix SkewY(double degrees) => new(1, Math.Tan(degrees * Math.PI / 180), 0, 1, 0, 0);

    /// <summary>The transformation that applies this one first and then <paramref name="then"/>.</summary>
    public SvgMatrix Then(SvgMatrix then) => new(
        (A * then.A) + (B * then.C),
        (A * then.B) + (B * then.D),
        (C * then.A) + (D * then.C),
        (C * then.B) + (D * then.D),
        (E * then.A) + (F * then.C) + then.E,
        (E * then.B) + (F * then.D) + then.F);

    public (double X, double Y) Apply(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    /// <summary>
    /// Parses an SVG <c>transform</c> list such as <c>translate(10 20) rotate(45, 5, 5)</c>. The rightmost transform
    /// applies first. Returns <c>null</c> when the list is invalid (the attribute is then ignored).
    /// </summary>
    public static SvgMatrix? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Identity;
        }

        var result = Identity;
        var scanner = new SvgScanner(text);
        Span<double> args = stackalloc double[6];
        while (!scanner.AtEnd)
        {
            var name = scanner.ReadName().ToString();
            if (name.Length == 0 || scanner.Read() != '(')
            {
                return null;
            }

            var count = 0;
            while (count < args.Length && scanner.TryReadNumber(out var value))
            {
                args[count++] = value;
            }

            if (scanner.Read() != ')')
            {
                return null;
            }

            SvgMatrix? item = (name, count) switch
            {
                ("matrix", 6) => new SvgMatrix(args[0], args[1], args[2], args[3], args[4], args[5]),
                ("translate", 1) => Translate(args[0], 0),
                ("translate", 2) => Translate(args[0], args[1]),
                ("scale", 1) => Scale(args[0], args[0]),
                ("scale", 2) => Scale(args[0], args[1]),
                ("rotate", 1) => Rotate(args[0]),
                ("rotate", 3) => Translate(-args[1], -args[2]).Then(Rotate(args[0])).Then(Translate(args[1], args[2])),
                ("skewX", 1) => SkewX(args[0]),
                ("skewY", 1) => SkewY(args[0]),
                _ => null,
            };

            if (item is not { } matrix)
            {
                return null;
            }

            // "a b" means b is applied to the coordinates first, then a.
            result = matrix.Then(result);
            scanner.SkipSeparator();
        }

        return result.IsFinite ? result : null;
    }
}

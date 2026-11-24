using System.Globalization;

namespace RaiCore.Pdf.DrawingML;

/// <summary>The kinds of command of a free-form path.</summary>
internal enum CustomCommandKind
{
    /// <summary>Starts a subpath (x, y).</summary>
    MoveTo,

    /// <summary>A line (x, y).</summary>
    LineTo,

    /// <summary>An arc (width radius, height radius, start angle, sweep angle).</summary>
    ArcTo,

    /// <summary>A quadratic curve (x1, y1, x, y).</summary>
    QuadTo,

    /// <summary>A cubic curve (x1, y1, x2, y2, x, y).</summary>
    CubicTo,

    /// <summary>Closes the subpath.</summary>
    Close,
}

/// <summary>A command of a free-form path, its arguments as written (numbers or guide names).</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Arguments">The arguments.</param>
internal sealed record CustomCommand(CustomCommandKind Kind, IReadOnlyList<string> Arguments);

/// <summary>A path of a free form: its own coordinate size (0 for the shape's), fill and stroke, and commands.</summary>
/// <param name="Width">The width the coordinates are relative to (in EMUs), or 0 for the shape's.</param>
/// <param name="Height">The height the coordinates are relative to (in EMUs), or 0 for the shape's.</param>
/// <param name="Filled">Whether the path is filled.</param>
/// <param name="Stroked">Whether the path is stroked.</param>
/// <param name="Commands">The commands.</param>
internal sealed record CustomPath(double Width, double Height, bool Filled, bool Stroked, IReadOnlyList<CustomCommand> Commands);

/// <summary>
/// A free-form geometry (DrawingML <c>custGeom</c>): guides (formulas over the shape's size, in EMUs) and paths whose
/// coordinates are numbers or guide names.
/// </summary>
/// <param name="guides">The adjust values and guides, in order: each may use the ones before it.</param>
/// <param name="paths">The paths.</param>
internal sealed class CustomGeometry(IReadOnlyList<(string Name, string Formula)> guides, IReadOnlyList<CustomPath> paths) : Geometry
{
    /// <summary>EMUs per point.</summary>
    private const double Emu = 12700;

    /// <summary>The most guides evaluated, so that a hostile file cannot make building a path slow.</summary>
    private const int MaxGuides = 2000;

    /// <summary>The paths.</summary>
    public IReadOnlyList<CustomPath> Paths => paths;

    /// <inheritdoc/>
    public override IReadOnlyList<GeometryPath> Build(double width, double height)
    {
        var (w, h) = (Math.Max(width, 0) * Emu, Math.Max(height, 0) * Emu);
        var values = Builtins(w, h);
        foreach (var (name, formula) in guides.Take(MaxGuides))
        {
            values[name] = Evaluate(formula, values);
        }

        var result = new List<GeometryPath>();
        foreach (var path in paths)
        {
            // Coordinates in the path's own size are scaled to the shape; others are EMUs of the shape.
            var sx = path.Width > 0 ? width / path.Width : 1 / Emu;
            var sy = path.Height > 0 ? height / path.Height : 1 / Emu;
            var b = new PathBuilder();
            var started = false;
            foreach (var command in path.Commands)
            {
                double A(int i) => i < command.Arguments.Count ? Value(command.Arguments[i], values) : 0;
                switch (command.Kind)
                {
                    case CustomCommandKind.MoveTo:
                        b.MoveTo(A(0) * sx, A(1) * sy);
                        started = true;
                        break;
                    case CustomCommandKind.LineTo when started:
                        b.LineTo(A(0) * sx, A(1) * sy);
                        break;
                    case CustomCommandKind.QuadTo when started:
                        b.QuadTo(A(0) * sx, A(1) * sy, A(2) * sx, A(3) * sy);
                        break;
                    case CustomCommandKind.CubicTo when started:
                        b.CubicTo(A(0) * sx, A(1) * sy, A(2) * sx, A(3) * sy, A(4) * sx, A(5) * sy);
                        break;
                    case CustomCommandKind.ArcTo when started:
                        b.ArcTo(A(0) * sx, A(1) * sy, A(2) / 60000, A(3) / 60000);
                        break;
                    case CustomCommandKind.Close when started:
                        b.Close();
                        break;
                }
            }

            if (b.Segments.Count > 0)
            {
                result.Add(b.ToPath(path.Filled, path.Stroked));
            }
        }

        return result;
    }

    /// <summary>The built-in guides of a shape of a size in EMUs.</summary>
    internal static Dictionary<string, double> Builtins(double w, double h)
    {
        var ss = Math.Min(w, h);
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["w"] = w, ["h"] = h, ["l"] = 0, ["t"] = 0, ["r"] = w, ["b"] = h, ["hc"] = w / 2, ["vc"] = h / 2,
            ["wd2"] = w / 2, ["wd3"] = w / 3, ["wd4"] = w / 4, ["wd5"] = w / 5, ["wd6"] = w / 6, ["wd8"] = w / 8,
            ["wd10"] = w / 10, ["wd12"] = w / 12, ["wd32"] = w / 32,
            ["hd2"] = h / 2, ["hd3"] = h / 3, ["hd4"] = h / 4, ["hd5"] = h / 5, ["hd6"] = h / 6, ["hd8"] = h / 8,
            ["hd10"] = h / 10, ["hd12"] = h / 12, ["hd32"] = h / 32,
            ["ss"] = ss, ["ls"] = Math.Max(w, h), ["ssd2"] = ss / 2, ["ssd4"] = ss / 4, ["ssd6"] = ss / 6, ["ssd8"] = ss / 8,
            ["ssd16"] = ss / 16, ["ssd32"] = ss / 32,
            ["cd2"] = 10800000, ["cd4"] = 5400000, ["cd8"] = 2700000, ["3cd4"] = 16200000, ["3cd8"] = 8100000,
            ["5cd8"] = 13500000, ["7cd8"] = 18900000,
        };
    }

    /// <summary>A number or the value of a guide (0 for an unknown one).</summary>
    internal static double Value(string token, Dictionary<string, double> values)
    {
        if (values.TryGetValue(token, out var value))
        {
            return value;
        }

        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;
    }

    /// <summary>Evaluates a guide formula (<c>*/ w 1 2</c>, <c>val 5000</c>, ...); angles are in 60000ths of a degree.</summary>
    internal static double Evaluate(string formula, Dictionary<string, double> values)
    {
        var parts = formula.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return 0;
        }

        double A(int i) => i < parts.Length ? Value(parts[i], values) : 0;
        static double Radians(double angle) => angle / 60000 * Math.PI / 180;
        static double Angle(double radians) => radians * 180 / Math.PI * 60000;
        var result = parts[0] switch
        {
            "*/" => A(3) == 0 ? 0 : A(1) * A(2) / A(3),
            "+-" => A(1) + A(2) - A(3),
            "+/" => A(3) == 0 ? 0 : (A(1) + A(2)) / A(3),
            "?:" => A(1) > 0 ? A(2) : A(3),
            "abs" => Math.Abs(A(1)),
            "at2" => Angle(Math.Atan2(A(2), A(1))),
            "cat2" => A(1) * Math.Cos(Math.Atan2(A(3), A(2))),
            "cos" => A(1) * Math.Cos(Radians(A(2))),
            "max" => Math.Max(A(1), A(2)),
            "min" => Math.Min(A(1), A(2)),
            "mod" => Math.Sqrt((A(1) * A(1)) + (A(2) * A(2)) + (A(3) * A(3))),
            "pin" => A(2) < A(1) ? A(1) : A(2) > A(3) ? A(3) : A(2),
            "sat2" => A(1) * Math.Sin(Math.Atan2(A(3), A(2))),
            "sin" => A(1) * Math.Sin(Radians(A(2))),
            "sqrt" => Math.Sqrt(Math.Max(A(1), 0)),
            "tan" => A(1) * Math.Tan(Radians(A(2))),
            "val" => A(1),
            _ => 0,
        };
        return double.IsFinite(result) ? result : 0;
    }
}

using System.Globalization;

namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// A CSS math function (<c>calc()</c>, <c>min()</c>, <c>max()</c>, <c>clamp()</c>, nested in any combination) of
/// lengths and percentages, kept until the percentage reference (the containing block's width or height) is known.
/// Units are converted to points while parsing and constant parts are folded, so <c>calc(1em + 2px)</c> becomes a plain
/// length and only what depends on a percentage, such as <c>calc(100% - 20px)</c>, stays an expression.
/// </summary>
/// <remarks>
/// Follows CSS Values 4: <c>+</c> and <c>-</c> need whitespace on both sides, <c>*</c> needs a number on one side,
/// <c>/</c> a non-zero number on the right, and lengths cannot be added to numbers. Invalid expressions are rejected.
/// </remarks>
internal abstract partial class CssCalc
{
    private CssCalc(bool isNumber) => IsNumber = isNumber;

    /// <summary>Whether this is a plain number (as in <c>2 * 10px</c>) rather than a length or percentage.</summary>
    private bool IsNumber { get; }

    /// <summary>The value in points (or the number), with percentages taken of <paramref name="reference"/>.</summary>
    public abstract double Evaluate(double reference);

    /// <summary>
    /// Parses a math function of lengths and percentages. Returns <c>null</c> when the value is not one or is invalid
    /// (syntax, mixed types such as <c>calc(10px + 5)</c>, division by zero, unknown units).
    /// </summary>
    /// <param name="value">The value, such as <c>calc(100% - 2em)</c>.</param>
    /// <param name="fontSize">The font size <c>em</c>, <c>ex</c> and <c>ch</c> refer to, in points.</param>
    /// <param name="rootFontSize">The root element's font size, for <c>rem</c>.</param>
    /// <param name="viewport">The viewport (the page content area) for <c>vw</c>, <c>vh</c>, <c>vmin</c> and <c>vmax</c>.</param>
    public static CssCalc? Parse(string value, double fontSize, double rootFontSize, CssViewport viewport)
    {
        var parser = new Parser(value.Trim(), fontSize, rootFontSize, viewport);
        return parser.Function() is { IsNumber: false } calc && parser.AtEnd && double.IsFinite(calc.Evaluate(0)) && double.IsFinite(calc.Evaluate(100))
            ? calc
            : null;
    }

    /// <summary>The same expression, clamped at 0 (for properties that cannot be negative, as CSS clamps calc results).</summary>
    public CssCalc NonNegative() => Extremum(isMax: true, [this, new Term(0, 0, isNumber: false)]);

    /// <summary>
    /// The expression as points plus a percentage of the reference, when it is one (<c>calc()</c> with <c>+</c>,
    /// <c>-</c>, <c>*</c> and <c>/</c>, also clamped at 0 by <see cref="NonNegative"/>, which the caller applies);
    /// <c>false</c> for <c>min()</c>, <c>max()</c> and <c>clamp()</c> of a percentage.
    /// </summary>
    public bool TryLinear(out double points, out double percent)
    {
        switch (this)
        {
            case Term term:
                (points, percent) = (term.Points, term.Percent);
                return true;
            case Sum sum when sum.Left.TryLinear(out var leftPoints, out var leftPercent) && sum.Right.TryLinear(out var rightPoints, out var rightPercent):
                (points, percent) = (leftPoints + rightPoints, leftPercent + rightPercent);
                return true;
            case Scaled scaled when scaled.Inner.TryLinear(out var innerPoints, out var innerPercent):
                (points, percent) = (innerPoints * scaled.Factor, innerPercent * scaled.Factor);
                return true;
            case Extreme { IsMax: true, Arguments: [var inner, Term { Points: 0, Percent: 0 }] }:
                return inner.TryLinear(out points, out percent); // NonNegative().
            default:
                (points, percent) = (0, 0);
                return false;
        }
    }

    /// <summary>A plain length or percentage when the expression folded to one, otherwise a calc length.</summary>
    public CssLength ToLength() => this switch
    {
        Term { Percent: 0 } term => new(CssValues.Bounded(term.Points), CssLengthKind.Points),
        Term { Points: 0 } term => new(CssValues.Bounded(term.Percent), CssLengthKind.Percent),
        _ => CssLength.FromCalc(this),
    };

    private static CssCalc Add(CssCalc left, CssCalc right) =>
        left is Term a && right is Term b ? new Term(a.Points + b.Points, a.Percent + b.Percent, a.IsNumber) : new Sum(left, right);

    private static CssCalc Scale(CssCalc calc, double factor) => calc switch
    {
        Term term => new Term(term.Points * factor, term.Percent * factor, term.IsNumber),
        Scaled scaled => new Scaled(scaled.Inner, scaled.Factor * factor),
        _ => new Scaled(calc, factor),
    };

    /// <summary>min() or max(); folded when no argument depends on the percentage reference.</summary>
    private static CssCalc Extremum(bool isMax, CssCalc[] arguments)
    {
        if (arguments.Length == 1)
        {
            return arguments[0];
        }

        if (arguments.All(a => a is Term { Percent: 0 }))
        {
            var points = arguments.Select(a => ((Term)a).Points);
            return new Term(isMax ? points.Max() : points.Min(), 0, arguments[0].IsNumber);
        }

        return new Extreme(isMax, arguments);
    }

    /// <summary>A linear term: points (or a number) plus a percentage of the reference.</summary>
    private sealed class Term(double points, double percent, bool isNumber) : CssCalc(isNumber)
    {
        public double Points { get; } = points;

        public double Percent { get; } = percent;

        public override double Evaluate(double reference) => Points + (Percent / 100 * reference);
    }

    private sealed class Sum(CssCalc left, CssCalc right) : CssCalc(left.IsNumber)
    {
        public CssCalc Left { get; } = left;

        public CssCalc Right { get; } = right;

        public override double Evaluate(double reference) => Left.Evaluate(reference) + Right.Evaluate(reference);
    }

    private sealed class Scaled(CssCalc inner, double factor) : CssCalc(inner.IsNumber)
    {
        public CssCalc Inner { get; } = inner;

        public double Factor { get; } = factor;

        public override double Evaluate(double reference) => Inner.Evaluate(reference) * Factor;
    }

    private sealed class Extreme(bool isMax, CssCalc[] arguments) : CssCalc(arguments[0].IsNumber)
    {
        public bool IsMax { get; } = isMax;

        public CssCalc[] Arguments { get; } = arguments;

        public override double Evaluate(double reference)
        {
            var result = Arguments[0].Evaluate(reference);
            foreach (var argument in Arguments.Skip(1))
            {
                var value = argument.Evaluate(reference);
                result = IsMax ? Math.Max(result, value) : Math.Min(result, value);
            }

            return result;
        }
    }

    /// <summary>Recursive descent over the CSS Values 4 grammar (calc-sum, calc-product, calc-value).</summary>
    private sealed class Parser(string text, double fontSize, double rootFontSize, CssViewport viewport)
    {
        private int _position;

        public bool AtEnd => _position == text.Length;

        /// <summary>calc(sum), min(sum#), max(sum#) or clamp(sum, sum, sum).</summary>
        public CssCalc? Function()
        {
            var start = _position;
            while (_position < text.Length && char.IsAsciiLetter(text[_position]))
            {
                _position++;
            }

            var name = text[start.._position].ToUpperInvariant();
            if (name is not ("CALC" or "MIN" or "MAX" or "CLAMP") || !Take('('))
            {
                return null;
            }

            var arguments = new List<CssCalc>();
            do
            {
                SkipWhitespace();
                if (Sum() is not { } argument)
                {
                    return null;
                }

                SkipWhitespace();
                arguments.Add(argument);
            }
            while (name != "CALC" && Take(','));

            if (!Take(')') || arguments.Any(a => a.IsNumber != arguments[0].IsNumber))
            {
                return null;
            }

            return name switch
            {
                "CALC" => arguments[0],
                "MIN" => Extremum(isMax: false, [.. arguments]),
                "MAX" => Extremum(isMax: true, [.. arguments]),

                // clamp(MIN, VAL, MAX) is max(MIN, min(VAL, MAX)).
                _ => arguments.Count == 3 ? Extremum(isMax: true, [arguments[0], Extremum(isMax: false, [arguments[1], arguments[2]])]) : null,
            };
        }

        /// <summary>product [ (' + ' | ' - ') product ]*: the operators need whitespace on both sides.</summary>
        private CssCalc? Sum()
        {
            var left = Product();
            while (left is not null)
            {
                var mark = _position;
                if (!SkipWhitespace() || _position + 1 >= text.Length || text[_position] is not ('+' or '-') || !IsWhitespace(text[_position + 1]))
                {
                    _position = mark;
                    break;
                }

                var minus = text[_position] == '-';
                _position++;
                SkipWhitespace();
                if (Product() is not { } right || right.IsNumber != left.IsNumber)
                {
                    return null;
                }

                left = Add(left, minus ? Scale(right, -1) : right);
            }

            return left;
        }

        /// <summary>value [ ('*' | '/') value ]*: one side of '*' and the right side of '/' must be numbers.</summary>
        private CssCalc? Product()
        {
            var left = Value();
            while (left is not null)
            {
                var mark = _position;
                SkipWhitespace();
                if (_position >= text.Length || text[_position] is not ('*' or '/'))
                {
                    _position = mark;
                    break;
                }

                var divide = text[_position] == '/';
                _position++;
                SkipWhitespace();

                // Number-only expressions always fold to a constant term.
                var right = Value();
                left = (divide, left, right) switch
                {
                    (_, _, null) => null,
                    (true, _, Term { IsNumber: true } divisor) when divisor.Points != 0 => Scale(left, 1 / divisor.Points),
                    (true, _, _) => null,
                    (false, _, Term { IsNumber: true } factor) => Scale(left, factor.Points),
                    (false, Term { IsNumber: true } factor, _) => Scale(right, factor.Points),
                    _ => null,
                };
            }

            return left;
        }

        /// <summary>A number, a dimension, a percentage, a parenthesized sum or a nested math function.</summary>
        private CssCalc? Value()
        {
            if (_position >= text.Length)
            {
                return null;
            }

            if (Take('('))
            {
                SkipWhitespace();
                var inner = Sum();
                SkipWhitespace();
                return inner is not null && Take(')') ? inner : null;
            }

            if (char.IsAsciiLetter(text[_position]))
            {
                return Function();
            }

            var match = NumberRegex().Match(text, _position);
            if (!match.Success || !double.TryParse(match.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            {
                return null;
            }

            _position += match.Length;
            var unit = match.Groups[2].Value;
            return unit switch
            {
                "" => new Term(number, 0, isNumber: true),
                "%" => new Term(0, number, isNumber: false),
                _ => CssValues.ToPoints(number, unit, fontSize, rootFontSize, viewport) is { } points ? new Term(points, 0, isNumber: false) : null,
            };
        }

        private bool Take(char c)
        {
            if (_position < text.Length && text[_position] == c)
            {
                _position++;
                return true;
            }

            return false;
        }

        private bool SkipWhitespace()
        {
            var start = _position;
            while (_position < text.Length && IsWhitespace(text[_position]))
            {
                _position++;
            }

            return _position > start;
        }

        private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\G([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)(%|[a-zA-Z]+)?")]
    private static partial System.Text.RegularExpressions.Regex NumberRegex();
}

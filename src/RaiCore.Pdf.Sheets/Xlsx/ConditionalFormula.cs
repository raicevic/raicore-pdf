using System.Globalization;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>What a value of a formula is.</summary>
internal enum FormulaKind
{
    /// <summary>An empty cell.</summary>
    Blank,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A text.</summary>
    Text,

    /// <summary>TRUE or FALSE (<see cref="FormulaValue.Number"/> 1 or 0).</summary>
    Boolean,

    /// <summary>An error (<see cref="FormulaValue.Text"/> is its text, <c>#DIV/0!</c>).</summary>
    Error,
}

/// <summary>A value of a formula: a cell's stored value or what a formula computes.</summary>
internal readonly record struct FormulaValue(FormulaKind Kind, double Number = 0, string Text = "")
{
    public static FormulaValue Blank => default;

    public static FormulaValue Of(double number) => double.IsFinite(number) ? new(FormulaKind.Number, number) : Error("#NUM!");

    public static FormulaValue Of(bool value) => new(FormulaKind.Boolean, value ? 1 : 0);

    public static FormulaValue Of(string text) => new(FormulaKind.Text, 0, text);

    public static FormulaValue Error(string text) => new(FormulaKind.Error, 0, text);

    /// <summary>Whether a rule's formula with this result applies: TRUE, or a number other than 0.</summary>
    public bool IsTrue => Kind is FormulaKind.Boolean or FormulaKind.Number && Number != 0;
}

/// <summary>
/// A formula of a conditional formatting rule, parsed to be computed over the cells' stored values (Excel's A1 syntax):
/// numbers, texts, TRUE and FALSE, error values, references to cells and ranges of the same sheet (relative ones from
/// the rule's first cell, as Excel writes them), the operators <c>+ - * / ^ &amp; % = &lt;&gt; &lt; &gt; &lt;= &gt;=</c>,
/// and the functions in <see cref="Functions"/>. Anything else (other sheets, names, arrays, other functions) is not
/// supported: <see cref="Parse"/> gives <c>null</c>, and the rule is reported. Parsing and computing are bounded: the
/// formula's length, its size and depth, and the work of each computation (<see cref="Evaluate"/>'s budget).
/// </summary>
internal sealed class ConditionalFormula
{
    /// <summary>The longest formula parsed (Excel's limit is 8192 characters).</summary>
    public const int MaxLength = 8192;

    /// <summary>The most nodes (values, operators, calls) a formula has.</summary>
    public const int MaxNodes = 2048;

    /// <summary>The deepest nesting of a formula.</summary>
    public const int MaxDepth = 64;

    /// <summary>The functions supported, as Excel names them.</summary>
    public static readonly IReadOnlySet<string> Functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "NOT", "IF", "TRUE", "FALSE", "MOD", "ROW", "COLUMN", "ISBLANK", "ISNUMBER", "ISTEXT", "ISERROR", "ISNA",
        "ISEVEN", "ISODD", "ABS", "INT", "ROUND", "LEN", "LEFT", "RIGHT", "UPPER", "LOWER", "TRIM", "SEARCH", "FIND", "TODAY",
        "SUM", "AVERAGE", "MIN", "MAX", "COUNT", "COUNTA", "COUNTBLANK", "COUNTIF",
    };

    private const int MaxRow = XlsxWorkbook.MaxRows - 1;
    private const int MaxColumn = XlsxWorkbook.MaxColumns - 1;

    private readonly Node _root;
    private readonly int _baseRow;
    private readonly int _baseColumn;

    private ConditionalFormula(Node root, int baseRow, int baseColumn) => (_root, _baseRow, _baseColumn) = (root, baseRow, baseColumn);

    /// <summary>
    /// Parses a formula whose relative references are written for the cell at <paramref name="baseRow"/> and
    /// <paramref name="baseColumn"/> (from 0); <c>null</c> for a formula that is malformed or not supported.
    /// </summary>
    public static ConditionalFormula? Parse(string? text, int baseRow, int baseColumn)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            return null;
        }

        var parser = new Parser(text);
        try
        {
            var root = parser.Formula();
            return root is null ? null : new ConditionalFormula(root, baseRow, baseColumn);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Computes the formula for the cell at <paramref name="row"/> and <paramref name="column"/> (its relative references
    /// moved from the first cell to it), reading the cells' values from <paramref name="cells"/>. Each node computed and
    /// each cell of a range read takes one from <paramref name="budget"/>; <c>null</c> once it runs out.
    /// </summary>
    public FormulaValue? Evaluate(Func<int, int, FormulaValue> cells, int row, int column, double today, ref long budget)
    {
        var context = new Context(cells, row - _baseRow, column - _baseColumn, row, column, today, budget);
        try
        {
            var value = context.Scalar(_root);
            budget = context.Budget;
            return value;
        }
        catch (OverflowException)
        {
            budget = -1;
            return null;
        }
    }

    /// <summary>
    /// How two values compare as Excel compares them: numbers before texts before booleans, texts without case; an
    /// empty cell is 0, an empty text or FALSE beside a value of that kind.
    /// </summary>
    internal static int Order(FormulaValue left, FormulaValue right)
    {
        static int Rank(FormulaKind kind) => kind switch
        {
            FormulaKind.Number => 0,
            FormulaKind.Text => 1,
            _ => 2,
        };

        if (left.Kind == FormulaKind.Blank)
        {
            left = right.Kind switch
            {
                FormulaKind.Text => FormulaValue.Of(string.Empty),
                FormulaKind.Boolean => FormulaValue.Of(false),
                _ => FormulaValue.Of(0),
            };
        }

        if (right.Kind == FormulaKind.Blank)
        {
            right = left.Kind switch
            {
                FormulaKind.Text => FormulaValue.Of(string.Empty),
                FormulaKind.Boolean => FormulaValue.Of(false),
                _ => FormulaValue.Of(0),
            };
        }

        if (Rank(left.Kind) != Rank(right.Kind))
        {
            return Rank(left.Kind).CompareTo(Rank(right.Kind));
        }

        return left.Kind == FormulaKind.Text
            ? Math.Sign(string.Compare(left.Text, right.Text, StringComparison.OrdinalIgnoreCase))
            : left.Number.CompareTo(right.Number);
    }

    // ---- The formula's nodes ----

    private abstract record Node;

    private sealed record Constant(FormulaValue Value) : Node;

    /// <summary>A cell: its row and column as written, and whether each is absolute (<c>$</c>).</summary>
    private sealed record Reference(int Row, int Column, bool AbsoluteRow, bool AbsoluteColumn) : Node;

    private sealed record Area(Reference First, Reference Last) : Node;

    private sealed record Unary(char Operator, Node Operand) : Node;

    private sealed record Binary(string Operator, Node Left, Node Right) : Node;

    private sealed record Call(string Name, IReadOnlyList<Node> Arguments) : Node;

    // ---- Parsing ----

    private sealed class Parser(string text)
    {
        private int _position;
        private int _nodes;
        private int _depth;

        public Node? Formula()
        {
            Skip();
            if (Peek() == '=')
            {
                _position++;
            }

            var node = Comparison();
            Skip();
            return _position == text.Length ? node : null;
        }

        private T Count<T>(T node)
            where T : Node
        {
            if (++_nodes > MaxNodes)
            {
                throw new FormatException("The formula is too large.");
            }

            return node;
        }

        private char Peek() => _position < text.Length ? text[_position] : '\0';

        private void Skip()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }

        private bool Take(string token)
        {
            Skip();
            if (string.CompareOrdinal(text, _position, token, 0, token.Length) != 0)
            {
                return false;
            }

            _position += token.Length;
            return true;
        }

        private Node Nested(Func<Node> parse)
        {
            if (++_depth > MaxDepth)
            {
                throw new FormatException("The formula is nested too deeply.");
            }

            try
            {
                return parse();
            }
            finally
            {
                _depth--;
            }
        }

        private Node Comparison()
        {
            var left = Concatenation();
            while (true)
            {
                var op = Take("<>") ? "<>" : Take("<=") ? "<=" : Take(">=") ? ">=" : Take("=") ? "=" : Take("<") ? "<" : Take(">") ? ">" : null;
                if (op is null)
                {
                    return left;
                }

                left = Count(new Binary(op, left, Concatenation()));
            }
        }

        private Node Concatenation()
        {
            var left = Additive();
            while (Take("&"))
            {
                left = Count(new Binary("&", left, Additive()));
            }

            return left;
        }

        private Node Additive()
        {
            var left = Multiplicative();
            while (true)
            {
                var op = Take("+") ? "+" : Take("-") ? "-" : null;
                if (op is null)
                {
                    return left;
                }

                left = Count(new Binary(op, left, Multiplicative()));
            }
        }

        private Node Multiplicative()
        {
            var left = Power();
            while (true)
            {
                var op = Take("*") ? "*" : Take("/") ? "/" : null;
                if (op is null)
                {
                    return left;
                }

                left = Count(new Binary(op, left, Power()));
            }
        }

        private Node Power()
        {
            var left = Prefix();
            while (Take("^"))
            {
                left = Count(new Binary("^", left, Prefix()));
            }

            return left;
        }

        /// <summary>A value with its signs before it and percent signs after it (Excel: <c>-2^2</c> is 4).</summary>
        private Node Prefix()
        {
            if (Take("-"))
            {
                return Nested(() => Count(new Unary('-', Prefix())));
            }

            if (Take("+"))
            {
                return Nested(Prefix);
            }

            var node = Primary();
            while (Take("%"))
            {
                node = Count(new Unary('%', node));
            }

            return node;
        }

        private Node Primary()
        {
            Skip();
            var c = Peek();
            if (c == '(')
            {
                _position++;
                var inner = Nested(Comparison);
                return Take(")") ? inner : throw new FormatException("A parenthesis is not closed.");
            }

            if (c == '"')
            {
                return Count(new Constant(FormulaValue.Of(Text())));
            }

            if (char.IsAsciiDigit(c) || c == '.')
            {
                return Count(new Constant(FormulaValue.Of(Number())));
            }

            if (c == '#')
            {
                foreach (var error in (string[])["#NULL!", "#DIV/0!", "#VALUE!", "#REF!", "#NAME?", "#NUM!", "#N/A"])
                {
                    if (Take(error))
                    {
                        return Count(new Constant(FormulaValue.Error(error)));
                    }
                }

                throw new FormatException("Not an error value.");
            }

            if (ReadReference() is { } reference)
            {
                if (!Take(":"))
                {
                    return Count(reference);
                }

                Skip();
                return ReadReference() is { } last ? Count(new Area(reference, last)) : throw new FormatException("Not a range.");
            }

            var start = _position;
            while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] is '_' or '.'))
            {
                _position++;
            }

            var name = text[start.._position];
            if (name.Length == 0)
            {
                throw new FormatException("Not a value.");
            }

            if (Take("("))
            {
                if (!Functions.Contains(name))
                {
                    throw new FormatException("A function that is not supported.");
                }

                var arguments = new List<Node>();
                if (!Take(")"))
                {
                    do
                    {
                        Skip();
                        arguments.Add(Peek() is ',' or ')' ? Count(new Constant(FormulaValue.Blank)) : Nested(Comparison));
                    }
                    while (Take(","));

                    if (!Take(")"))
                    {
                        throw new FormatException("A call is not closed.");
                    }
                }

                return Count(new Call(name.ToUpperInvariant(), arguments));
            }

            return name.ToUpperInvariant() switch
            {
                "TRUE" => Count(new Constant(FormulaValue.Of(true))),
                "FALSE" => Count(new Constant(FormulaValue.Of(false))),
                _ => throw new FormatException("A name, or a reference to another sheet."),
            };
        }

        private string Text()
        {
            var builder = new System.Text.StringBuilder();
            _position++;
            while (true)
            {
                if (_position >= text.Length)
                {
                    throw new FormatException("A text is not closed.");
                }

                if (text[_position] == '"')
                {
                    if (_position + 1 < text.Length && text[_position + 1] == '"')
                    {
                        builder.Append('"');
                        _position += 2;
                        continue;
                    }

                    _position++;
                    return builder.ToString();
                }

                builder.Append(text[_position++]);
            }
        }

        private double Number()
        {
            var start = _position;
            while (_position < text.Length && (char.IsAsciiDigit(text[_position]) || text[_position] == '.'))
            {
                _position++;
            }

            if (_position < text.Length && text[_position] is 'e' or 'E')
            {
                var mark = _position++;
                if (_position < text.Length && text[_position] is '+' or '-')
                {
                    _position++;
                }

                if (_position >= text.Length || !char.IsAsciiDigit(text[_position]))
                {
                    _position = mark;
                }

                while (_position < text.Length && char.IsAsciiDigit(text[_position]))
                {
                    _position++;
                }
            }

            return double.TryParse(text.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                ? number
                : throw new FormatException("Not a number.");
        }

        /// <summary>A reference to a cell (<c>B2</c>, <c>$B$2</c>), or <c>null</c> (and nothing read) if none is here.</summary>
        private Reference? ReadReference()
        {
            var at = _position;
            var absoluteColumn = at < text.Length && text[at] == '$';
            at += absoluteColumn ? 1 : 0;
            var letters = at;
            while (at < text.Length && at - letters < 4 && char.IsAsciiLetter(text[at]))
            {
                at++;
            }

            var letterCount = at - letters;
            var absoluteRow = at < text.Length && text[at] == '$';
            at += absoluteRow ? 1 : 0;
            var digits = at;
            while (at < text.Length && at - digits < 8 && char.IsAsciiDigit(text[at]))
            {
                at++;
            }

            // Not a reference: no letters or digits, or a name or function that goes on (A1B, LOG10(), Sheet1!A1).
            if (letterCount is < 1 or > 3 || at == digits || (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] is '_' or '(' or '!' or '.')))
            {
                return null;
            }

            var column = 0;
            for (var i = letters; i < letters + letterCount; i++)
            {
                column = (column * 26) + (char.ToUpperInvariant(text[i]) - 'A' + 1);
            }

            var row = int.Parse(text.AsSpan(digits, at - digits), NumberStyles.None, CultureInfo.InvariantCulture);
            if (column - 1 > MaxColumn || row < 1 || row - 1 > MaxRow)
            {
                return null;
            }

            _position = at;
            return new Reference(row - 1, column - 1, absoluteRow, absoluteColumn);
        }
    }

    // ---- Computing ----

    private sealed class Context(Func<int, int, FormulaValue> cells, int rowShift, int columnShift, int row, int column, double today, long budget)
    {
        private static readonly FormulaValue ValueError = FormulaValue.Error("#VALUE!");
        private static readonly FormulaValue DivisionError = FormulaValue.Error("#DIV/0!");
        private static readonly FormulaValue ReferenceError = FormulaValue.Error("#REF!");

        public long Budget { get; private set; } = budget;

        private void Spend(long amount = 1)
        {
            Budget -= amount;
            if (Budget < 0)
            {
                throw new OverflowException("The formula's work is over its budget.");
            }
        }

        /// <summary>The cell a reference is to from the cell computed; <c>null</c> off the sheet.</summary>
        private (int Row, int Column)? Resolve(Reference reference)
        {
            var r = reference.AbsoluteRow ? reference.Row : reference.Row + rowShift;
            var c = reference.AbsoluteColumn ? reference.Column : reference.Column + columnShift;
            return r is < 0 or > MaxRow || c is < 0 or > MaxColumn ? null : (r, c);
        }

        private CellRange? Resolve(Area area) =>
            Resolve(area.First) is { } a && Resolve(area.Last) is { } b
                ? new CellRange(Math.Min(a.Row, b.Row), Math.Min(a.Column, b.Column), Math.Max(a.Row, b.Row), Math.Max(a.Column, b.Column))
                : null;

        /// <summary>The values of a node: a range's cells (row by row), or a single value.</summary>
        private IEnumerable<FormulaValue> Values(Node node)
        {
            if (node is Area area)
            {
                if (Resolve(area) is not { } range)
                {
                    yield return ReferenceError;
                    yield break;
                }

                Spend(((long)(range.Row2 - range.Row1 + 1)) * (range.Column2 - range.Column1 + 1));
                for (var r = range.Row1; r <= range.Row2; r++)
                {
                    for (var c = range.Column1; c <= range.Column2; c++)
                    {
                        yield return cells(r, c);
                    }
                }
            }
            else
            {
                yield return Scalar(node);
            }
        }

        public FormulaValue Scalar(Node node)
        {
            Spend();
            switch (node)
            {
                case Constant constant:
                    return constant.Value;
                case Reference reference:
                    return Resolve(reference) is var (r, c) ? cells(r, c) : ReferenceError;
                case Area area:
                    // A range where one value is wanted: the cell of the computed cell's row or column (implicit intersection).
                    if (Resolve(area) is not { } range)
                    {
                        return ReferenceError;
                    }

                    if (range.Column1 == range.Column2 && row >= range.Row1 && row <= range.Row2)
                    {
                        return cells(row, range.Column1);
                    }

                    return range.Row1 == range.Row2 && column >= range.Column1 && column <= range.Column2 ? cells(range.Row1, column) : ValueError;
                case Unary unary:
                {
                    var operand = Scalar(unary.Operand);
                    if (ToNumber(operand) is not { } n)
                    {
                        return operand.Kind == FormulaKind.Error ? operand : ValueError;
                    }

                    return FormulaValue.Of(unary.Operator == '-' ? -n : n / 100);
                }

                case Binary binary:
                    return Binary(binary.Operator, Scalar(binary.Left), Scalar(binary.Right));
                case Call call:
                    return Function(call);
                default:
                    return ValueError;
            }
        }

        private static double? ToNumber(FormulaValue value) => value.Kind switch
        {
            FormulaKind.Blank => 0,
            FormulaKind.Number or FormulaKind.Boolean => value.Number,
            FormulaKind.Text => double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null,
            _ => null,
        };

        private static string ToText(FormulaValue value) => value.Kind switch
        {
            FormulaKind.Number => value.Number.ToString("R", CultureInfo.InvariantCulture),
            FormulaKind.Boolean => value.Number != 0 ? "TRUE" : "FALSE",
            _ => value.Text ?? string.Empty, // A blank (a missing argument too) is an empty text.
        };

        private static FormulaValue Binary(string op, FormulaValue left, FormulaValue right)
        {
            if (left.Kind == FormulaKind.Error)
            {
                return left;
            }

            if (right.Kind == FormulaKind.Error)
            {
                return right;
            }

            if (op == "&")
            {
                return FormulaValue.Of(ToText(left) + ToText(right));
            }

            if (op is "=" or "<>" or "<" or ">" or "<=" or ">=")
            {
                var order = Order(left, right);
                return FormulaValue.Of(op switch
                {
                    "=" => order == 0,
                    "<>" => order != 0,
                    "<" => order < 0,
                    ">" => order > 0,
                    "<=" => order <= 0,
                    _ => order >= 0,
                });
            }

            if (ToNumber(left) is not { } a || ToNumber(right) is not { } b)
            {
                return ValueError;
            }

            return op switch
            {
                "+" => FormulaValue.Of(a + b),
                "-" => FormulaValue.Of(a - b),
                "*" => FormulaValue.Of(a * b),
                "/" => b == 0 ? DivisionError : FormulaValue.Of(a / b),
                _ => a == 0 && b < 0 ? DivisionError : FormulaValue.Of(Math.Pow(a, b)),
            };
        }

        private FormulaValue Function(Call call)
        {
            var args = call.Arguments;
            FormulaValue Arg(int i) => i < args.Count ? Scalar(args[i]) : FormulaValue.Blank;
            double? Number(int i) => ToNumber(Arg(i));
            FormulaValue Numeric(Func<double, FormulaValue> f, int i = 0) => Arg(i) is var a && a.Kind == FormulaKind.Error ? a : ToNumber(a) is { } n ? f(n) : ValueError;
            FormulaValue Textual(Func<string, FormulaValue> f) => Arg(0) is var a && a.Kind == FormulaKind.Error ? a : f(ToText(a));
            switch (call.Name)
            {
                case "TRUE" or "FALSE":
                    return FormulaValue.Of(call.Name == "TRUE");
                case "AND" or "OR":
                {
                    bool? result = null;
                    foreach (var arg in args)
                    {
                        foreach (var value in Values(arg))
                        {
                            if (value.Kind == FormulaKind.Error)
                            {
                                return value;
                            }

                            if (value.Kind is FormulaKind.Number or FormulaKind.Boolean)
                            {
                                result = call.Name == "AND" ? (result ?? true) && value.Number != 0 : (result ?? false) || value.Number != 0;
                            }
                            else if (value.Kind == FormulaKind.Text && arg is not Area)
                            {
                                return ValueError;
                            }
                        }
                    }

                    return result is { } r ? FormulaValue.Of(r) : ValueError;
                }

                case "NOT":
                    return Numeric(n => FormulaValue.Of(n == 0));
                case "IF":
                {
                    var condition = Arg(0);
                    if (condition.Kind == FormulaKind.Error)
                    {
                        return condition;
                    }

                    if (ToNumber(condition) is not { } c)
                    {
                        return ValueError;
                    }

                    return c != 0 ? Arg(1) : args.Count > 2 ? Arg(2) : FormulaValue.Of(false);
                }

                case "MOD":
                {
                    if (Number(0) is not { } a || Number(1) is not { } b)
                    {
                        return ValueError;
                    }

                    return b == 0 ? DivisionError : FormulaValue.Of(a - (b * Math.Floor(a / b)));
                }

                case "ROW" or "COLUMN":
                {
                    var target = args.Count == 0 ? (row, column)
                        : args[0] switch
                        {
                            Reference reference => Resolve(reference),
                            Area area => Resolve(area) is { } range ? (range.Row1, range.Column1) : null,
                            _ => ((int, int)?)null,
                        };
                    return target is var (r, c) ? FormulaValue.Of((call.Name == "ROW" ? r : c) + 1) : args.Count > 0 && args[0] is Reference or Area ? ReferenceError : ValueError;
                }

                case "ISBLANK":
                    return FormulaValue.Of(Arg(0).Kind == FormulaKind.Blank);
                case "ISNUMBER":
                    return FormulaValue.Of(Arg(0).Kind == FormulaKind.Number);
                case "ISTEXT":
                    return FormulaValue.Of(Arg(0).Kind == FormulaKind.Text);
                case "ISERROR":
                    return FormulaValue.Of(Arg(0).Kind == FormulaKind.Error);
                case "ISNA":
                    return FormulaValue.Of(Arg(0) is { Kind: FormulaKind.Error, Text: "#N/A" });
                case "ISEVEN" or "ISODD":
                    return Numeric(n => FormulaValue.Of((Math.Abs(Math.Truncate(n)) % 2 == 0) == (call.Name == "ISEVEN")));
                case "ABS":
                    return Numeric(n => FormulaValue.Of(Math.Abs(n)));
                case "INT":
                    return Numeric(n => FormulaValue.Of(Math.Floor(n)));
                case "ROUND":
                {
                    if (Number(0) is not { } n || Number(1) is not { } digits)
                    {
                        return ValueError;
                    }

                    var places = (int)Math.Clamp(Math.Truncate(digits), -15, 15);
                    if (places >= 0 && Math.Abs(n) < 1e15)
                    {
                        // In decimal, as Excel rounds the number it shows (2.345 to 2.35, not the double's 2.34).
                        return FormulaValue.Of((double)Math.Round((decimal)n, places, MidpointRounding.AwayFromZero));
                    }

                    var factor = Math.Pow(10, places);
                    return FormulaValue.Of(Math.Round(n * factor, MidpointRounding.AwayFromZero) / factor);
                }

                case "LEN":
                    return Textual(t => FormulaValue.Of(t.Length));
                case "UPPER":
                    return Textual(t => FormulaValue.Of(t.ToUpperInvariant()));
                case "LOWER":
                    return Textual(t => FormulaValue.Of(t.ToLowerInvariant()));
                case "TRIM":
                    return Textual(t => FormulaValue.Of(string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries))));
                case "LEFT" or "RIGHT":
                {
                    var count = args.Count > 1 ? Number(1) : 1;
                    if (count is not { } n || n < 0)
                    {
                        return ValueError;
                    }

                    return Textual(t =>
                    {
                        var length = (int)Math.Min(t.Length, Math.Truncate(n));
                        return FormulaValue.Of(call.Name == "LEFT" ? t[..length] : t[^length..]);
                    });
                }

                case "SEARCH" or "FIND":
                {
                    var find = Arg(0);
                    var within = Arg(1);
                    var start = args.Count > 2 ? Number(2) : 1;
                    if (find.Kind == FormulaKind.Error || within.Kind == FormulaKind.Error)
                    {
                        return find.Kind == FormulaKind.Error ? find : within;
                    }

                    var text = ToText(within);
                    if (start is not { } s || s < 1 || s > text.Length + 1)
                    {
                        return ValueError;
                    }

                    var at = text.IndexOf(ToText(find), (int)s - 1, call.Name == "SEARCH" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    return at < 0 ? ValueError : FormulaValue.Of(at + 1);
                }

                case "TODAY":
                    return FormulaValue.Of(today);
                case "SUM" or "AVERAGE" or "MIN" or "MAX" or "COUNT" or "COUNTA" or "COUNTBLANK":
                    return Aggregate(call.Name, args);
                case "COUNTIF":
                    return args.Count == 2 && args[0] is Area or Reference ? CountIf(args[0], Arg(1)) : ValueError;
                default:
                    return FormulaValue.Error("#NAME?");
            }
        }

        private FormulaValue Aggregate(string name, IReadOnlyList<Node> args)
        {
            double sum = 0, min = double.MaxValue, max = double.MinValue;
            long count = 0, filled = 0, blank = 0;
            foreach (var arg in args)
            {
                foreach (var value in Values(arg))
                {
                    var fromCells = arg is Area or Reference;
                    if (value.Kind == FormulaKind.Blank)
                    {
                        blank++;
                        continue;
                    }

                    filled++;
                    if (value.Kind == FormulaKind.Error)
                    {
                        if (name is "COUNT" or "COUNTA" or "COUNTBLANK")
                        {
                            continue;
                        }

                        return value;
                    }

                    // Cells count their numbers only; values given directly count their numbers, booleans and number texts.
                    var number = value.Kind == FormulaKind.Number || (!fromCells && value.Kind != FormulaKind.Text) ? value.Number
                        : !fromCells && ToNumber(value) is { } n ? n
                        : (double?)null;
                    if (number is not { } x)
                    {
                        if (!fromCells && name is not ("COUNT" or "COUNTA"))
                        {
                            return ValueError;
                        }

                        continue;
                    }

                    sum += x;
                    count++;
                    min = Math.Min(min, x);
                    max = Math.Max(max, x);
                }
            }

            return name switch
            {
                "SUM" => FormulaValue.Of(sum),
                "AVERAGE" => count == 0 ? DivisionError : FormulaValue.Of(sum / count),
                "MIN" => FormulaValue.Of(count == 0 ? 0 : min),
                "MAX" => FormulaValue.Of(count == 0 ? 0 : max),
                "COUNT" => FormulaValue.Of(count),
                "COUNTA" => FormulaValue.Of(filled),
                _ => FormulaValue.Of(blank),
            };
        }

        /// <summary>COUNTIF with a criterion that is a value, or a text of a comparison and a value (<c>"&gt;5"</c>); no wildcards.</summary>
        private FormulaValue CountIf(Node range, FormulaValue criterion)
        {
            if (criterion.Kind == FormulaKind.Error)
            {
                return criterion;
            }

            var op = "=";
            var target = criterion;
            if (criterion.Kind == FormulaKind.Text)
            {
                var text = criterion.Text;
                foreach (var prefix in (string[])["<>", "<=", ">=", "=", "<", ">"])
                {
                    if (text.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        op = prefix;
                        text = text[prefix.Length..];
                        break;
                    }
                }

                target = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? FormulaValue.Of(n) : FormulaValue.Of(text);
            }

            var count = 0;
            foreach (var value in Values(range))
            {
                if (value.Kind == FormulaKind.Blank && !(op == "<>" || (target.Kind == FormulaKind.Text && target.Text.Length == 0)))
                {
                    continue;
                }

                // Only values of the criterion's kind match (a number does not equal a text of it), except for "<>".
                var sameKind = value.Kind == target.Kind || value.Kind == FormulaKind.Blank;
                if (!sameKind)
                {
                    count += op == "<>" ? 1 : 0;
                    continue;
                }

                var order = Order(value, target);
                count += op switch
                {
                    "=" => order == 0,
                    "<>" => order != 0,
                    "<" => order < 0,
                    ">" => order > 0,
                    "<=" => order <= 0,
                    _ => order >= 0,
                } ? 1 : 0;
            }

            return FormulaValue.Of(count);
        }
    }
}

using System.Globalization;
using System.Xml.Linq;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// A format a conditional formatting rule applies (<c>dxf</c>): what it sets of the font (bold, italic, underline,
/// strikethrough, color), the fill and the borders; <c>null</c> where it sets nothing.
/// </summary>
internal sealed record DifferentialFormat(bool? Bold, bool? Italic, CellUnderline? Underline, bool? Strikethrough, PdfColor? Color, PdfColor? Fill,
    CellBorder? Top, CellBorder? Right, CellBorder? Bottom, CellBorder? Left)
{
    /// <summary>Reads a <c>dxf</c> element.</summary>
    public static DifferentialFormat Read(XElement dxf, XlsxColors colors)
    {
        XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(c => c.Name.LocalName == name);
        PdfColor? Color(XElement? c) => c is null ? null
            : colors.Resolve((string?)c.Attribute("rgb"), (string?)c.Attribute("indexed"), (string?)c.Attribute("theme"), (string?)c.Attribute("tint"), (string?)c.Attribute("auto"));
        bool? On(XElement? font, string name) => Child(font, name) is { } e ? (string?)e.Attribute("val") is not ("0" or "false") : null;
        var font = Child(dxf, "font");
        var pattern = Child(Child(dxf, "fill"), "patternFill");

        // A solid fill of a conditional format is its background color (Excel writes it so), else its pattern's.
        var fill = pattern is null ? null : Color(Child(pattern, "bgColor")) ?? Color(Child(pattern, "fgColor"));
        if (pattern is not null && (string?)pattern.Attribute("patternType") == "none")
        {
            fill = null;
        }

        var border = Child(dxf, "border");
        CellBorder? Side(string name) => Child(border, name) is { } side && (string?)side.Attribute("style") is { } style && style != "none"
            ? XlsxStyles.Border(style, Color(Child(side, "color")))
            : null;
        var underline = Child(font, "u") is { } u ? (string?)u.Attribute("val") switch
        {
            "none" => CellUnderline.None,
            "double" or "doubleAccounting" => CellUnderline.Double,
            _ => CellUnderline.Single,
        } : (CellUnderline?)null;
        return new DifferentialFormat(On(font, "b"), On(font, "i"), underline, On(font, "strike"), Color(Child(font, "color")), fill,
            Side("top"), Side("right"), Side("bottom"), Side("left"));
    }

    /// <summary>A cell's format with this one over it.</summary>
    public CellFormat Apply(CellFormat format) => format with
    {
        Bold = Bold ?? format.Bold,
        Italic = Italic ?? format.Italic,
        Underline = Underline ?? format.Underline,
        Strikethrough = Strikethrough ?? format.Strikethrough,
        Color = Color ?? format.Color,
        Fill = Fill ?? format.Fill,
        Top = Top ?? format.Top,
        Right = Right ?? format.Right,
        Bottom = Bottom ?? format.Bottom,
        Left = Left ?? format.Left,
    };
}

/// <summary>
/// Applies a worksheet's conditional formatting to its cells, over their stored values (formulas are not calculated):
/// rules comparing values (<c>cellIs</c>), color scales, top and bottom ranks and percents, above or below average,
/// duplicate and unique values, text that contains, begins or ends with a text, blanks and errors. Rules are applied in
/// their priority, a rule that stops (<c>stopIfTrue</c>) ending the rules after it for its cells. Rules with formulas
/// (<c>expression</c>), data bars, icon sets and dates are not applied, and are counted.
/// </summary>
internal static class ConditionalFormatting
{
    /// <summary>The most cells (over all rules) a sheet's rules are tried on; rules beyond it are counted as not applied.</summary>
    private const long MaxChecks = 20_000_000;

    private readonly record struct Rule(XElement Element, List<CellRange> Ranges, int Priority);

    /// <summary>Applies the rules of a worksheet's <c>conditionalFormatting</c> elements to its cells.</summary>
    public static void Apply(XlsxWorksheet sheet, IReadOnlyList<XElement> elements, XlsxStyles styles, Action? checkpoint)
    {
        var formats = styles.ConditionalFormats;
        var rules = new List<Rule>();
        foreach (var element in elements)
        {
            var ranges = ((string?)element.Attribute("sqref") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(XlsxWorkbook.ParseRange).OfType<CellRange>().Where(r => r.Row1 >= 0 && r.Column1 >= 0).Take(1000).ToList();
            foreach (var rule in element.Elements().Where(e => e.Name.LocalName == "cfRule"))
            {
                rules.Add(new Rule(rule, ranges, int.TryParse((string?)rule.Attribute("priority"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : int.MaxValue));
            }
        }

        var stopped = new HashSet<long>();
        long checks = 0;
        foreach (var rule in rules.OrderBy(r => r.Priority))
        {
            checkpoint?.Invoke();
            var cells = sheet.Cells.Keys.Where(key => rule.Ranges.Any(r => In(r, key))).ToList();
            checks += cells.Count;
            if (checks > MaxChecks || !ApplyRule(sheet, rule.Element, cells, formats, styles.Colors, stopped))
            {
                sheet.Unsupported.Add(SheetDiagnosticKind.ConditionalFormatting);
            }
        }
    }

    private static bool In(CellRange range, long key)
    {
        var (row, column) = ((int)(key >> 16), (int)(key & 0xFFFF));
        return row >= range.Row1 && row <= range.Row2 && column >= range.Column1 && column <= range.Column2;
    }

    /// <summary>Applies a rule to the cells of its ranges (not those a rule before it stopped); <c>false</c> for a rule that is not supported.</summary>
    private static bool ApplyRule(XlsxWorksheet sheet, XElement rule, List<long> cells, IReadOnlyList<DifferentialFormat> formats, XlsxColors palette, HashSet<long> stopped)
    {
        var type = (string?)rule.Attribute("type");
        var format = int.TryParse((string?)rule.Attribute("dxfId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dxf) && dxf >= 0 && dxf < formats.Count ? formats[dxf] : null;
        var stop = (string?)rule.Attribute("stopIfTrue") is "1" or "true";
        cells = [.. cells.Where(c => !stopped.Contains(c))];
        double? Number(long key) => sheet.Numbers.TryGetValue(key, out var n) ? n : null;
        string Text(long key) => sheet.Cells[key].Text;
        var numbers = cells.Select(Number).OfType<double>().ToList();
        Func<long, bool>? matches = null;
        switch (type)
        {
            case "colorScale":
                return ColorScale(sheet, rule, cells, numbers, palette);
            case "cellIs":
            {
                var formulas = rule.Elements().Where(e => e.Name.LocalName == "formula").Select(e => Constant(e.Value)).ToList();
                if (formulas.Count == 0 || formulas.Any(f => f is null))
                {
                    return false; // A formula that refers to cells or calculates.
                }

                var a = formulas[0]!.Value;
                var b = formulas.Count > 1 ? formulas[1]!.Value : a;
                matches = key => Compare((string?)rule.Attribute("operator"), Number(key), Text(key), a, b);
                break;
            }

            case "top10":
            {
                var rank = int.TryParse((string?)rule.Attribute("rank"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? Math.Max(r, 0) : 10;
                var bottom = (string?)rule.Attribute("bottom") is "1" or "true";
                var count = (string?)rule.Attribute("percent") is "1" or "true" ? Math.Max(1, (int)(numbers.Count * rank / 100.0)) : rank;
                var sorted = bottom ? numbers.Order().ToList() : numbers.OrderDescending().ToList();
                if (sorted.Count == 0 || count == 0)
                {
                    return true;
                }

                var limit = sorted[Math.Min(count, sorted.Count) - 1];
                matches = key => Number(key) is { } n && (bottom ? n <= limit : n >= limit);
                break;
            }

            case "aboveAverage":
            {
                if (numbers.Count == 0)
                {
                    return true;
                }

                var average = numbers.Average();
                var deviations = int.TryParse((string?)rule.Attribute("stdDev"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 0;
                var spread = numbers.Count > 1 ? Math.Sqrt(numbers.Sum(n => (n - average) * (n - average)) / (numbers.Count - 1)) * deviations : 0;
                var above = (string?)rule.Attribute("aboveAverage") is not ("0" or "false");
                var equal = (string?)rule.Attribute("equalAverage") is "1" or "true";
                matches = key => Number(key) is { } n && (above
                    ? n > average + spread || (equal && n == average + spread)
                    : n < average - spread || (equal && n == average - spread));
                break;
            }

            case "duplicateValues" or "uniqueValues":
            {
                var counts = cells.Where(c => Text(c).Length > 0).GroupBy(c => Text(c).ToUpperInvariant()).ToDictionary(g => g.Key, g => g.Count());
                var duplicate = type == "duplicateValues";
                matches = key => Text(key).Length > 0 && counts[Text(key).ToUpperInvariant()] > 1 == duplicate;
                break;
            }

            case "containsText" or "notContainsText" or "beginsWith" or "endsWith":
            {
                var text = (string?)rule.Attribute("text") ?? string.Empty;
                matches = key => type switch
                {
                    "containsText" => Text(key).Contains(text, StringComparison.OrdinalIgnoreCase),
                    "notContainsText" => !Text(key).Contains(text, StringComparison.OrdinalIgnoreCase),
                    "beginsWith" => Text(key).StartsWith(text, StringComparison.OrdinalIgnoreCase),
                    _ => Text(key).EndsWith(text, StringComparison.OrdinalIgnoreCase),
                };
                break;
            }

            case "containsBlanks" or "notContainsBlanks":
                matches = key => string.IsNullOrWhiteSpace(Text(key)) == (type == "containsBlanks");
                break;
            case "containsErrors" or "notContainsErrors":
                matches = key => IsError(Text(key)) == (type == "containsErrors");
                break;
            default:
                return false; // Formulas, data bars, icon sets, dates.
        }

        foreach (var key in cells.Where(matches))
        {
            if (format is not null)
            {
                var cell = sheet.Cells[key];
                sheet.Cells[key] = cell with { Format = format.Apply(cell.Format ?? CellFormat.Default) };
            }

            if (stop)
            {
                stopped.Add(key);
            }
        }

        return true;
    }

    /// <summary>A 2- or 3-color scale: each number's fill between the colors of the scale's points.</summary>
    private static bool ColorScale(XlsxWorksheet sheet, XElement rule, List<long> cells, List<double> numbers, XlsxColors palette)
    {
        var scale = rule.Elements().FirstOrDefault(e => e.Name.LocalName == "colorScale");
        var points = scale?.Elements().Where(e => e.Name.LocalName == "cfvo").ToList() ?? [];
        var colors = scale?.Elements().Where(e => e.Name.LocalName == "color").Select(c => palette.Resolve((string?)c.Attribute("rgb"), (string?)c.Attribute("indexed"), (string?)c.Attribute("theme"), (string?)c.Attribute("tint"), (string?)c.Attribute("auto"))).ToList() ?? [];
        if (points.Count is < 2 or > 3 || colors.Count != points.Count || colors.Any(c => c is null) || numbers.Count == 0)
        {
            return numbers.Count == 0 && points.Count is 2 or 3;
        }

        var sorted = numbers.Order().ToList();
        var values = new double[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var value = double.TryParse((string?)points[i].Attribute("val"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 50;
            values[i] = (string?)points[i].Attribute("type") switch
            {
                "min" => sorted[0],
                "max" => sorted[^1],
                "num" => value,
                "percent" => sorted[0] + ((sorted[^1] - sorted[0]) * value / 100),
                "percentile" => Percentile(sorted, value / 100),
                _ => double.NaN, // A formula.
            };
        }

        if (values.Any(double.IsNaN))
        {
            return false;
        }

        foreach (var key in cells)
        {
            if (!sheet.Numbers.TryGetValue(key, out var n))
            {
                continue;
            }

            var (i, t) = values.Length == 2 || n <= values[1] ? (0, Fraction(n, values[0], values[1])) : (1, Fraction(n, values[1], values[2]));
            var (from, to) = (colors[i]!.Value, colors[i + 1]!.Value);
            var fill = new PdfColor(from.Red + ((to.Red - from.Red) * t), from.Green + ((to.Green - from.Green) * t), from.Blue + ((to.Blue - from.Blue) * t));
            var cell = sheet.Cells[key];
            sheet.Cells[key] = cell with { Format = (cell.Format ?? CellFormat.Default) with { Fill = fill } };
        }

        return true;
    }

    private static double Fraction(double n, double low, double high) => high > low ? Math.Clamp((n - low) / (high - low), 0, 1) : n >= high ? 1 : 0;

    /// <summary>A percentile as Excel computes it (linear between the ranks).</summary>
    internal static double Percentile(List<double> sorted, double p)
    {
        var rank = Math.Clamp(p, 0, 1) * (sorted.Count - 1);
        var low = (int)Math.Floor(rank);
        return low + 1 < sorted.Count ? sorted[low] + ((sorted[low + 1] - sorted[low]) * (rank - low)) : sorted[low];
    }

    /// <summary>A constant of a rule's formula: a number, or a text in quotes; <c>null</c> for anything else.</summary>
    internal static (double? Number, string Text)? Constant(string formula)
    {
        formula = formula.Trim();
        if (formula.Length >= 2 && formula[0] == '"' && formula[^1] == '"')
        {
            return (null, formula[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal));
        }

        return double.TryParse(formula, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? (n, formula) : null;
    }

    /// <summary>Whether a cell's value meets a comparison with one or two constants.</summary>
    internal static bool Compare(string? op, double? number, string text, (double? Number, string Text) a, (double? Number, string Text) b)
    {
        int? Order((double? Number, string Text) c) => number is { } n && c.Number is { } m ? n.CompareTo(m)
            : number is null && c.Number is null ? string.Compare(text, c.Text, StringComparison.OrdinalIgnoreCase)
            : null;
        if (Order(a) is not { } x)
        {
            return op == "notEqual";
        }

        var y = Order(b) ?? 0;
        return op switch
        {
            "between" => x >= 0 && y <= 0,
            "notBetween" => x < 0 || y > 0,
            "equal" => x == 0,
            "notEqual" => x != 0,
            "greaterThan" => x > 0,
            "lessThan" => x < 0,
            "greaterThanOrEqual" => x >= 0,
            "lessThanOrEqual" => x <= 0,
            _ => false,
        };
    }

    private static bool IsError(string text) => text is "#NULL!" or "#DIV/0!" or "#VALUE!" or "#REF!" or "#NAME?" or "#NUM!" or "#N/A" or "#GETTING_DATA";
}

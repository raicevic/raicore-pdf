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
/// A point of a color scale, data bar or icon set (<c>cfvo</c>): its type (<c>min</c>, <c>max</c>, <c>num</c>,
/// <c>percent</c>, <c>percentile</c>, <c>formula</c>, <c>autoMin</c>, <c>autoMax</c>), its value, and for icon sets
/// whether a value equal to it reaches it.
/// </summary>
internal sealed record ConditionalValue(string Type, string? Value, bool GreaterOrEqual = true);

/// <summary>
/// A conditional formatting rule, as read from either format (<see cref="FromXlsx"/>, and the BIFF records of .xls
/// workbooks): its type and range, its priority, the format it applies, and what its type needs. Types and values are
/// named as in SpreadsheetML.
/// </summary>
internal sealed record ConditionalRule
{
    /// <summary>The rule's type: <c>cellIs</c>, <c>expression</c>, <c>colorScale</c>, <c>dataBar</c>, <c>iconSet</c>, <c>top10</c>...</summary>
    public required string Type { get; init; }

    /// <summary>The ranges the rule applies to; relative references of its formulas are written for the first one's top left cell.</summary>
    public IReadOnlyList<CellRange> Ranges { get; init; } = [];

    /// <summary>The priority: rules with lower numbers go first.</summary>
    public int Priority { get; init; } = int.MaxValue;

    /// <summary>The format applied to the cells that meet the rule, or <c>null</c>.</summary>
    public DifferentialFormat? Format { get; init; }

    /// <summary>Whether the rules after this one are not applied to the cells that meet it.</summary>
    public bool StopIfTrue { get; init; }

    /// <summary>The comparison of a <c>cellIs</c> rule (<c>between</c>, <c>greaterThan</c>...).</summary>
    public string? Operator { get; init; }

    /// <summary>The formulas (without their <c>=</c>): the values of a <c>cellIs</c> rule, the condition of an <c>expression</c>.</summary>
    public IReadOnlyList<string> Formulas { get; init; } = [];

    /// <summary>The text of a text rule (<c>containsText</c>, <c>beginsWith</c>...).</summary>
    public string? Text { get; init; }

    /// <summary>The rank of a <c>top10</c> rule; with <see cref="Percent"/>, a percentage of the values.</summary>
    public int Rank { get; init; } = 10;

    /// <summary>Whether a <c>top10</c> rule is for the lowest values.</summary>
    public bool Bottom { get; init; }

    /// <summary>Whether a <c>top10</c> rule's rank is a percentage.</summary>
    public bool Percent { get; init; }

    /// <summary>Whether an <c>aboveAverage</c> rule is for values above the average (otherwise below).</summary>
    public bool AboveAverage { get; init; } = true;

    /// <summary>Whether an <c>aboveAverage</c> rule takes in values equal to the average.</summary>
    public bool EqualAverage { get; init; }

    /// <summary>The standard deviations above or below the average an <c>aboveAverage</c> rule's values are.</summary>
    public int StandardDeviations { get; init; }

    /// <summary>The period of a <c>timePeriod</c> rule (<c>today</c>, <c>lastWeek</c>...).</summary>
    public string? TimePeriod { get; init; }

    /// <summary>The points of a color scale, data bar (minimum, maximum) or icon set (each icon's threshold).</summary>
    public IReadOnlyList<ConditionalValue> Values { get; init; } = [];

    /// <summary>The colors of a color scale's points, or the data bar's color.</summary>
    public IReadOnlyList<PdfColor?> Colors { get; init; } = [];

    /// <summary>The icon set's name (<c>3TrafficLights1</c> by default).</summary>
    public string IconSet { get; init; } = "3TrafficLights1";

    /// <summary>Whether a data bar or icon set shows the cell's value too.</summary>
    public bool ShowValue { get; init; } = true;

    /// <summary>Whether an icon set's icons go in reverse order.</summary>
    public bool Reverse { get; init; }

    /// <summary>Whether an icon set has icons of its own choosing (<see cref="Icons"/>).</summary>
    public bool CustomIcons { get; init; }

    /// <summary>The icons of a custom icon set, one for each threshold: a set's name (<c>NoIcons</c> for none) and its icon's index.</summary>
    public IReadOnlyList<CellIcon> Icons { get; init; } = [];

    /// <summary>Whether a data bar fades to white (Excel 2007's bars, and Excel 2010's by default) or is solid.</summary>
    public bool Gradient { get; init; } = true;

    /// <summary>A data bar's border color, or <c>null</c> for none.</summary>
    public PdfColor? BorderColor { get; init; }

    /// <summary>The color of a data bar's negative values, or <c>null</c> for red.</summary>
    public PdfColor? NegativeColor { get; init; }

    /// <summary>
    /// The border color of a data bar's negative values, or <c>null</c> for its border color (Excel 2010's default:
    /// <c>negativeBarBorderColorSameAsPositive</c>).
    /// </summary>
    public PdfColor? NegativeBorderColor { get; init; }

    /// <summary>
    /// Where an Excel 2010 data bar's axis is: <c>automatic</c> (where the values change sign, when they do),
    /// <c>middle</c> (the middle of the cell) or <c>none</c> (negative values have bars as positive ones).
    /// </summary>
    public string AxisPosition { get; init; } = "automatic";

    /// <summary>The color of a data bar's axis (a dashed line), black by default.</summary>
    public PdfColor AxisColor { get; init; } = PdfColor.Black;

    /// <summary>The length of a data bar's shortest and longest bars, in percent of the cell's width.</summary>
    public (double Min, double Max) Lengths { get; init; } = (10, 90);

    /// <summary>
    /// Whether a data bar is Excel 2010's (its extension list's): negative values have bars of their own to the left
    /// of an axis.
    /// </summary>
    public bool Extended { get; init; }

    /// <summary>
    /// Reads a rule (<c>cfRule</c>) of SpreadsheetML: of a <c>conditionalFormatting</c> element (its <c>dxfId</c> one of
    /// <paramref name="formats"/>), or of the extension list's (<c>x14:cfRule</c>, its format in it, its formulas in
    /// <c>xm:f</c> elements).
    /// </summary>
    public static ConditionalRule FromXlsx(XElement rule, List<CellRange> ranges, IReadOnlyList<DifferentialFormat> formats, XlsxColors colors)
    {
        string? Attribute(XElement e, string name) => (string?)e.Attribute(name);
        bool Flag(XElement e, string name, bool fallback = false) => Attribute(e, name) is { } v ? v is "1" or "true" : fallback;
        int Number(XElement e, string name, int fallback) =>
            int.TryParse(Attribute(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name);
        PdfColor? Color(XElement? c) => c is null ? null
            : colors.Resolve(Attribute(c, "rgb"), Attribute(c, "indexed"), Attribute(c, "theme"), Attribute(c, "tint"), Attribute(c, "auto"));
        List<ConditionalValue> Points(XElement? parent) => parent?.Elements().Where(e => e.Name.LocalName == "cfvo").Take(16)
            .Select(e => new ConditionalValue(Attribute(e, "type") ?? "num", Attribute(e, "val") ?? Child(e, "f")?.Value, Flag(e, "gte", true))).ToList() ?? [];

        var type = Attribute(rule, "type") ?? string.Empty;
        var dxfId = Number(rule, "dxfId", -1);
        var format = dxfId >= 0 && dxfId < formats.Count ? formats[dxfId] : Child(rule, "dxf") is { } inline ? DifferentialFormat.Read(inline, colors) : null;
        var result = new ConditionalRule
        {
            Type = type,
            Ranges = ranges,
            Priority = Number(rule, "priority", int.MaxValue),
            Format = format,
            StopIfTrue = Flag(rule, "stopIfTrue"),
            Operator = Attribute(rule, "operator"),
            Formulas = [.. rule.Elements().Where(e => e.Name.LocalName is "formula" or "f").Take(3).Select(e => e.Value)],
            Text = Attribute(rule, "text"),
            Rank = Math.Max(Number(rule, "rank", 10), 0),
            Bottom = Flag(rule, "bottom"),
            Percent = Flag(rule, "percent"),
            AboveAverage = Flag(rule, "aboveAverage", true),
            EqualAverage = Flag(rule, "equalAverage"),
            StandardDeviations = Math.Clamp(Number(rule, "stdDev", 0), 0, 3),
            TimePeriod = Attribute(rule, "timePeriod"),
        };
        switch (type)
        {
            case "colorScale" when Child(rule, "colorScale") is { } scale:
                return result with
                {
                    Values = Points(scale),
                    Colors = [.. scale.Elements().Where(e => e.Name.LocalName == "color").Take(16).Select(Color)],
                };
            case "dataBar" when Child(rule, "dataBar") is { } bar:
                return result with
                {
                    Values = Points(bar),
                    Colors = [Color(Child(bar, "color")) ?? Color(Child(bar, "fillColor"))],
                    ShowValue = Flag(bar, "showValue", true),
                    Lengths = (Math.Clamp(Number(bar, "minLength", 10), 0, 100), Math.Clamp(Number(bar, "maxLength", 90), 0, 100)),
                };
            case "iconSet" when Child(rule, "iconSet") is { } icons:
                return result with
                {
                    Values = Points(icons),
                    IconSet = Attribute(icons, "iconSet") ?? "3TrafficLights1",
                    ShowValue = Flag(icons, "showValue", true),
                    Reverse = Flag(icons, "reverse"),
                    CustomIcons = Flag(icons, "custom"),
                    Icons = [.. icons.Elements().Where(e => e.Name.LocalName == "cfIcon").Take(16)
                        .Select(e => new CellIcon(Attribute(e, "iconSet") ?? "NoIcons", Math.Clamp(Number(e, "iconId", 0), 0, 4)))],
                };
            default:
                return result;
        }
    }

    /// <summary>
    /// Adds to a data bar what Excel 2010 keeps of it in the extension list (<c>x14:dataBar</c>): solid or gradient,
    /// border and negative colors, lengths, and its own minimum and maximum (<c>autoMin</c>, <c>autoMax</c>).
    /// </summary>
    public ConditionalRule WithExtension(XElement extended, XlsxColors colors)
    {
        if (Type != "dataBar" || extended.Elements().FirstOrDefault(e => e.Name.LocalName == "dataBar") is not { } bar)
        {
            return this;
        }

        string? Attribute(string name) => (string?)bar.Attribute(name);
        PdfColor? Color(string name) => bar.Elements().FirstOrDefault(e => e.Name.LocalName == name) is { } c
            ? colors.Resolve((string?)c.Attribute("rgb"), (string?)c.Attribute("indexed"), (string?)c.Attribute("theme"), (string?)c.Attribute("tint"), (string?)c.Attribute("auto"))
            : null;
        int Length(string name, int fallback) => int.TryParse(Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 0, 100) : fallback;
        bool Flag(string name, bool fallback) => Attribute(name) is { } v ? v is "1" or "true" : fallback;
        var points = bar.Elements().Where(e => e.Name.LocalName == "cfvo").Take(2)
            .Select(e => new ConditionalValue((string?)e.Attribute("type") ?? "num", (string?)e.Attribute("val") ?? e.Elements().FirstOrDefault(f => f.Name.LocalName == "f")?.Value)).ToList();
        return this with
        {
            Extended = true,
            Gradient = Attribute("gradient") is not ("0" or "false"),
            BorderColor = Attribute("border") is "1" or "true" ? Color("borderColor") : null,
            NegativeColor = Flag("negativeBarColorSameAsPositive", false) ? (Colors.Count > 0 ? Colors[0] : null) : Color("negativeFillColor"),
            NegativeBorderColor = Flag("negativeBarBorderColorSameAsPositive", true) ? null : Color("negativeBorderColor") ?? PdfColor.FromRgb(255, 0, 0),
            AxisPosition = Attribute("axisPosition") is "middle" or "none" ? Attribute("axisPosition")! : "automatic",
            AxisColor = Color("axisColor") ?? PdfColor.Black,
            Lengths = (Length("minLength", 0), Length("maxLength", 100)),
            Values = points.Count == 2 ? points : Values,
        };
    }
}

/// <summary>
/// Applies a worksheet's conditional formatting to its cells, over their stored values (formulas are not calculated, but
/// the rules' own formulas are, by <see cref="ConditionalFormula"/>): rules comparing values (<c>cellIs</c>) and formula
/// rules (<c>expression</c>), color scales, data bars, icon sets, top and bottom ranks and percents, above or below
/// average, duplicate and unique values, text that contains, begins or ends with a text, blanks, errors and dates in a
/// period. Rules are applied in their priority, a rule that stops (<c>stopIfTrue</c>) ending the rules after it for its
/// cells. Rules that are not supported (a formula that is not) are counted.
/// </summary>
internal static class ConditionalFormatting
{
    /// <summary>The most cells (over all rules) a sheet's rules are tried on; rules beyond it are counted as not applied.</summary>
    private const long MaxChecks = 20_000_000;

    /// <summary>The most work (formula nodes and range cells computed) of a sheet's formulas; rules beyond it are counted as not applied.</summary>
    private const long MaxFormulaWork = 50_000_000;

    /// <summary>The most cells (empty ones too) a formula or <c>cellIs</c> rule is tried on; beyond, only those with something.</summary>
    private const long MaxEmptyChecks = 2_000_000;

    /// <summary>Reads the rules of a worksheet's <c>conditionalFormatting</c> elements and of its extension list's.</summary>
    public static List<ConditionalRule> ReadXlsx(IReadOnlyList<XElement> elements, IReadOnlyList<XElement> extended, XlsxStyles styles)
    {
        static List<CellRange> Ranges(string? sqref) => [.. (sqref ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(XlsxWorkbook.ParseRange).OfType<CellRange>().Where(r => r.Row1 >= 0 && r.Column1 >= 0).Take(1000)];
        static IEnumerable<XElement> Children(XElement e, string name) => e.Elements().Where(c => c.Name.LocalName == name);

        // The extension list's rules, by their id: those a rule of the main list refers to add to it, the others stand alone.
        var byId = new Dictionary<string, (XElement Rule, List<CellRange> Ranges)>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in extended)
        {
            var ranges = Ranges(Children(element, "sqref").FirstOrDefault()?.Value);
            foreach (var rule in Children(element, "cfRule"))
            {
                byId.TryAdd((string?)rule.Attribute("id") ?? $"#{byId.Count}", (rule, ranges));
            }
        }

        var rules = new List<ConditionalRule>();
        foreach (var element in elements)
        {
            var ranges = Ranges((string?)element.Attribute("sqref"));
            foreach (var cfRule in Children(element, "cfRule"))
            {
                var rule = ConditionalRule.FromXlsx(cfRule, ranges, styles.ConditionalFormats, styles.Colors);
                var id = cfRule.Descendants().FirstOrDefault(e => e.Name.LocalName == "id")?.Value.Trim();
                if (id is not null && byId.Remove(id, out var extension))
                {
                    rule = rule.WithExtension(extension.Rule, styles.Colors);
                }

                rules.Add(rule);
            }
        }

        foreach (var (rule, ranges) in byId.Values)
        {
            var read = ConditionalRule.FromXlsx(rule, ranges, [], styles.Colors);
            rules.Add(read.Type == "dataBar" ? read.WithExtension(rule, styles.Colors) : read);
        }

        return rules;
    }

    /// <summary>
    /// Applies rules to the cells of a sheet. Formula and <c>cellIs</c> rules are tried on the empty cells of their
    /// ranges too (within the sheet's used range), as Excel does; a format they give an empty cell is added to it.
    /// </summary>
    /// <param name="sheet">The sheet.</param>
    /// <param name="rules">The rules, in any order.</param>
    /// <param name="today">The date dates are compared with (<c>TODAY()</c>, <c>timePeriod</c>).</param>
    /// <param name="date1904">Whether the workbook counts dates from 1904.</param>
    /// <param name="maxCells">The most cells the sheet may have.</param>
    /// <param name="checkpoint">Called regularly; throws to stop.</param>
    public static void Apply(XlsxWorksheet sheet, IReadOnlyList<ConditionalRule> rules, DateTime today, bool date1904, long maxCells, Action? checkpoint)
    {
        var state = new State(sheet, today, date1904, maxCells);
        foreach (var rule in rules.OrderBy(r => r.Priority))
        {
            checkpoint?.Invoke();
            if (!state.Apply(rule))
            {
                sheet.Unsupported.Add(SheetDiagnosticKind.ConditionalFormatting);
            }
        }
    }

    private sealed class State(XlsxWorksheet sheet, DateTime today, bool date1904, long maxCells)
    {
        private readonly HashSet<long> _stopped = [];
        private long _checks;
        private long _work = MaxFormulaWork;

        private double TodaySerial => Serial(today.Date);

        private double Serial(DateTime date) => date1904 ? (date - new DateTime(1904, 1, 1)).TotalDays : date.ToOADate();

        private DateTime? Date(double serial) =>
            date1904 ? (serial is >= 0 and < 2_957_003 ? new DateTime(1904, 1, 1).AddDays(Math.Floor(serial)) : null)
            : serial is >= 61 and < 2_958_466 ? DateTime.FromOADate(Math.Floor(serial)) : null;

        private double? Number(long key) => sheet.Numbers.TryGetValue(key, out var n) ? n : null;

        private string Text(long key) => sheet.Cells.TryGetValue(key, out var cell) ? cell.Text : string.Empty;

        /// <summary>A cell's stored value, as formulas read it.</summary>
        public FormulaValue Value(int row, int column)
        {
            var key = XlsxWorksheet.Key(row, column);
            if (sheet.Numbers.TryGetValue(key, out var number))
            {
                return FormulaValue.Of(number);
            }

            if (!sheet.Cells.TryGetValue(key, out var cell) || cell.Kind == CellKind.Empty)
            {
                return FormulaValue.Blank;
            }

            return IsError(cell.Text) ? FormulaValue.Error(cell.Text) : FormulaValue.Of(cell.Text);
        }

        private static bool In(CellRange range, long key)
        {
            var (row, column) = ((int)(key >> 16), (int)(key & 0xFFFF));
            return row >= range.Row1 && row <= range.Row2 && column >= range.Column1 && column <= range.Column2;
        }

        /// <summary>The cells of a rule's ranges: those that have something, or with <paramref name="empty"/> every cell within the used range.</summary>
        private List<long>? Cells(ConditionalRule rule, bool empty)
        {
            // Empty cells are tried within the used range, unless there are too many of them: then only those with something.
            empty &= rule.Ranges.Sum(r => (long)Math.Max(0, Math.Min(r.Row2, sheet.LastRow) - r.Row1 + 1) * Math.Max(0, Math.Min(r.Column2, sheet.LastColumn) - r.Column1 + 1))
                <= MaxEmptyChecks;
            if (!empty)
            {
                var cells = sheet.Cells.Keys.Where(key => rule.Ranges.Any(r => In(r, key))).ToList();
                _checks += cells.Count;
                return _checks > MaxChecks ? null : cells;
            }

            var all = new HashSet<long>();
            foreach (var range in rule.Ranges)
            {
                var (lastRow, lastColumn) = (Math.Min(range.Row2, sheet.LastRow), Math.Min(range.Column2, sheet.LastColumn));
                var size = (long)Math.Max(0, lastRow - range.Row1 + 1) * Math.Max(0, lastColumn - range.Column1 + 1);
                if ((_checks += size) > MaxChecks)
                {
                    return null;
                }

                for (var row = range.Row1; row <= lastRow; row++)
                {
                    for (var column = range.Column1; column <= lastColumn; column++)
                    {
                        all.Add(XlsxWorksheet.Key(row, column));
                    }
                }
            }

            return [.. all.Order()];
        }

        /// <summary>Applies a rule to the cells of its ranges (not those a rule before it stopped); <c>false</c> for a rule that is not supported.</summary>
        public bool Apply(ConditionalRule rule)
        {
            var type = rule.Type;
            var cells = Cells(rule, type is "expression" or "cellIs");
            if (cells is null)
            {
                return false;
            }

            cells = [.. cells.Where(c => !_stopped.Contains(c))];
            var numbers = cells.Select(Number).OfType<double>().ToList();
            Func<long, bool>? matches;
            switch (type)
            {
                case "colorScale":
                    return ColorScale(rule, cells, numbers);
                case "dataBar":
                    return DataBar(rule, cells, numbers);
                case "iconSet":
                    return IconSet(rule, cells, numbers);
                case "cellIs" or "expression":
                    matches = FormulaRule(rule);
                    if (matches is null)
                    {
                        return false;
                    }

                    break;
                case "top10":
                {
                    var count = rule.Percent ? Math.Max(1, (int)(numbers.Count * rule.Rank / 100.0)) : rule.Rank;
                    var sorted = rule.Bottom ? numbers.Order().ToList() : numbers.OrderDescending().ToList();
                    if (sorted.Count == 0 || count == 0)
                    {
                        return true;
                    }

                    var limit = sorted[Math.Min(count, sorted.Count) - 1];
                    matches = key => Number(key) is { } n && (rule.Bottom ? n <= limit : n >= limit);
                    break;
                }

                case "aboveAverage":
                {
                    if (numbers.Count == 0)
                    {
                        return true;
                    }

                    var average = numbers.Average();
                    var spread = numbers.Count > 1 ? Math.Sqrt(numbers.Sum(n => (n - average) * (n - average)) / (numbers.Count - 1)) * rule.StandardDeviations : 0;
                    matches = key => Number(key) is { } n && (rule.AboveAverage
                        ? n > average + spread || (rule.EqualAverage && n == average + spread)
                        : n < average - spread || (rule.EqualAverage && n == average - spread));
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
                    var text = rule.Text ?? string.Empty;
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
                case "timePeriod":
                    matches = TimePeriod(rule.TimePeriod);
                    if (matches is null)
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }

            // The cells that meet the rule, all found before any is formatted: a rule whose formulas' work runs out is not
            // applied at all (and counted), rather than to some of its cells.
            List<long> met;
            try
            {
                met = [.. cells.Where(matches)];
            }
            catch (OverflowException)
            {
                return false;
            }

            foreach (var key in met)
            {
                if (rule.Format is { } format)
                {
                    Set(key, format.Apply(sheet.Cells.TryGetValue(key, out var cell) ? cell.Format ?? CellFormat.Default : CellFormat.Default));
                }

                if (rule.StopIfTrue)
                {
                    _stopped.Add(key);
                }
            }

            return true;
        }

        /// <summary>Gives a cell a format: an empty cell is added if the format shows something.</summary>
        private void Set(long key, CellFormat format)
        {
            if (sheet.Cells.TryGetValue(key, out var cell))
            {
                sheet.Cells[key] = cell with { Format = format };
            }
            else if (format.IsVisible)
            {
                sheet.Add((int)(key >> 16), (int)(key & 0xFFFF), SheetCell.Empty with { Format = format }, maxCells);
            }
        }

        /// <summary>
        /// Whether a cell meets a <c>cellIs</c> rule (its value against its formulas' values) or an <c>expression</c> rule
        /// (its formula is true), the formulas computed for the cell; <c>null</c> if a formula is not supported.
        /// </summary>
        private Func<long, bool>? FormulaRule(ConditionalRule rule)
        {
            if (rule.Ranges.Count == 0 || rule.Formulas.Count == 0)
            {
                return rule.Ranges.Count == 0 ? _ => false : null;
            }

            var origin = rule.Ranges[0];
            var formulas = new List<ConditionalFormula>();
            foreach (var text in rule.Formulas.Take(rule.Type == "expression" ? 1 : 2))
            {
                if (ConditionalFormula.Parse(text, origin.Row1, origin.Column1) is not { } formula)
                {
                    return null;
                }

                formulas.Add(formula);
            }

            var serial = Math.Floor(TodaySerial);
            FormulaValue Compute(ConditionalFormula formula, int row, int column) =>
                formula.Evaluate(Value, row, column, serial, ref _work) ?? throw new OverflowException("The formulas' work ran out.");
            return key =>
            {
                var (row, column) = ((int)(key >> 16), (int)(key & 0xFFFF));
                if (rule.Type == "expression")
                {
                    return Compute(formulas[0], row, column).IsTrue;
                }

                var value = Value(row, column);
                var a = Compute(formulas[0], row, column);
                var b = formulas.Count > 1 ? Compute(formulas[1], row, column) : a;
                return Compare(rule.Operator, value, a, b);
            };
        }

        /// <summary>Whether a cell's date is in a period of a <c>timePeriod</c> rule (weeks start on Sunday); <c>null</c> for an unknown period.</summary>
        private Func<long, bool>? TimePeriod(string? period)
        {
            var day = today.Date;
            var week = day.AddDays(-(int)day.DayOfWeek);
            var month = new DateTime(day.Year, day.Month, 1);
            (DateTime From, DateTime To)? span = period switch
            {
                "today" => (day, day),
                "yesterday" => (day.AddDays(-1), day.AddDays(-1)),
                "tomorrow" => (day.AddDays(1), day.AddDays(1)),
                "last7Days" => (day.AddDays(-6), day),
                "thisWeek" => (week, week.AddDays(6)),
                "lastWeek" => (week.AddDays(-7), week.AddDays(-1)),
                "nextWeek" => (week.AddDays(7), week.AddDays(13)),
                "thisMonth" => (month, month.AddMonths(1).AddDays(-1)),
                "lastMonth" => (month.AddMonths(-1), month.AddDays(-1)),
                "nextMonth" => (month.AddMonths(1), month.AddMonths(2).AddDays(-1)),
                _ => null,
            };
            if (span is not var (from, to))
            {
                return null;
            }

            return key => Number(key) is { } n && Date(n) is { } date && date >= from && date <= to;
        }

        /// <summary>The value of a point of a scale, bar or icon set over the numbers (sorted); <c>null</c> if it cannot be computed.</summary>
        private double? Point(ConditionalValue point, List<double> sorted, double fallback)
        {
            var value = double.TryParse(point.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : (double?)null;
            return point.Type switch
            {
                "min" => sorted[0],
                "max" => sorted[^1],
                "autoMin" => Math.Min(0, sorted[0]),
                "autoMax" => Math.Max(0, sorted[^1]),
                "num" => value ?? Constant(point.Value),
                "percent" => sorted[0] + ((sorted[^1] - sorted[0]) * (value ?? fallback) / 100),
                "percentile" => Percentile(sorted, (value ?? fallback) / 100),
                "formula" => Constant(point.Value),
                _ => null,
            };
        }

        /// <summary>A formula that does not depend on the cell (<c>10*2</c>, <c>$B$1</c>) computed once; <c>null</c> otherwise.</summary>
        private double? Constant(string? text)
        {
            if (ConditionalFormula.Parse(text, 0, 0) is not { } formula || formula.Evaluate(Value, 0, 0, Math.Floor(TodaySerial), ref _work) is not { } result)
            {
                return null;
            }

            // The same at two cells apart: it does not depend on the cell.
            var other = formula.Evaluate(Value, 1, 1, Math.Floor(TodaySerial), ref _work);
            return result.Kind == FormulaKind.Number && other == result ? result.Number : null;
        }

        /// <summary>A 2- or 3-color scale: each number's fill between the colors of the scale's points.</summary>
        private bool ColorScale(ConditionalRule rule, List<long> cells, List<double> numbers)
        {
            var points = rule.Values;
            var colors = rule.Colors;
            if (points.Count is < 2 or > 3 || colors.Count != points.Count || colors.Any(c => c is null) || numbers.Count == 0)
            {
                return numbers.Count == 0 && points.Count is 2 or 3;
            }

            var sorted = numbers.Order().ToList();
            var values = new double[points.Count];
            for (var i = 0; i < points.Count; i++)
            {
                if (Point(points[i], sorted, 50) is not { } value)
                {
                    return false;
                }

                values[i] = value;
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
                Set(key, (sheet.Cells[key].Format ?? CellFormat.Default) with { Fill = fill });
            }

            return true;
        }

        /// <summary>
        /// A data bar: each number's bar as long as its place between the minimum and maximum, within the bar's shortest
        /// and longest lengths. Excel 2010's bars of negative values go left from an axis where the values change sign.
        /// </summary>
        private bool DataBar(ConditionalRule rule, List<long> cells, List<double> numbers)
        {
            if (rule.Values.Count != 2 || rule.Colors.Count == 0 || rule.Colors[0] is not { } color)
            {
                return false;
            }

            if (numbers.Count == 0)
            {
                return true;
            }

            var sorted = numbers.Order().ToList();
            if (Point(rule.Values[0], sorted, 0) is not { } low || Point(rule.Values[1], sorted, 100) is not { } high)
            {
                return false;
            }

            var (shortest, longest) = (rule.Lengths.Min / 100, Math.Max(rule.Lengths.Min, rule.Lengths.Max) / 100);

            // Excel 2010's axis: in the middle of the cells (bars on each side as long as the value's share of the
            // largest absolute value), or where the values change sign (each bar its value's share of the whole span).
            var middle = rule.Extended && rule.AxisPosition == "middle";
            var axis = middle ? 0.5
                : rule.Extended && rule.AxisPosition == "automatic" && low < 0 && high > 0 ? -low / (high - low)
                : (double?)null;
            var span = middle ? 2 * Math.Max(Math.Abs(low), Math.Abs(high)) : high - low;
            foreach (var key in cells)
            {
                if (!sheet.Numbers.TryGetValue(key, out var n))
                {
                    continue;
                }

                CellDataBar bar;
                if (axis is { } a)
                {
                    var end = span > 0 && double.IsFinite(span) ? a + (Math.Clamp(n, low, high) / span) : a;
                    bar = n < 0
                        ? new CellDataBar(end, a, rule.NegativeColor ?? PdfColor.FromRgb(255, 0, 0), rule.Gradient, rule.BorderColor is null ? null : rule.NegativeBorderColor ?? rule.BorderColor, a, rule.AxisColor)
                        : new CellDataBar(a, end, color, rule.Gradient, rule.BorderColor, a, rule.AxisColor);
                }
                else
                {
                    bar = new CellDataBar(0, shortest + ((longest - shortest) * Fraction(n, low, high)), color, rule.Gradient, rule.BorderColor);
                }

                Set(key, (sheet.Cells[key].Format ?? CellFormat.Default) with { DataBar = bar, HideText = !rule.ShowValue });
            }

            return true;
        }

        /// <summary>An icon set: each number's icon, the last whose threshold it reaches.</summary>
        private bool IconSet(ConditionalRule rule, List<long> cells, List<double> numbers)
        {
            var count = rule.IconSet.Length > 0 ? rule.IconSet[0] - '0' : 0;
            if (rule.Values.Count != count || (rule.CustomIcons ? rule.Icons.Count != count || rule.Icons.Any(i => i.Set != "NoIcons" && !SheetIcons.IsKnown(i.Set))
                : !SheetIcons.IsKnown(rule.IconSet)))
            {
                return false;
            }

            if (numbers.Count == 0)
            {
                return true;
            }

            var sorted = numbers.Order().ToList();
            var thresholds = new double[count];
            for (var i = 0; i < count; i++)
            {
                if (Point(rule.Values[i], sorted, 0) is not { } value)
                {
                    return false;
                }

                thresholds[i] = value;
            }

            foreach (var key in cells)
            {
                if (!sheet.Numbers.TryGetValue(key, out var n))
                {
                    continue;
                }

                var index = 0;
                for (var i = 1; i < count; i++)
                {
                    if (rule.Values[i].GreaterOrEqual ? n >= thresholds[i] : n > thresholds[i])
                    {
                        index = i;
                    }
                }

                index = rule.Reverse ? count - 1 - index : index;
                var icon = rule.CustomIcons ? rule.Icons[index] : new CellIcon(rule.IconSet, index);
                Set(key, (sheet.Cells[key].Format ?? CellFormat.Default) with
                {
                    Icon = icon.Set == "NoIcons" || icon.Index >= icon.Set[0] - '0' ? null : icon,
                    HideText = !rule.ShowValue,
                });
            }

            return true;
        }
    }

    private static double Fraction(double n, double low, double high) => high > low ? Math.Clamp((n - low) / (high - low), 0, 1) : n >= high ? 1 : 0;

    /// <summary>A percentile as Excel computes it (linear between the ranks).</summary>
    internal static double Percentile(List<double> sorted, double p)
    {
        var rank = Math.Clamp(p, 0, 1) * (sorted.Count - 1);
        var low = (int)Math.Floor(rank);
        return low + 1 < sorted.Count ? sorted[low] + ((sorted[low + 1] - sorted[low]) * (rank - low)) : sorted[low];
    }

    /// <summary>
    /// Whether a cell's value meets a comparison with one or two values (Excel's order: numbers, then texts without case,
    /// then booleans; an empty cell is 0 or an empty text). An error, in the cell or the values, meets none.
    /// </summary>
    internal static bool Compare(string? op, FormulaValue value, FormulaValue a, FormulaValue b)
    {
        if (value.Kind == FormulaKind.Error || a.Kind == FormulaKind.Error || b.Kind == FormulaKind.Error)
        {
            return false;
        }

        var x = ConditionalFormula.Order(value, a);
        var y = ConditionalFormula.Order(value, b);
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

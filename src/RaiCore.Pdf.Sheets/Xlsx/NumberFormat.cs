using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>A cell value formatted for display: its text, what it is, and the color its format gives it.</summary>
internal readonly record struct FormattedValue(string Text, CellKind Kind, PdfColor? Color = null);

/// <summary>The culture and date system values are formatted in.</summary>
internal sealed class FormatSettings
{
    public FormatSettings(CultureInfo culture, bool date1904)
    {
        Culture = culture;
        Date1904 = date1904;
        var numbers = culture.NumberFormat;
        DecimalSeparator = numbers.NumberDecimalSeparator;
        GroupSeparator = numbers.NumberGroupSeparator;
        Dates = culture.DateTimeFormat;
        var invariant = culture.Equals(CultureInfo.InvariantCulture);
        CurrencySymbol = invariant ? "$" : numbers.CurrencySymbol;
        CurrencyPattern = invariant ? 0 : numbers.CurrencyPositivePattern;
    }

    public CultureInfo Culture { get; }

    /// <summary>Whether serial dates count from 1904-01-01 (the workbook's date system) rather than from 1900-01-00.</summary>
    public bool Date1904 { get; }

    public string DecimalSeparator { get; }

    public string GroupSeparator { get; }

    public DateTimeFormatInfo Dates { get; }

    /// <summary>The currency of the built-in currency formats: the culture's, or the dollar for the invariant culture.</summary>
    public string CurrencySymbol { get; }

    /// <summary>Where the currency symbol goes: 0 before the number, 1 after it, 2 before it and a space, 3 after a space.</summary>
    public int CurrencyPattern { get; }
}

/// <summary>
/// A spreadsheet number format (ECMA-376 18.8.31): up to four sections (positive; negative; zero; text) of digit
/// placeholders (<c>0 # ?</c>), thousands separators and scaling, decimals, percent, exponents, fractions, literal text,
/// colors and conditions in brackets, and date and time codes (<c>yyyy mm dd hh:mm:ss AM/PM</c>, elapsed <c>[h]</c>,
/// fractions of seconds). Codes it cannot read format as General.
/// </summary>
internal sealed class NumberFormat
{
    /// <summary>The highest serial number that is a date (9999-12-31 in the 1900 date system).</summary>
    private const double MaxSerial = 2958465.99999;

    private static readonly string[] ColorNames = ["black", "blue", "cyan", "green", "magenta", "red", "white", "yellow"];

    private readonly Section[] _sections;

    private NumberFormat(Section[] sections) => _sections = sections;

    /// <summary>The General format: numbers in up to eleven characters, text as it is.</summary>
    public static NumberFormat General { get; } = new([]);

    /// <summary>Whether numbers are shown as dates or times (by the first section).</summary>
    public bool IsDate => _sections.Length > 0 && _sections[0].IsDate;

    /// <summary>Parses a format code; one it cannot read is General.</summary>
    public static NumberFormat Parse(string code)
    {
        if (code.Length == 0 || code.Length > 1024 || code.Equals("General", StringComparison.OrdinalIgnoreCase))
        {
            return General;
        }

        try
        {
            var sections = SplitSections(code).Take(4).Select(ParseSection).ToArray();
            return new NumberFormat(sections);
        }
        catch (FormatException)
        {
            return General;
        }
    }

    /// <summary>
    /// The format code of a built-in format (ECMA-376 18.8.30), or <c>null</c> for an unknown id. The locale-dependent
    /// ones (short dates, currency) follow the culture; for the invariant culture dates are ISO 8601.
    /// </summary>
    public static string? BuiltInCode(int id, FormatSettings settings)
    {
        var symbol = "\"" + settings.CurrencySymbol.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";
        string Money(string number) => settings.CurrencyPattern switch
        {
            1 => number + symbol,
            2 => symbol + "\\ " + number,
            3 => number + "\\ " + symbol,
            _ => symbol + number,
        };

        return id switch
        {
            0 => "General",
            1 => "0",
            2 => "0.00",
            3 => "#,##0",
            4 => "#,##0.00",
            5 => Money("#,##0") + "_);(" + Money("#,##0") + ")",
            6 => Money("#,##0") + "_);[Red](" + Money("#,##0") + ")",
            7 => Money("#,##0.00") + "_);(" + Money("#,##0.00") + ")",
            8 => Money("#,##0.00") + "_);[Red](" + Money("#,##0.00") + ")",
            9 => "0%",
            10 => "0.00%",
            11 => "0.00E+00",
            12 => "# ?/?",
            13 => "# ??/??",
            14 or (>= 27 and <= 31) or (>= 34 and <= 36) or (>= 50 and <= 58) => ShortDateCode(settings),
            15 => "d-mmm-yy",
            16 => "d-mmm",
            17 => "mmm-yy",
            18 => "h:mm AM/PM",
            19 => "h:mm:ss AM/PM",
            20 or 32 => "h:mm",
            21 or 33 => "h:mm:ss",
            22 => ShortDateCode(settings) + " h:mm",
            37 => "#,##0 ;(#,##0)",
            38 => "#,##0 ;[Red](#,##0)",
            39 => "#,##0.00;(#,##0.00)",
            40 => "#,##0.00;[Red](#,##0.00)",
            41 => "_(* #,##0_);_(* (#,##0);_(* \"-\"_);_(@_)",
            42 => "_(" + Money("* #,##0") + "_);_(" + Money("* (#,##0)") + ";_(" + Money("* \"-\"") + "_);_(@_)",
            43 => "_(* #,##0.00_);_(* (#,##0.00);_(* \"-\"??_);_(@_)",
            44 => "_(" + Money("* #,##0.00") + "_);_(" + Money("* (#,##0.00)") + ";_(" + Money("* \"-\"??") + "_);_(@_)",
            45 => "mm:ss",
            46 => "[h]:mm:ss",
            47 => "mm:ss.0",
            48 => "##0.0E+0",
            49 => "@",
            _ => null,
        };
    }

    /// <summary>The culture's short date as a format code: ISO 8601 (<c>yyyy-mm-dd</c>) for the invariant culture.</summary>
    internal static string ShortDateCode(FormatSettings settings)
    {
        if (settings.Culture.Equals(CultureInfo.InvariantCulture))
        {
            return "yyyy-mm-dd";
        }

        var pattern = settings.Dates.ShortDatePattern;
        var code = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case 'M':
                    code.Append('m');
                    break;
                case 'd' or 'y':
                    code.Append(c);
                    break;
                case '/':
                    code.Append('"').Append(settings.Dates.DateSeparator.Replace("\"", string.Empty, StringComparison.Ordinal)).Append('"');
                    break;
                case '\'' or '"':
                    var end = pattern.IndexOf(c, i + 1);
                    end = end < 0 ? pattern.Length : end;
                    code.Append('"').Append(pattern.AsSpan(i + 1, end - i - 1)).Append('"');
                    i = end;
                    break;
                case '\\' when i + 1 < pattern.Length:
                    code.Append('\\').Append(pattern[++i]);
                    break;
                default:
                    code.Append('\\').Append(c);
                    break;
            }
        }

        return code.ToString();
    }

    /// <summary>Formats a number.</summary>
    public FormattedValue Format(double value, FormatSettings settings)
    {
        if (_sections.Length == 0 || (_sections.Length == 1 && _sections[0].IsText))
        {
            return new FormattedValue(FormatGeneral(value, settings), CellKind.Number);
        }

        if (!Choose(value, out var section, out var negative))
        {
            return new FormattedValue(FormatGeneral(value, settings), CellKind.Number);
        }

        if (section.IsDate)
        {
            return value < 0 || value > MaxSerial || FormatDate(section, value, settings) is not { } date
                ? new FormattedValue(FormatGeneral(value, settings), CellKind.Number)
                : new FormattedValue(date, CellKind.Date, section.Color);
        }

        var text = FormatNumber(section, Math.Abs(value), settings);
        return new FormattedValue(negative && text.Length > 0 ? "-" + text : text, CellKind.Number, section.Color);
    }

    /// <summary>Formats text: with the text section (the fourth, or a single one with <c>@</c>), else as it is.</summary>
    public FormattedValue FormatText(string text)
    {
        var section = _sections.Length >= 4 ? _sections[3] : _sections.Length >= 1 && _sections[^1].IsText ? _sections[^1] : null;
        if (section is null)
        {
            return new FormattedValue(text, CellKind.Text);
        }

        var result = new StringBuilder();
        foreach (var token in section.Tokens)
        {
            result.Append(token.Kind == TokenKind.Text ? text : token.Kind == TokenKind.Literal ? token.Text : string.Empty);
        }

        return new FormattedValue(result.ToString(), CellKind.Text, section.Color);
    }

    /// <summary>
    /// The section a number is shown with, and whether a minus sign goes before it: by the sections' conditions if they
    /// have any, else positive (and zero with two sections), negative and zero.
    /// </summary>
    private bool Choose(double value, out Section section, out bool negative)
    {
        var numeric = _sections.Length >= 4 ? 3 : _sections.Length;
        if (numeric > 1 && _sections[numeric - 1].IsText)
        {
            numeric--;
        }

        if (_sections[0].Condition is not null || (numeric > 1 && _sections[1].Condition is not null))
        {
            for (var i = 0; i < Math.Min(numeric, 2); i++)
            {
                if (_sections[i].Condition is { } condition && condition.Matches(value))
                {
                    section = _sections[i];
                    negative = value < 0 && !condition.ImpliesNegative;
                    return true;
                }
            }

            var other = numeric > 2 ? 2 : numeric == 2 && _sections[1].Condition is null ? 1 : -1;
            section = other < 0 ? _sections[0] : _sections[other];
            negative = value < 0;
            return other >= 0;
        }

        if (numeric == 1 || value > 0 || (value == 0 && numeric == 2))
        {
            section = _sections[0];
            negative = value < 0;
            return true;
        }

        section = value < 0 ? _sections[1] : _sections[2];
        negative = false;
        return true;
    }

    /// <summary>
    /// A number in the General format: up to eleven characters, in scientific notation when it is too large or too small
    /// for them (<c>1.23457E+11</c>).
    /// </summary>
    internal static string FormatGeneral(double value, FormatSettings settings)
    {
        if (value == 0 || !double.IsFinite(value))
        {
            return "0";
        }

        var abs = Math.Abs(value);
        var sign = value < 0 ? "-" : string.Empty;
        string text;
        if (abs >= 1e11 || abs < 1e-9)
        {
            text = Scientific(abs);
        }
        else
        {
            var integerDigits = abs < 1 ? 1 : (int)Math.Floor(Math.Log10(abs)) + 1;
            var decimals = Math.Clamp(10 - integerDigits, 0, 15);
            var rounded = Math.Round((decimal)abs, decimals, MidpointRounding.AwayFromZero);
            text = (abs < 1e-4 && (double)rounded != abs) || rounded >= 1e11m
                ? Scientific(abs)
                : rounded.ToString("0.###############", CultureInfo.InvariantCulture);
        }

        return sign + text.Replace(".", settings.DecimalSeparator, StringComparison.Ordinal);

        static string Scientific(double abs)
        {
            var exponent = (int)Math.Floor(Math.Log10(abs));
            var mantissa = Math.Round(abs / Math.Pow(10, exponent), 5, MidpointRounding.AwayFromZero);
            if (mantissa >= 10)
            {
                mantissa /= 10;
                exponent++;
            }

            return mantissa.ToString("0.#####", CultureInfo.InvariantCulture) + (exponent < 0 ? "E-" : "E+")
                + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        }
    }

    private static string FormatNumber(Section section, double value, FormatSettings settings)
    {
        if (section.DigitCount == 0)
        {
            // Literal text only (the zero section of accounting formats), or General with literal text around it.
            var literal = new StringBuilder();
            foreach (var token in section.Tokens)
            {
                literal.Append(token.Kind switch
                {
                    TokenKind.Literal => token.Text,
                    TokenKind.General => FormatGeneral(value, settings),
                    _ => string.Empty,
                });
            }

            return literal.ToString();
        }

        value *= Math.Pow(100, section.Percent);
        value /= Math.Pow(1000, section.Scaling);
        if (!double.IsFinite(value))
        {
            return FormatGeneral(value, settings);
        }

        return section.Fraction is not null
            ? FormatFraction(section, value, settings)
            : FormatDecimal(section, value, settings);
    }

    private static string FormatDecimal(Section section, double value, FormatSettings settings)
    {
        var decimals = section.Decimals.Count;
        var exponent = 0;
        if (section.Exponent is not null && value != 0)
        {
            var places = Math.Max(1, section.Integers.Count);
            var engineering = section.Integers.Count > 1 && section.Integers.Any(t => t.Kind == TokenKind.DigitHash);
            int ExponentOf(int magnitude) => engineering ? (int)Math.Floor(magnitude / (double)places) * places : magnitude - (places - 1);

            var magnitude = (int)Math.Floor(Math.Log10(value));
            exponent = ExponentOf(magnitude);
            if (Math.Round(value / Math.Pow(10, exponent), decimals, MidpointRounding.AwayFromZero) >= Math.Pow(10, magnitude - exponent + 1))
            {
                exponent = ExponentOf(magnitude + 1); // Rounding carried into another digit (9.995 to 10.00).
            }

            value /= Math.Pow(10, exponent);
            if (!double.IsFinite(value))
            {
                return FormatGeneral(value, settings);
            }
        }

        var (integer, fraction) = Digits(value, decimals);
        return Emit(section, integer, fraction, exponent, null, settings);
    }

    /// <summary>The integer digits (none for zero) and the decimals of a non-negative number rounded to some decimals.</summary>
    private static (string Integer, string Fraction) Digits(double value, int decimals)
    {
        string text;
        if (value < 7.9e27)
        {
            text = Math.Round((decimal)value, Math.Min(decimals, 28), MidpointRounding.AwayFromZero)
                .ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }
        else
        {
            text = WholeDigits(value) + "." + new string('0', decimals);
        }

        var point = text.IndexOf('.', StringComparison.Ordinal);
        var integer = point < 0 ? text : text[..point];
        var fraction = point < 0 ? string.Empty : text[(point + 1)..];
        fraction = fraction.PadRight(decimals, '0');
        return (integer.TrimStart('0'), fraction);
    }

    /// <summary>
    /// The digits of a whole number with its 15 significant digits followed by zeros, as spreadsheets show large numbers
    /// (not the digits of its binary value).
    /// </summary>
    private static string WholeDigits(double value)
    {
        if (value < 1e15)
        {
            return value.ToString("F0", CultureInfo.InvariantCulture);
        }

        var scientific = value.ToString("E14", CultureInfo.InvariantCulture);
        var exponent = int.Parse(scientific.AsSpan(scientific.IndexOf('E', StringComparison.Ordinal) + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var digits = scientific[0] + scientific[2..16];
        return digits + new string('0', Math.Max(0, exponent - 14));
    }

    private static string FormatFraction(Section section, double value, FormatSettings settings)
    {
        var fraction = section.Fraction!;
        var whole = section.Integers.Count > 0 ? Math.Floor(value) : 0;
        var rest = value - whole;
        var limit = fraction.FixedDenominator > 0 ? fraction.FixedDenominator : Math.Pow(10, Math.Min(fraction.Denominator.Count, 9));
        if (rest * limit > 1e15)
        {
            return FormatGeneral(value, settings);
        }

        long numerator, denominator;
        if (fraction.FixedDenominator > 0)
        {
            denominator = fraction.FixedDenominator;
            numerator = (long)Math.Round(rest * denominator, MidpointRounding.AwayFromZero);
        }
        else
        {
            var maxDenominator = (long)Math.Pow(10, Math.Min(fraction.Denominator.Count, 9)) - 1;
            (numerator, denominator) = Approximate(rest, Math.Max(1, maxDenominator));
        }

        if (section.Integers.Count > 0 && numerator == denominator)
        {
            whole++;
            numerator = 0;
        }

        var integer = whole == 0 ? string.Empty : WholeDigits(whole);
        return Emit(section, integer, string.Empty, 0, (numerator, denominator), settings);
    }

    /// <summary>The fraction closest to <paramref name="value"/> whose denominator is at most <paramref name="maxDenominator"/>.</summary>
    internal static (long Numerator, long Denominator) Approximate(double value, long maxDenominator)
    {
        long p0 = 0, q0 = 1, p1 = 1, q1 = 0;
        var r = value;
        for (var i = 0; i < 64; i++)
        {
            var a = Math.Floor(r);
            if (a > 1e15)
            {
                break;
            }

            if ((a * q1) + q0 > maxDenominator)
            {
                // The best semiconvergent, if closer than the last convergent.
                var k = (maxDenominator - q0) / q1;
                var (ps, qs) = ((p0 + (k * p1)), (q0 + (k * q1)));
                if (qs > 0 && Math.Abs(value - ((double)ps / qs)) < Math.Abs(value - ((double)p1 / q1)))
                {
                    (p1, q1) = (ps, qs);
                }

                break;
            }

            var whole = (long)a;
            (p0, q0, p1, q1) = (p1, q1, (whole * p1) + p0, (whole * q1) + q0);
            var rest = r - a;
            if (rest < 1e-12)
            {
                break;
            }

            r = 1 / rest;
        }

        return q1 == 0 ? (0, 1) : (p1, q1);
    }

    /// <summary>Writes the tokens of a number section with the digits put in their placeholders.</summary>
    private static string Emit(Section section, string integer, string fraction, int exponent, (long Numerator, long Denominator)? ratio, FormatSettings settings)
    {
        var result = new StringBuilder();
        var integerText = section.Grouping ? Grouped(section, integer, settings) : null;
        var integerIndex = 0;
        var decimalIndex = 0;
        var exponentText = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        var exponentDigits = section.ExponentDigits.Count;
        var exponentIndex = 0;
        var lastSignificant = fraction.TrimEnd('0').Length;
        var blankFraction = ratio is { Numerator: 0 } && section.Integers.Count > 0;
        if (blankFraction && integer.Length == 0)
        {
            integer = "0";
            integerText = section.Grouping ? "0" : null;
        }

        var numeratorText = ratio?.Numerator.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var denominatorText = ratio?.Denominator.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var numeratorIndex = 0;
        var denominatorIndex = 0;
        var fractionStart = result.Length;
        foreach (var token in section.Tokens)
        {
            switch (token.Zone)
            {
                case Zone.Integer:
                    if (integerText is not null)
                    {
                        if (integerIndex++ == 0)
                        {
                            result.Append(integerText);
                        }
                    }
                    else
                    {
                        result.Append(PlaceInteger(token.Kind, integer, section.Integers.Count, integerIndex++));
                    }

                    continue;
                case Zone.Decimal:
                    var d = decimalIndex++;
                    result.Append(d < lastSignificant ? fraction[d].ToString() : Placeholder(token.Kind, ' '));
                    continue;
                case Zone.Exponent:
                    var position = exponentDigits - 1 - exponentIndex++;
                    result.Append(PlaceDigits(token.Kind, exponentText, position, exponentIndex == 1, exponentDigits));
                    continue;
                case Zone.Numerator:
                    if (numeratorIndex == 0)
                    {
                        fractionStart = result.Length;
                    }

                    result.Append(PlaceDigits(token.Kind, numeratorText, section.Fraction!.Numerator.Count - 1 - numeratorIndex, numeratorIndex == 0, section.Fraction.Numerator.Count));
                    numeratorIndex++;
                    continue;
                case Zone.Denominator:
                    var k = denominatorIndex++;
                    result.Append(k < denominatorText.Length ? denominatorText[k].ToString() : Placeholder(token.Kind, ' '));
                    if (k == section.Fraction!.Denominator.Count - 1 && denominatorText.Length > k + 1)
                    {
                        result.Append(denominatorText.AsSpan(k + 1));
                    }

                    continue;
            }

            switch (token.Kind)
            {
                case TokenKind.Literal:
                    result.Append(token.Text);
                    break;
                case TokenKind.DecimalPoint:
                    result.Append(settings.DecimalSeparator);
                    break;
                case TokenKind.Exponent:
                    result.Append(token.Text[0]).Append(exponent < 0 ? "-" : token.Text[1] == '+' ? "+" : string.Empty);
                    break;
                case TokenKind.Slash when ratio is not null:
                    if (numeratorIndex == 0)
                    {
                        fractionStart = result.Length;
                    }

                    result.Append('/');
                    if (section.Fraction!.FixedDenominator > 0)
                    {
                        result.Append(denominatorText);
                    }

                    break;
            }
        }

        if (blankFraction)
        {
            // A whole number: the fraction is left blank, keeping its width.
            var end = result.Length;
            var tail = section.Fraction!.TrailingLength;
            var start = Math.Min(fractionStart, end);
            var blank = Math.Max(0, end - start - tail);
            result.Remove(start, blank).Insert(start, new string(' ', blank));
        }

        return result.ToString();
    }

    /// <summary>What an unused placeholder shows: a zero for <c>0</c>, a space for <c>?</c>, nothing for <c>#</c>.</summary>
    private static string Placeholder(TokenKind kind, char space) => kind switch
    {
        TokenKind.Digit0 => "0",
        TokenKind.DigitQuestion => space.ToString(),
        _ => string.Empty,
    };

    /// <summary>
    /// The digits for the <paramref name="index"/>-th of <paramref name="count"/> integer placeholders: the first takes
    /// all the digits that have no placeholder of their own.
    /// </summary>
    private static string PlaceInteger(TokenKind kind, string digits, int count, int index) =>
        PlaceDigits(kind, digits, count - 1 - index, index == 0, count);

    /// <summary>The digit at <paramref name="position"/> from the right, and all higher ones for the first placeholder.</summary>
    private static string PlaceDigits(TokenKind kind, string digits, int position, bool first, int count)
    {
        if (first && digits.Length > count)
        {
            return digits[..(digits.Length - count + 1)];
        }

        return position < digits.Length ? digits[digits.Length - 1 - position].ToString() : Placeholder(kind, ' ');
    }

    /// <summary>The integer part with thousands separators, padded to the placeholders.</summary>
    private static string Grouped(Section section, string integer, FormatSettings settings)
    {
        var placeholders = section.Integers;
        var length = Math.Max(integer.Length, placeholders.Count);
        var result = new StringBuilder();
        for (var position = length - 1; position >= 0; position--)
        {
            string digit;
            if (position < integer.Length)
            {
                digit = integer[integer.Length - 1 - position].ToString();
            }
            else
            {
                digit = Placeholder(placeholders[placeholders.Count - 1 - position].Kind, ' ');
            }

            result.Append(digit);
            if (position > 0 && position % 3 == 0 && digit.Length > 0)
            {
                result.Append(digit == " " ? " " : settings.GroupSeparator);
            }
        }

        return result.ToString();
    }

    private static string? FormatDate(Section section, double serial, FormatSettings settings)
    {
        var fractionDigits = section.SecondDecimals;
        var unit = 86400L * (long)Math.Pow(10, fractionDigits);
        var ticks = (long)Math.Round(serial * unit, MidpointRounding.AwayFromZero);
        var days = ticks / unit;
        var rest = ticks % unit;
        var scale = (long)Math.Pow(10, fractionDigits);
        var seconds = rest / scale;
        var subsecond = rest % scale;
        if (days > (settings.Date1904 ? 2957003 : 2958465))
        {
            return null;
        }

        int year, month, day;
        DayOfWeek weekday;
        if (settings.Date1904)
        {
            var date = new DateTime(1904, 1, 1).AddDays(days);
            (year, month, day, weekday) = (date.Year, date.Month, date.Day, date.DayOfWeek);
        }
        else if (days == 0)
        {
            (year, month, day, weekday) = (1900, 1, 0, DayOfWeek.Saturday); // Day 0, as spreadsheets show it.
        }
        else if (days == 60)
        {
            (year, month, day, weekday) = (1900, 2, 29, DayOfWeek.Wednesday); // The day 1900 never had, kept for compatibility.
        }
        else
        {
            var date = new DateTime(1899, 12, days < 60 ? 31 : 30).AddDays(days);
            (year, month, day, weekday) = (date.Year, date.Month, date.Day, date.DayOfWeek);
        }

        if (year > 9999)
        {
            return null;
        }

        var hour = (int)(seconds / 3600);
        var minute = (int)(seconds / 60 % 60);
        var second = (int)(seconds % 60);
        var dates = settings.Dates;
        var result = new StringBuilder();
        foreach (var token in section.Tokens)
        {
            var n = token.Length;
            switch (token.Kind)
            {
                case TokenKind.Literal:
                    result.Append(token.Text);
                    break;
                case TokenKind.Year:
                    result.Append(n <= 2 ? (year % 100).ToString("00", CultureInfo.InvariantCulture) : year.ToString("0000", CultureInfo.InvariantCulture));
                    break;
                case TokenKind.Month:
                    result.Append(n switch
                    {
                        1 => month.ToString(CultureInfo.InvariantCulture),
                        2 => month.ToString("00", CultureInfo.InvariantCulture),
                        3 => dates.AbbreviatedMonthNames[month - 1],
                        4 => dates.MonthNames[month - 1],
                        _ => dates.MonthNames[month - 1][..Math.Min(1, dates.MonthNames[month - 1].Length)],
                    });
                    break;
                case TokenKind.Day:
                    result.Append(n switch
                    {
                        1 => day.ToString(CultureInfo.InvariantCulture),
                        2 => day.ToString("00", CultureInfo.InvariantCulture),
                        3 => dates.AbbreviatedDayNames[(int)weekday],
                        _ => dates.DayNames[(int)weekday],
                    });
                    break;
                case TokenKind.Hour:
                    var h = section.HasAmPm ? (hour % 12 == 0 ? 12 : hour % 12) : hour;
                    result.Append(h.ToString(n >= 2 ? "00" : "0", CultureInfo.InvariantCulture));
                    break;
                case TokenKind.Minute:
                    result.Append(minute.ToString(n >= 2 ? "00" : "0", CultureInfo.InvariantCulture));
                    break;
                case TokenKind.Second:
                    result.Append(second.ToString(n >= 2 ? "00" : "0", CultureInfo.InvariantCulture));
                    break;
                case TokenKind.SecondFraction:
                    result.Append(settings.DecimalSeparator)
                        .Append((subsecond / (long)Math.Pow(10, fractionDigits - n)).ToString(new string('0', n), CultureInfo.InvariantCulture));
                    break;
                case TokenKind.ElapsedHours:
                    result.Append(((days * 24) + hour).ToString(new string('0', n), CultureInfo.InvariantCulture));
                    break;
                case TokenKind.ElapsedMinutes:
                    result.Append(((days * 1440) + (seconds / 60)).ToString(new string('0', n), CultureInfo.InvariantCulture));
                    break;
                case TokenKind.ElapsedSeconds:
                    result.Append(((days * 86400) + seconds).ToString(new string('0', n), CultureInfo.InvariantCulture));
                    break;
                case TokenKind.AmPm:
                    var am = hour < 12;
                    var designator = token.Text switch
                    {
                        "A/P" => am ? "A" : "P",
                        "a/p" => am ? "a" : "p",
                        _ => am ? (dates.AMDesignator is { Length: > 0 } a ? a : "AM") : (dates.PMDesignator is { Length: > 0 } p ? p : "PM"),
                    };
                    result.Append(token.Text == "am/pm" ? designator.ToLowerInvariant() : designator);
                    break;
            }
        }

        return result.ToString();
    }

    /// <summary>Splits a format code at the semicolons that are not quoted, escaped or in brackets.</summary>
    private static IEnumerable<string> SplitSections(string code)
    {
        var start = 0;
        for (var i = 0; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '"':
                    i = code.IndexOf('"', i + 1);
                    if (i < 0)
                    {
                        throw new FormatException("An unclosed quote.");
                    }

                    break;
                case '\\' or '_' or '*':
                    i++;
                    break;
                case '[':
                    i = code.IndexOf(']', i + 1);
                    if (i < 0)
                    {
                        throw new FormatException("An unclosed bracket.");
                    }

                    break;
                case ';':
                    yield return code[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return code[start..];
    }

    private static Section ParseSection(string code)
    {
        var tokens = new List<Token>();
        PdfColor? color = null;
        Condition? condition = null;
        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            var lower = char.ToLowerInvariant(c);
            switch (c)
            {
                case '"':
                    var end = code.IndexOf('"', i + 1);
                    tokens.Add(Token.Literal(code[(i + 1)..end]));
                    i = end + 1;
                    continue;
                case '\\':
                    if (i + 1 < code.Length)
                    {
                        tokens.Add(Token.Literal(code[i + 1].ToString()));
                    }

                    i += 2;
                    continue;
                case '_':
                    tokens.Add(Token.Literal(" "));
                    i += 2;
                    continue;
                case '*':
                    i += 2; // A fill character: repeated to the width of the cell in a spreadsheet, left out here.
                    continue;
                case '[':
                    var close = code.IndexOf(']', i + 1);
                    ParseBracket(code[(i + 1)..close], tokens, ref color, ref condition);
                    i = close + 1;
                    continue;
                case '0':
                    tokens.Add(new Token(TokenKind.Digit0));
                    break;
                case '#':
                    tokens.Add(new Token(TokenKind.DigitHash));
                    break;
                case '?':
                    tokens.Add(new Token(TokenKind.DigitQuestion));
                    break;
                case '.':
                    tokens.Add(new Token(TokenKind.DecimalPoint));
                    break;
                case ',':
                    tokens.Add(new Token(TokenKind.Comma));
                    break;
                case '%':
                    tokens.Add(new Token(TokenKind.Percent));
                    break;
                case '@':
                    tokens.Add(new Token(TokenKind.Text));
                    break;
                case '/':
                    // A fixed denominator: digits that start with 1 to 9 (# ?/16).
                    var digits = i + 1;
                    if (digits < code.Length && code[digits] is >= '1' and <= '9')
                    {
                        while (digits < code.Length && char.IsAsciiDigit(code[digits]))
                        {
                            digits++;
                        }

                        var denominator = int.Parse(code.AsSpan(i + 1, Math.Min(digits - i - 1, 9)), CultureInfo.InvariantCulture);
                        tokens.Add(new Token(TokenKind.Slash, code[i..digits], denominator));
                        i = digits;
                        continue;
                    }

                    tokens.Add(new Token(TokenKind.Slash, "/"));
                    break;
                case 'E' or 'e' when i + 1 < code.Length && code[i + 1] is '+' or '-':
                    tokens.Add(new Token(TokenKind.Exponent, code.Substring(i, 2)));
                    i += 2;
                    continue;
                default:
                    if (StartsWith(code, i, "General"))
                    {
                        tokens.Add(new Token(TokenKind.General));
                        i += 7;
                        continue;
                    }

                    if (StartsWith(code, i, "AM/PM"))
                    {
                        tokens.Add(new Token(TokenKind.AmPm, code.Substring(i, 5) == "am/pm" ? "am/pm" : "AM/PM"));
                        i += 5;
                        continue;
                    }

                    if (StartsWith(code, i, "A/P"))
                    {
                        tokens.Add(new Token(TokenKind.AmPm, code[i] == 'a' ? "a/p" : "A/P"));
                        i += 3;
                        continue;
                    }

                    if (lower is 'y' or 'm' or 'd' or 'h' or 's')
                    {
                        var run = i;
                        while (run < code.Length && char.ToLowerInvariant(code[run]) == lower)
                        {
                            run++;
                        }

                        var kind = lower switch
                        {
                            'y' => TokenKind.Year,
                            'm' => TokenKind.Month,
                            'd' => TokenKind.Day,
                            'h' => TokenKind.Hour,
                            _ => TokenKind.Second,
                        };
                        tokens.Add(new Token(kind, string.Empty, run - i));
                        i = run;
                        continue;
                    }

                    if ((lower is 'b' && i + 1 < code.Length && code[i + 1] is '1' or '2') || lower is 'g')
                    {
                        // Calendar and era codes (Buddhist, Hijri, Japanese eras): not supported, left out.
                        i += lower == 'b' ? 2 : 1;
                        continue;
                    }

                    tokens.Add(Token.Literal(c.ToString()));
                    break;
            }

            i++;
        }

        return new Section(tokens, color, condition);
    }

    private static bool StartsWith(string code, int index, string value) =>
        string.Compare(code, index, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0 && index + value.Length <= code.Length;

    /// <summary>Reads what is in brackets: a color, a condition, an elapsed time unit or a currency and locale.</summary>
    private static void ParseBracket(string content, List<Token> tokens, ref PdfColor? color, ref Condition? condition)
    {
        var lower = content.ToLowerInvariant();
        var named = Array.IndexOf(ColorNames, lower);
        if (named >= 0)
        {
            color = XlsxColors.Indexed(named == 0 ? 8 : named switch { 1 => 12, 2 => 15, 3 => 11, 4 => 14, 5 => 10, 6 => 9, _ => 13 });
        }
        else if (lower.StartsWith("color", StringComparison.Ordinal)
            && int.TryParse(lower.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index is >= 1 and <= 56)
        {
            color = XlsxColors.Indexed(index + 7);
        }
        else if (content.Length > 0 && content[0] is '<' or '>' or '=')
        {
            condition = Condition.Parse(content);
        }
        else if (lower.Length > 0 && lower.All(c => c == lower[0]) && lower[0] is 'h' or 'm' or 's')
        {
            var kind = lower[0] switch
            {
                'h' => TokenKind.ElapsedHours,
                'm' => TokenKind.ElapsedMinutes,
                _ => TokenKind.ElapsedSeconds,
            };
            tokens.Add(new Token(kind, string.Empty, Math.Min(lower.Length, 16)));
        }
        else if (content.StartsWith('$'))
        {
            // [$EUR-407]: the currency symbol, then a locale id; [$-409] and [$-F800] name only a locale.
            var dash = content.IndexOf('-', StringComparison.Ordinal);
            var symbol = dash < 0 ? content[1..] : content[1..dash];
            if (symbol.Length > 0)
            {
                tokens.Add(Token.Literal(symbol));
            }
        }
    }

    private enum TokenKind
    {
        Literal,
        Digit0,
        DigitHash,
        DigitQuestion,
        DecimalPoint,
        Comma,
        Percent,
        Exponent,
        Slash,
        Text,
        General,
        Year,
        Month,
        Day,
        Hour,
        Minute,
        Second,
        SecondFraction,
        ElapsedHours,
        ElapsedMinutes,
        ElapsedSeconds,
        AmPm,

        /// <summary>A comma that groups or scales: written as nothing.</summary>
        Ignored,
    }

    /// <summary>Where a digit placeholder is in a number.</summary>
    private enum Zone
    {
        None,
        Integer,
        Decimal,
        Exponent,
        Numerator,
        Denominator,
    }

    private sealed class Token(TokenKind kind, string text = "", int length = 0)
    {
        public TokenKind Kind { get; set; } = kind;

        public string Text { get; set; } = text;

        /// <summary>The number of letters of a date code, or the fixed denominator of a slash.</summary>
        public int Length { get; set; } = length;

        public Zone Zone { get; set; }

        public bool IsDigit => Kind is TokenKind.Digit0 or TokenKind.DigitHash or TokenKind.DigitQuestion;

        public bool IsDatePart => Kind is TokenKind.Year or TokenKind.Month or TokenKind.Day or TokenKind.Hour or TokenKind.Minute
            or TokenKind.Second or TokenKind.ElapsedHours or TokenKind.ElapsedMinutes or TokenKind.ElapsedSeconds or TokenKind.AmPm;

        public static Token Literal(string text) => new(TokenKind.Literal, text);
    }

    /// <summary>A condition in brackets: <c>[&gt;=100]</c>.</summary>
    private sealed record Condition(string Operator, double Value)
    {
        /// <summary>Whether only negative numbers can match, so that they are shown without a minus sign.</summary>
        public bool ImpliesNegative => (Operator is "<" && Value <= 0) || (Operator is "<=" && Value < 0) || (Operator is "=" && Value < 0);

        public static Condition Parse(string content)
        {
            var length = content.Length > 1 && content[1] is '=' or '>' ? 2 : 1;
            var op = content[..length];
            if (op is not ("<" or ">" or "=" or "<=" or ">=" or "<>")
                || !double.TryParse(content.AsSpan(length), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            {
                throw new FormatException("A condition that is not one.");
            }

            return new Condition(op, value);
        }

        public bool Matches(double v) => Operator switch
        {
            "<" => v < Value,
            ">" => v > Value,
            "=" => v == Value,
            "<=" => v <= Value,
            ">=" => v >= Value,
            _ => v != Value,
        };
    }

    /// <summary>The numerator and denominator placeholders of a fraction.</summary>
    private sealed class FractionPart
    {
        public List<Token> Numerator { get; } = [];

        public List<Token> Denominator { get; } = [];

        public int FixedDenominator { get; set; }

        /// <summary>The characters written after the fraction's last placeholder (kept when the fraction is blank).</summary>
        public int TrailingLength { get; set; }
    }

    /// <summary>One section of a format, its tokens classified.</summary>
    private sealed class Section
    {
        public Section(List<Token> tokens, PdfColor? color, Condition? condition)
        {
            Tokens = tokens;
            Color = color;
            Condition = condition;
            IsDate = tokens.Any(t => t.IsDatePart);
            if (IsDate)
            {
                ClassifyDate();
            }
            else
            {
                ClassifyNumber();
            }

            IsText = !IsDate && DigitCount == 0 && tokens.Any(t => t.Kind == TokenKind.Text);
        }

        public List<Token> Tokens { get; }

        public PdfColor? Color { get; }

        public Condition? Condition { get; }

        public bool IsDate { get; }

        /// <summary>Whether the section is for text (it has <c>@</c> and no digits).</summary>
        public bool IsText { get; }

        public bool HasAmPm { get; private set; }

        /// <summary>The most decimals of seconds shown (0 to 3).</summary>
        public int SecondDecimals { get; private set; }

        public int DigitCount { get; private set; }

        public List<Token> Integers { get; } = [];

        public List<Token> Decimals { get; } = [];

        public List<Token> ExponentDigits { get; } = [];

        public Token? Exponent { get; private set; }

        public FractionPart? Fraction { get; private set; }

        public bool Grouping { get; private set; }

        public int Scaling { get; private set; }

        public int Percent { get; private set; }

        private void ClassifyDate()
        {
            HasAmPm = Tokens.Any(t => t.Kind == TokenKind.AmPm);
            for (var i = 0; i < Tokens.Count; i++)
            {
                var token = Tokens[i];
                if (token.Kind == TokenKind.Month && token.Length <= 2)
                {
                    // Minutes after hours or before seconds.
                    var previous = Tokens.Take(i).LastOrDefault(t => t.IsDatePart);
                    var next = Tokens.Skip(i + 1).FirstOrDefault(t => t.IsDatePart);
                    if (previous?.Kind is TokenKind.Hour or TokenKind.ElapsedHours || next?.Kind is TokenKind.Second or TokenKind.ElapsedSeconds)
                    {
                        token.Kind = TokenKind.Minute;
                    }
                }
                else if (token.Kind == TokenKind.DecimalPoint && Tokens.Take(i).LastOrDefault(t => t.IsDatePart)?.Kind is TokenKind.Second or TokenKind.ElapsedSeconds
                    && i + 1 < Tokens.Count && Tokens[i + 1].Kind == TokenKind.Digit0)
                {
                    var n = 0;
                    while (i + 1 < Tokens.Count && Tokens[i + 1].Kind == TokenKind.Digit0)
                    {
                        Tokens.RemoveAt(i + 1);
                        n++;
                    }

                    token.Kind = TokenKind.SecondFraction;
                    token.Length = Math.Min(n, 3);
                    SecondDecimals = Math.Max(SecondDecimals, token.Length);
                }
                else if (token.Kind is not (TokenKind.Literal or TokenKind.Text or TokenKind.General) && !token.IsDatePart)
                {
                    token.Text = token.Kind switch
                    {
                        TokenKind.Digit0 => "0",
                        TokenKind.DigitHash => "#",
                        TokenKind.DigitQuestion => "?",
                        TokenKind.DecimalPoint => ".",
                        TokenKind.Comma => ",",
                        TokenKind.Percent => "%",
                        _ => token.Text,
                    };
                    token.Kind = TokenKind.Literal;
                }
            }

            foreach (var token in Tokens.Where(t => t.Kind is TokenKind.Year or TokenKind.Month or TokenKind.Day))
            {
                token.Length = Math.Min(token.Length, token.Kind == TokenKind.Month ? 5 : 4);
            }
        }

        private void ClassifyNumber()
        {
            var digits = Tokens.Where(t => t.IsDigit).ToList();
            DigitCount = digits.Count;
            var slash = Tokens.FindIndex(t => t.Kind == TokenKind.Slash);
            var point = Tokens.FindIndex(t => t.Kind == TokenKind.DecimalPoint);
            var exponent = Tokens.FindIndex(t => t.Kind == TokenKind.Exponent);

            // A fraction: digits right before the slash (the numerator) and after it (the denominator, or a number).
            if (slash > 0 && Tokens[slash - 1].IsDigit && point < 0 && exponent < 0
                && (Tokens[slash].Length > 0 || (slash + 1 < Tokens.Count && Tokens[slash + 1].IsDigit)))
            {
                var fraction = new FractionPart { FixedDenominator = Tokens[slash].Length };
                var n = slash - 1;
                while (n >= 0 && Tokens[n].IsDigit)
                {
                    n--;
                }

                for (var k = n + 1; k < slash; k++)
                {
                    Tokens[k].Zone = Zone.Numerator;
                    fraction.Numerator.Add(Tokens[k]);
                }

                var d = slash + 1;
                while (d < Tokens.Count && Tokens[d].IsDigit && fraction.FixedDenominator == 0)
                {
                    Tokens[d].Zone = Zone.Denominator;
                    fraction.Denominator.Add(Tokens[d]);
                    d++;
                }

                fraction.TrailingLength = Tokens.Skip(d).Sum(t => t.Kind == TokenKind.Literal ? t.Text.Length : 0);
                for (var k = 0; k <= n; k++)
                {
                    if (Tokens[k].IsDigit)
                    {
                        Tokens[k].Zone = Zone.Integer;
                        Integers.Add(Tokens[k]);
                    }
                }

                Fraction = fraction;
            }
            else
            {
                var zone = Zone.Integer;
                foreach (var token in Tokens)
                {
                    if (token.Kind == TokenKind.DecimalPoint && zone == Zone.Integer)
                    {
                        zone = Zone.Decimal;
                    }
                    else if (token.Kind == TokenKind.Exponent)
                    {
                        zone = Zone.Exponent;
                        Exponent ??= token;
                    }
                    else if (token.IsDigit)
                    {
                        token.Zone = zone;
                        (zone == Zone.Integer ? Integers : zone == Zone.Decimal ? Decimals : ExponentDigits).Add(token);
                    }
                    else if (token.Kind == TokenKind.DecimalPoint)
                    {
                        token.Kind = TokenKind.Literal;
                        token.Text = ".";
                    }
                }

                if (Exponent is not null && ExponentDigits.Count == 0)
                {
                    // E+ without digits is text.
                    Exponent.Kind = TokenKind.Literal;
                    Exponent = null;
                }
            }

            // Only the fraction's slash and the first exponent are what they are; others are text.
            for (var i = 0; i < Tokens.Count; i++)
            {
                if ((Tokens[i].Kind == TokenKind.Slash && (Fraction is null || i != slash)) || (Tokens[i].Kind == TokenKind.Exponent && Tokens[i] != Exponent))
                {
                    Tokens[i].Kind = TokenKind.Literal;
                }
            }

            // Commas: between integer digits they group thousands; after the last one they divide by a thousand each.
            var lastInteger = Integers.Count > 0 ? Tokens.IndexOf(Integers[^1]) : -1;
            for (var i = 0; i < Tokens.Count; i++)
            {
                var token = Tokens[i];
                if (token.Kind == TokenKind.Percent)
                {
                    Percent = Math.Min(Percent + 1, 4);
                    token.Kind = TokenKind.Literal;
                    token.Text = "%";
                }
                else if (token.Kind == TokenKind.Comma)
                {
                    var before = Tokens.Take(i).Any(t => t.IsDigit);
                    if (before && i < lastInteger)
                    {
                        Grouping = true;
                        token.Kind = TokenKind.Ignored;
                    }
                    else if (before && Tokens.Skip(i + 1).All(t => t.Zone is not (Zone.Integer or Zone.Numerator)))
                    {
                        Scaling = Math.Min(Scaling + 1, 8);
                        token.Kind = TokenKind.Ignored;
                    }
                    else
                    {
                        token.Kind = TokenKind.Literal;
                        token.Text = ",";
                    }
                }
            }
        }
    }
}

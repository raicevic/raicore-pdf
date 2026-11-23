using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Charts;

/// <summary>
/// Formats the numbers of chart labels with Excel number format codes, as far as charts need them: General, digit
/// placeholders (<c>0</c>, <c>#</c>, <c>?</c>), thousands separators and scaling, percentages, scientific notation,
/// literal text and currency symbols, sections for positive, negative and zero values, and dates and times. Colors,
/// conditions and fractions are ignored. Output is invariant (period and comma), as files store it.
/// </summary>
internal static class ChartNumberFormat
{
    /// <summary>Formats a number with a format code (<c>null</c> or empty for General).</summary>
    /// <param name="value">The number.</param>
    /// <param name="code">The format code.</param>
    /// <param name="date1904">Whether dates count from 1904 rather than 1900.</param>
    public static string Format(double value, string? code, bool date1904 = false)
    {
        if (!double.IsFinite(value))
        {
            return string.Empty;
        }

        if (code is null || code.Length == 0 || code.Equals("General", StringComparison.OrdinalIgnoreCase) || code.Length > 255)
        {
            return General(value);
        }

        var sections = Split(code);
        string section;
        var negativeSection = false;
        if (value < 0 && sections.Count > 1)
        {
            section = sections[1];
            negativeSection = true;
        }
        else if (value == 0 && sections.Count > 2)
        {
            section = sections[2];
        }
        else
        {
            section = sections[0];
        }

        if (section.Equals("General", StringComparison.OrdinalIgnoreCase))
        {
            return negativeSection ? General(Math.Abs(value)) : General(value);
        }

        var tokens = Tokenize(section);
        if (tokens.Any(t => t.Kind == TokenKind.Date))
        {
            return FormatDate(value, tokens, date1904);
        }

        return FormatNumber(negativeSection ? Math.Abs(value) : value, tokens, sections.Count > 1 && value < 0);
    }

    /// <summary>
    /// The General format: up to 10 significant digits without trailing zeros, scientific notation for very large
    /// and very small numbers.
    /// </summary>
    public static string General(double value)
    {
        if (value == 0 || !double.IsFinite(value))
        {
            return "0";
        }

        var magnitude = Math.Abs(value);
        if (magnitude >= 1e11 || magnitude < 1e-9)
        {
            var text = value.ToString("0.#####E+00", CultureInfo.InvariantCulture);
            return text;
        }

        var digits = Math.Clamp(9 - (int)Math.Floor(Math.Log10(magnitude)), 0, 15);
        return Math.Round(value, digits).ToString("0.###############", CultureInfo.InvariantCulture);
    }

    /// <summary>The sections of a format code (separated by semicolons outside quotes and brackets).</summary>
    private static List<string> Split(string code)
    {
        var sections = new List<string>();
        var start = 0;
        var quoted = false;
        var bracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '\\' && !quoted)
            {
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '[')
            {
                bracket = true;
            }
            else if (!quoted && c == ']')
            {
                bracket = false;
            }
            else if (!quoted && !bracket && c == ';')
            {
                sections.Add(code[start..i]);
                start = i + 1;
            }
        }

        sections.Add(code[start..]);
        return sections;
    }

    private enum TokenKind
    {
        Literal,
        Digit,
        Point,
        Comma,
        Percent,
        Exponent,
        Date,
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    /// <summary>Splits a section into literals, digit placeholders, separators and date parts.</summary>
    private static List<Token> Tokenize(string section)
    {
        var tokens = new List<Token>();
        for (var i = 0; i < section.Length; i++)
        {
            var c = section[i];
            switch (c)
            {
                case '"':
                {
                    var end = section.IndexOf('"', i + 1);
                    end = end < 0 ? section.Length : end;
                    tokens.Add(new Token(TokenKind.Literal, section[(i + 1)..end]));
                    i = end;
                    break;
                }

                case '\\' when i + 1 < section.Length:
                    tokens.Add(new Token(TokenKind.Literal, section[i + 1].ToString()));
                    i++;
                    break;
                case '_' when i + 1 < section.Length:
                    tokens.Add(new Token(TokenKind.Literal, " "));
                    i++;
                    break;
                case '*' when i + 1 < section.Length:
                    i++;
                    break;
                case '[':
                {
                    var end = section.IndexOf(']', i + 1);
                    end = end < 0 ? section.Length : end;
                    var content = section[(i + 1)..end];

                    // A currency and locale (such as [$EUR-407] with the euro sign) shows its symbol; colors, conditions and locales alone show nothing.
                    if (content.StartsWith('$'))
                    {
                        var dash = content.IndexOf('-', StringComparison.Ordinal);
                        tokens.Add(new Token(TokenKind.Literal, dash < 0 ? content[1..] : content[1..dash]));
                    }

                    i = end;
                    break;
                }

                case '0' or '#' or '?':
                    tokens.Add(new Token(TokenKind.Digit, c.ToString()));
                    break;
                case '.':
                    tokens.Add(new Token(TokenKind.Point, "."));
                    break;
                case ',':
                    tokens.Add(new Token(TokenKind.Comma, ","));
                    break;
                case '%':
                    tokens.Add(new Token(TokenKind.Percent, "%"));
                    break;
                case 'E' or 'e' when i + 1 < section.Length && section[i + 1] is '+' or '-':
                    tokens.Add(new Token(TokenKind.Exponent, section.Substring(i, 2)));
                    i++;
                    break;
                case 'y' or 'Y' or 'm' or 'M' or 'd' or 'D' or 'h' or 'H' or 's' or 'S':
                {
                    var end = i;
                    while (end < section.Length && char.ToLowerInvariant(section[end]) == char.ToLowerInvariant(c))
                    {
                        end++;
                    }

                    tokens.Add(new Token(TokenKind.Date, section[i..end].ToLowerInvariant()));
                    i = end - 1;
                    break;
                }

                case 'A' or 'a' when section.AsSpan(i).StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase):
                    tokens.Add(new Token(TokenKind.Date, "am/pm"));
                    i += 4;
                    break;
                case '@':
                    break;
                default:
                    tokens.Add(new Token(TokenKind.Literal, c.ToString()));
                    break;
            }
        }

        return tokens;
    }

    /// <summary>Formats a number with the tokens of a numeric section.</summary>
    private static string FormatNumber(double value, List<Token> tokens, bool signInSection)
    {
        var digitIndexes = tokens.Select((t, i) => (t, i)).Where(p => p.t.Kind == TokenKind.Digit).Select(p => p.i).ToList();
        var literalsOnly = digitIndexes.Count == 0;
        var percent = tokens.Count(t => t.Kind == TokenKind.Percent);
        value *= Math.Pow(100, Math.Min(percent, 3));

        // Commas right after the last digit placeholder scale by 1000 each; a comma between placeholders groups thousands.
        var lastDigit = digitIndexes.Count > 0 ? digitIndexes[^1] : -1;
        var firstDigit = digitIndexes.Count > 0 ? digitIndexes[0] : -1;
        var scale = 0;
        for (var i = lastDigit + 1; i < tokens.Count && tokens[i].Kind == TokenKind.Comma; i++)
        {
            scale++;
        }

        value /= Math.Pow(1000, scale);
        var grouping = tokens.Where((t, i) => t.Kind == TokenKind.Comma && i > firstDigit && i < lastDigit).Any();
        var pointIndex = tokens.FindIndex(t => t.Kind == TokenKind.Point);
        var exponentIndex = tokens.FindIndex(t => t.Kind == TokenKind.Exponent);
        var mantissaEnd = exponentIndex >= 0 ? exponentIndex : tokens.Count;
        var decimals = pointIndex >= 0 ? tokens.Take(mantissaEnd).Skip(pointIndex + 1).Count(t => t.Kind == TokenKind.Digit) : 0;
        var requiredDecimals = pointIndex >= 0 ? tokens.Take(mantissaEnd).Skip(pointIndex + 1).Count(t => t.Kind == TokenKind.Digit && t.Text == "0") : 0;
        var integerZeros = tokens.Take(pointIndex >= 0 ? pointIndex : mantissaEnd).Count(t => t.Kind == TokenKind.Digit && t.Text == "0");

        string number;
        if (literalsOnly)
        {
            number = string.Empty;
        }
        else if (exponentIndex >= 0)
        {
            var exponentDigits = Math.Max(1, tokens.Skip(exponentIndex + 1).Count(t => t.Kind == TokenKind.Digit));
            var exponent = value == 0 ? 0 : (int)Math.Floor(Math.Log10(Math.Abs(value)));
            var mantissa = value / Math.Pow(10, exponent);
            if (Math.Round(Math.Abs(mantissa), decimals) >= 10)
            {
                exponent++;
                mantissa /= 10;
            }

            number = Digits(Math.Abs(mantissa), decimals, requiredDecimals, Math.Max(integerZeros, 1), grouping: false)
                + (tokens[exponentIndex].Text[1] == '+' || exponent < 0 ? tokens[exponentIndex].Text[0] + (exponent < 0 ? "-" : "+") : tokens[exponentIndex].Text[0].ToString())
                + Math.Abs(exponent).ToString(new string('0', exponentDigits), CultureInfo.InvariantCulture);
        }
        else
        {
            number = Digits(Math.Abs(value), decimals, requiredDecimals, integerZeros, grouping);
        }

        // Literals before the first placeholder, the number, then literals after the last one.
        var text = new StringBuilder();
        var negative = value < 0 && !signInSection && Math.Round(Math.Abs(value), decimals) != 0;
        if (negative)
        {
            text.Append('-');
        }

        var numberWritten = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind is TokenKind.Literal)
            {
                text.Append(token.Text);
            }
            else if (token.Kind == TokenKind.Percent)
            {
                text.Append('%');
            }
            else if (token.Kind is TokenKind.Digit or TokenKind.Point or TokenKind.Comma or TokenKind.Exponent && !numberWritten && !literalsOnly)
            {
                if (i >= firstDigit || token.Kind == TokenKind.Point)
                {
                    text.Append(number);
                    numberWritten = true;
                }
            }
        }

        if (!numberWritten && !literalsOnly)
        {
            text.Append(number);
        }

        return text.ToString();
    }

    /// <summary>The digits of a non-negative number: rounded to its decimals, optional trailing ones dropped, grouped.</summary>
    private static string Digits(double value, int decimals, int requiredDecimals, int integerZeros, bool grouping)
    {
        decimals = Math.Clamp(decimals, 0, 15);
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        var text = rounded.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var point = text.IndexOf('.', StringComparison.Ordinal);
        var integer = point < 0 ? text : text[..point];
        var fraction = point < 0 ? string.Empty : text[(point + 1)..];
        while (fraction.Length > requiredDecimals && fraction.EndsWith('0'))
        {
            fraction = fraction[..^1];
        }

        if (integer == "0" && integerZeros == 0)
        {
            integer = string.Empty;
        }
        else if (integer.Length < integerZeros)
        {
            integer = integer.PadLeft(integerZeros, '0');
        }

        if (grouping && integer.Length > 3)
        {
            var grouped = new StringBuilder();
            for (var i = 0; i < integer.Length; i++)
            {
                if (i > 0 && (integer.Length - i) % 3 == 0)
                {
                    grouped.Append(',');
                }

                grouped.Append(integer[i]);
            }

            integer = grouped.ToString();
        }

        // Excel keeps the point of optional decimals that are all dropped ("0.##" shows 5 as "5.").
        return fraction.Length > 0 || decimals > 0 ? integer + "." + fraction : integer;
    }

    private static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames;
    private static readonly string[] DayNames = CultureInfo.InvariantCulture.DateTimeFormat.DayNames;

    /// <summary>A date serial (days since 1900 or 1904, with Excel's 29 February 1900) as a date, or <c>null</c>.</summary>
    internal static DateTime? Date(double serial, bool date1904)
    {
        if (serial < 0 || serial > 2958465)
        {
            return null;
        }

        var start = date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 31);
        var days = Math.Floor(serial);
        if (!date1904 && days >= 60)
        {
            // Excel counts a 29 February 1900 that did not exist.
            days--;
        }

        var seconds = Math.Round((serial - Math.Floor(serial)) * 86400);
        return start.AddDays(days).AddSeconds(seconds);
    }

    /// <summary>Formats a date serial with the tokens of a date section.</summary>
    private static string FormatDate(double value, List<Token> tokens, bool date1904)
    {
        if (Date(value, date1904) is not { } date)
        {
            return General(value);
        }

        var twelveHour = tokens.Any(t => t.Text == "am/pm");
        var text = new StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != TokenKind.Date)
            {
                text.Append(token.Kind == TokenKind.Percent ? "%" : token.Text);
                continue;
            }

            var part = token.Text;

            // "m" after an hour or before seconds is minutes.
            var minutes = part is "m" or "mm"
                && (tokens.Take(i).LastOrDefault(t => t.Kind == TokenKind.Date).Text is { } previous && previous.StartsWith('h')
                    || tokens.Skip(i + 1).FirstOrDefault(t => t.Kind == TokenKind.Date).Text is { } next && next.StartsWith('s'));
            text.Append(part switch
            {
                "yy" or "y" => (date.Year % 100).ToString("00", CultureInfo.InvariantCulture),
                _ when part.StartsWith('y') => date.Year.ToString(CultureInfo.InvariantCulture),
                "m" when minutes => date.Minute.ToString(CultureInfo.InvariantCulture),
                "mm" when minutes => date.Minute.ToString("00", CultureInfo.InvariantCulture),
                "m" => date.Month.ToString(CultureInfo.InvariantCulture),
                "mm" => date.Month.ToString("00", CultureInfo.InvariantCulture),
                "mmm" => MonthNames[date.Month - 1][..3],
                "mmmmm" => MonthNames[date.Month - 1][..1],
                _ when part.StartsWith('m') => MonthNames[date.Month - 1],
                "d" => date.Day.ToString(CultureInfo.InvariantCulture),
                "dd" => date.Day.ToString("00", CultureInfo.InvariantCulture),
                "ddd" => DayNames[(int)date.DayOfWeek][..3],
                _ when part.StartsWith('d') => DayNames[(int)date.DayOfWeek],
                "h" => (twelveHour ? ((date.Hour + 11) % 12) + 1 : date.Hour).ToString(CultureInfo.InvariantCulture),
                _ when part.StartsWith('h') => (twelveHour ? ((date.Hour + 11) % 12) + 1 : date.Hour).ToString("00", CultureInfo.InvariantCulture),
                "s" => date.Second.ToString(CultureInfo.InvariantCulture),
                _ when part.StartsWith('s') => date.Second.ToString("00", CultureInfo.InvariantCulture),
                "am/pm" => date.Hour < 12 ? "AM" : "PM",
                _ => string.Empty,
            });
        }

        return text.ToString();
    }
}

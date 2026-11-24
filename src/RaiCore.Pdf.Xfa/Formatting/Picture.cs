using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Xfa.Formatting;

/// <summary>
/// Formats canonical values with XFA picture clauses for display (XFA 3.3, "Picture Clause Specification"): numbers
/// (<c>num{}</c>, with digits, signs, grouping, radix, currency, percent and literals), dates (<c>date{}</c>), times
/// (<c>time{}</c>), date-times (<c>date{} time{}</c>), text (<c>text{}</c>), and the <c>null{}</c> and <c>zero{}</c>
/// alternatives, each in the clause's locale (<c>num(fr_FR){}</c>) or the field's. Alternatives are separated by
/// <c>|</c>: the first that fits the value is used. A value no alternative fits is shown as it is.
/// </summary>
internal static partial class Picture
{
    /// <summary>The longest picture clause followed.</summary>
    private const int MaxLength = 4096;

    /// <summary>
    /// Formats a value; <paramref name="kind"/> is the value's type (<c>text</c>, <c>integer</c>, <c>decimal</c>,
    /// <c>float</c>, <c>date</c>, <c>time</c>, <c>dateTime</c>), used for clauses without a category.
    /// </summary>
    /// <returns>The formatted text, or <c>null</c> when no alternative fits the value.</returns>
    public static string? Format(string picture, string value, string kind, XfaLocale locale, LocaleSet locales)
    {
        if (picture.Length > MaxLength)
        {
            return null;
        }

        foreach (var alternative in Split(picture, '|'))
        {
            var sections = Sections(alternative);
            if (sections.Count == 0)
            {
                continue;
            }

            var categories = sections.Where(s => s.Category is not null).Select(s => s.Category!).ToList();
            if (categories.Count == 0)
            {
                // A bare pattern: the category of the value's type.
                var category = kind switch
                {
                    "date" => "date",
                    "time" => "time",
                    "integer" or "decimal" or "float" => "num",
                    _ => "text",
                };
                sections = [new Section(category, null, alternative, null)];
                categories = [category];
            }

            if (categories[0] == "null")
            {
                if (value.Length == 0)
                {
                    return Literal(sections[0].Pattern);
                }

                continue;
            }

            if (value.Length == 0)
            {
                continue;
            }

            if (categories[0] == "zero")
            {
                if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var zero) && zero == 0)
                {
                    return Literal(sections[0].Pattern);
                }

                continue;
            }

            var result = FormatSections(sections, value, locale, locales);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\G(num|date|time|datetime|text|null|zero)(\.[a-z]+)?(\([A-Za-z_\-]*\))?\{")]
    private static partial System.Text.RegularExpressions.Regex Clause();

    private sealed record Section(string? Category, string? Locale, string Pattern, string? Subcategory);

    private static string? FormatSections(List<Section> sections, string value, XfaLocale locale, LocaleSet locales)
    {
        var output = new StringBuilder();
        string datePart = value, timePart = value;
        var t = value.IndexOf('T', StringComparison.Ordinal);
        if (t > 0 && sections.Any(s => s.Category == "date") && sections.Any(s => s.Category == "time"))
        {
            (datePart, timePart) = (value[..t], value[(t + 1)..]);
        }

        foreach (var section in sections)
        {
            var sectionLocale = section.Locale is { } name ? locales.Get(name) : locale;
            string? text = section.Category switch
            {
                null => Literal(section.Pattern),
                "num" => Number(Resolve(section, sectionLocale), value, sectionLocale),
                "date" => Date(Resolve(section, sectionLocale), datePart, sectionLocale),
                "time" => Time(Resolve(section, sectionLocale), timePart, sectionLocale),
                "datetime" => Date(Resolve(section, sectionLocale), datePart, sectionLocale),
                "text" => Text(section.Pattern, value),
                _ => null,
            };
            if (text is null)
            {
                return null;
            }

            output.Append(text);
        }

        return output.ToString();
    }

    /// <summary>A section's pattern: its own, or the locale's for an empty clause or a subcategory (<c>date.long{}</c>).</summary>
    private static string Resolve(Section section, XfaLocale locale)
    {
        if (section.Pattern.Trim().Length > 0)
        {
            return section.Pattern;
        }

        return section.Category switch
        {
            "date" or "datetime" => locale.DatePatterns.GetValueOrDefault(Size(section.Subcategory)) ?? locale.DatePatterns["med"],
            "time" => locale.TimePatterns.GetValueOrDefault(Size(section.Subcategory)) ?? locale.TimePatterns["med"],
            _ => section.Subcategory switch
            {
                "currency" => locale.NumberPatterns["currency"].Split('|')[0],
                "percent" => locale.NumberPatterns["percent"],
                "integer" => "z,zzz,zzz,zzz,zzz,zz9",
                _ => locale.NumberPatterns["numeric"],
            },
        };

        static string Size(string? subcategory) => subcategory switch
        {
            "short" => "short",
            "long" => "long",
            "full" => "full",
            _ => "med",
        };
    }

    /// <summary>Splits on a separator outside quotes and braces.</summary>
    private static List<string> Split(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '{')
            {
                depth++;
            }
            else if (!quoted && c == '}')
            {
                depth = Math.Max(depth - 1, 0);
            }
            else if (!quoted && depth == 0 && c == separator)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>An alternative's sections: <c>category(locale){pattern}</c> clauses and the literal text between them.</summary>
    private static List<Section> Sections(string alternative)
    {
        var sections = new List<Section>();
        var i = 0;
        var literal = new StringBuilder();
        while (i < alternative.Length)
        {
            var match = Clause().Match(alternative, i);
            if (match.Success)
            {
                if (literal.Length > 0)
                {
                    sections.Add(new Section(null, null, literal.ToString(), null));
                    literal.Clear();
                }

                var open = i + match.Length;
                var close = Close(alternative, open);
                var locale = match.Groups[3].Success ? match.Groups[3].Value.Trim('(', ')') : null;
                sections.Add(new Section(match.Groups[1].Value, string.IsNullOrEmpty(locale) ? null : locale, alternative[open..close], match.Groups[2].Success ? match.Groups[2].Value[1..] : null));
                i = Math.Min(close + 1, alternative.Length);
                continue;
            }

            literal.Append(alternative[i]);
            i++;
        }

        if (literal.Length > 0)
        {
            sections.Add(new Section(null, null, literal.ToString(), null));
        }

        return sections.All(s => s.Category is null) ? [] : sections;
    }

    /// <summary>The index of the brace closing a clause opened before <paramref name="start"/>.</summary>
    private static int Close(string text, int start)
    {
        var quoted = false;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && text[i] == '}')
            {
                return i;
            }
        }

        return text.Length;
    }

    /// <summary>Literal text: quoted text without its quotes (<c>''</c> is a quote).</summary>
    private static string Literal(string pattern)
    {
        var output = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\'')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '\'')
                {
                    output.Append('\'');
                    i++;
                }

                continue;
            }

            output.Append(pattern[i]);
        }

        return output.ToString();
    }

    /// <summary>A number formatted with a numeric pattern.</summary>
    internal static string? Number(string pattern, string value, XfaLocale locale)
    {
        if (!decimal.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        // The pattern's integer and fraction parts, split at the radix ('.', 'V' or the implied 'v').
        var radix = -1;
        var quoted = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && pattern[i] is '.' or 'v' or 'V')
            {
                radix = i;
                break;
            }
        }

        var integerPattern = radix < 0 ? pattern : pattern[..radix];
        var fractionPattern = radix < 0 ? string.Empty : pattern[(radix + 1)..];
        var fractionDigits = Digits(fractionPattern);
        var negative = number < 0;
        number = Math.Round(Math.Abs(number), Math.Min(fractionDigits, 28), MidpointRounding.AwayFromZero);
        negative &= number != 0;
        var digits = number.ToString("F" + Math.Min(fractionDigits, 28).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var dot = digits.IndexOf('.', StringComparison.Ordinal);
        var integerDigits = dot < 0 ? digits : digits[..dot];
        var fraction = dot < 0 ? string.Empty : digits[(dot + 1)..];
        if (integerDigits == "0")
        {
            integerDigits = string.Empty;
        }

        integerPattern = Widen(integerPattern, integerDigits.Length);
        var hasSign = pattern.IndexOfAny(['s', 'S', '(', 'C', 'c', 'D', 'd']) >= 0;
        var integer = IntegerPart(integerPattern, integerDigits, locale, negative);
        if (integer is null)
        {
            return null;
        }

        var output = new StringBuilder(integer);
        if (radix >= 0)
        {
            var fractionText = FractionPart(fractionPattern, fraction, locale, negative);
            if (pattern[radix] != 'v' && (fractionText.Length > 0 || fractionPattern.Contains('9', StringComparison.Ordinal)))
            {
                output.Append(locale.Decimal);
            }

            output.Append(fractionText);
        }

        if (negative && !hasSign)
        {
            output.Insert(0, locale.Minus);
        }

        return output.ToString();
    }

    /// <summary>
    /// A grouped integer pattern with room for more digits than it has (<c>z,zz9</c> for 1234567 is
    /// <c>z,zzz,zz9</c>): the digits it lacks are added in groups of three before its first digit.
    /// </summary>
    private static string Widen(string pattern, int digits)
    {
        var have = Digits(pattern);
        var first = pattern.IndexOfAny(['9', 'z', 'Z', '8']);
        if (digits <= have || first < 0 || !pattern.Contains(',', StringComparison.Ordinal) || pattern.Contains('\'', StringComparison.Ordinal))
        {
            return pattern;
        }

        var last = pattern.LastIndexOfAny(['9', 'z', 'Z', '8']);
        var body = new StringBuilder();
        for (var i = digits - 1; i >= 0; i--)
        {
            body.Append(i < have ? pattern[last - CountBack(pattern, last, i)] : 'z');
            if (i > 0 && i % 3 == 0)
            {
                body.Append(',');
            }
        }

        return pattern[..first] + body + pattern[(last + 1)..];
    }

    /// <summary>How far before <paramref name="last"/> the digit symbol <paramref name="index"/> digits from the right is.</summary>
    private static int CountBack(string pattern, int last, int index)
    {
        var seen = 0;
        for (var i = last; i >= 0; i--)
        {
            if (pattern[i] is '9' or 'z' or 'Z' or '8')
            {
                if (seen == index)
                {
                    return last - i;
                }

                seen++;
            }
        }

        return 0;
    }

    private static int Digits(string pattern)
    {
        var count = 0;
        var quoted = false;
        foreach (var c in pattern)
        {
            if (c == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '9' or 'z' or 'Z' or '8')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The integer part: digits right-aligned into the pattern's digit symbols, grouping only between digits.</summary>
    private static string? IntegerPart(string pattern, string digits, XfaLocale locale, bool negative)
    {
        var symbols = Digits(pattern);
        var extra = Math.Max(digits.Length - symbols, 0);
        var padded = extra > 0 ? digits[extra..] : digits.PadLeft(symbols, '\0');
        var output = new StringBuilder();
        var digit = 0;
        var emitted = false;
        var quoted = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\'')
            {
                if (quoted && i + 1 < pattern.Length && pattern[i + 1] == '\'')
                {
                    output.Append('\'');
                    i++;
                    continue;
                }

                quoted = !quoted;
                continue;
            }

            if (quoted)
            {
                output.Append(c);
                continue;
            }

            switch (c)
            {
                case '9' or 'z' or 'Z' or '8':
                {
                    if (digit == 0 && extra > 0)
                    {
                        output.Append(digits.AsSpan(0, extra));
                        emitted = true;
                    }

                    var d = padded[digit++];
                    if (d != '\0')
                    {
                        output.Append(d);
                        emitted = true;
                    }
                    else if (c == '9')
                    {
                        output.Append('0');
                        emitted = true;
                    }
                    else if (c == 'Z')
                    {
                        output.Append(' ');
                    }

                    break;
                }

                case ',':
                    if (emitted)
                    {
                        output.Append(locale.Grouping);
                    }

                    break;
                case 's':
                    output.Append(negative ? locale.Minus : string.Empty);
                    break;
                case 'S':
                    output.Append(negative ? locale.Minus : " ");
                    break;
                case '(':
                    output.Append(negative ? "(" : " ");
                    break;
                case ')':
                    output.Append(negative ? ")" : " ");
                    break;
                case '$':
                    output.Append(locale.CurrencySymbol);
                    break;
                case '%':
                    output.Append(locale.Percent);
                    break;
                case 'E' or 'e':
                    break;
                default:
                    output.Append(c);
                    break;
            }
        }

        return output.ToString();
    }

    /// <summary>The fraction part: digits left-aligned; <c>z</c> drops trailing zeros.</summary>
    private static string FractionPart(string pattern, string digits, XfaLocale locale, bool negative)
    {
        var output = new StringBuilder();
        var digit = 0;
        var quoted = false;
        var lastSignificant = digits.TrimEnd('0').Length;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\'')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted)
            {
                output.Append(c);
                continue;
            }

            switch (c)
            {
                case '9' or 'z' or 'Z' or '8':
                {
                    var d = digit < digits.Length ? digits[digit] : '0';
                    if (c == '9' || digit < lastSignificant)
                    {
                        output.Append(d);
                    }
                    else if (c == 'Z')
                    {
                        output.Append(' ');
                    }

                    digit++;
                    break;
                }

                case 's':
                    output.Append(negative ? locale.Minus : string.Empty);
                    break;
                case 'S':
                    output.Append(negative ? locale.Minus : " ");
                    break;
                case ')':
                    output.Append(negative ? ")" : " ");
                    break;
                case 'C' when i + 1 < pattern.Length && pattern[i + 1] == 'R':
                    output.Append(negative ? "CR" : "  ");
                    i++;
                    break;
                case 'D' when i + 1 < pattern.Length && pattern[i + 1] == 'B':
                    output.Append(negative ? "DB" : "  ");
                    i++;
                    break;
                case '$':
                    output.Append(locale.CurrencySymbol);
                    break;
                case '%':
                    output.Append(locale.Percent);
                    break;
                default:
                    output.Append(c);
                    break;
            }
        }

        return output.ToString();
    }

    /// <summary>Parses a canonical date: <c>YYYY-MM-DD</c>, <c>YYYYMMDD</c>, <c>YYYY-MM</c> or <c>YYYY</c>.</summary>
    internal static DateTime? ParseDate(string value)
    {
        value = value.Trim();
        string[] formats = ["yyyy-MM-dd", "yyyyMMdd", "yyyy-MM", "yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:sszzz"];
        return DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date) ? date : null;
    }

    /// <summary>A date formatted with a date pattern.</summary>
    internal static string? Date(string pattern, string value, XfaLocale locale)
    {
        if (ParseDate(value) is not { } date)
        {
            return null;
        }

        return Symbols(pattern, (symbol, count) => symbol switch
        {
            'D' => count >= 2 ? date.Day.ToString("00", CultureInfo.InvariantCulture) : date.Day.ToString(CultureInfo.InvariantCulture),
            'J' => count >= 3 ? date.DayOfYear.ToString("000", CultureInfo.InvariantCulture) : date.DayOfYear.ToString(CultureInfo.InvariantCulture),
            'M' => count switch
            {
                1 => date.Month.ToString(CultureInfo.InvariantCulture),
                2 => date.Month.ToString("00", CultureInfo.InvariantCulture),
                3 => locale.MonthAbbreviations[date.Month - 1],
                _ => locale.Months[date.Month - 1],
            },
            'E' => count switch
            {
                1 => ((int)date.DayOfWeek + 1).ToString(CultureInfo.InvariantCulture),
                3 => locale.DayAbbreviations[(int)date.DayOfWeek],
                _ => locale.Days[(int)date.DayOfWeek],
            },
            'Y' => count >= 4 ? date.Year.ToString("0000", CultureInfo.InvariantCulture) : (date.Year % 100).ToString("00", CultureInfo.InvariantCulture),
            'G' => locale.Eras[date.Year > 0 ? 1 : 0],
            'w' => (((date.Day - 1 + (int)new DateTime(date.Year, date.Month, 1).DayOfWeek) / 7) + 1).ToString(CultureInfo.InvariantCulture),
            'W' => ISOWeek.GetWeekOfYear(date).ToString(count >= 2 ? "00" : "0", CultureInfo.InvariantCulture),
            _ => null,
        });
    }

    /// <summary>A time formatted with a time pattern (canonical <c>HH:MM:SS</c>, <c>HH:MM</c> or <c>HHMMSS</c>).</summary>
    internal static string? Time(string pattern, string value, XfaLocale locale)
    {
        value = value.Trim();
        string[] formats = ["HH:mm:ss", "HH:mm", "HHmmss", "HHmm", "HH:mm:ss.fff", "HH:mm:ssZ", "HH:mm:sszzz"];
        if (!DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out var time))
        {
            return null;
        }

        var hour12 = time.Hour % 12 == 0 ? 12 : time.Hour % 12;
        return Symbols(pattern, (symbol, count) => symbol switch
        {
            'h' => count >= 2 ? hour12.ToString("00", CultureInfo.InvariantCulture) : hour12.ToString(CultureInfo.InvariantCulture),
            'H' => count >= 2 ? time.Hour.ToString("00", CultureInfo.InvariantCulture) : time.Hour.ToString(CultureInfo.InvariantCulture),
            'k' => count >= 2 ? (time.Hour % 12).ToString("00", CultureInfo.InvariantCulture) : (time.Hour % 12).ToString(CultureInfo.InvariantCulture),
            'K' => count >= 2 ? (time.Hour == 0 ? 24 : time.Hour).ToString("00", CultureInfo.InvariantCulture) : (time.Hour == 0 ? 24 : time.Hour).ToString(CultureInfo.InvariantCulture),
            'M' => count >= 2 ? time.Minute.ToString("00", CultureInfo.InvariantCulture) : time.Minute.ToString(CultureInfo.InvariantCulture),
            'S' => count >= 2 ? time.Second.ToString("00", CultureInfo.InvariantCulture) : time.Second.ToString(CultureInfo.InvariantCulture),
            'F' => time.Millisecond.ToString("000", CultureInfo.InvariantCulture)[..Math.Min(count, 3)],
            'A' => locale.Meridiems[time.Hour < 12 ? 0 : 1],
            'Z' or 'z' => string.Empty,
            _ => null,
        });
    }

    /// <summary>Text formatted with a text pattern: <c>9</c> a digit, <c>A</c> a letter, <c>O</c>/<c>0</c> a letter or digit, <c>X</c> any character, <c>t</c> a tab.</summary>
    internal static string? Text(string pattern, string value)
    {
        var output = new StringBuilder();
        var next = 0;
        var quoted = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\'')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted || c is not ('9' or 'A' or 'X' or 'O' or '0' or 't'))
            {
                output.Append(c);
                continue;
            }

            if (c == 't')
            {
                output.Append('\t');
                continue;
            }

            if (next >= value.Length)
            {
                return null;
            }

            var v = value[next++];
            var fits = c switch
            {
                '9' => char.IsDigit(v),
                'A' => char.IsLetter(v),
                'O' or '0' => char.IsLetterOrDigit(v),
                _ => true,
            };
            if (!fits)
            {
                return null;
            }

            output.Append(v);
        }

        return next == value.Length ? output.ToString() : null;
    }

    /// <summary>Formats a date or time pattern: each run of a symbol letter by <paramref name="symbol"/>, quoted and other text as it is.</summary>
    private static string? Symbols(string pattern, Func<char, int, string?> symbol)
    {
        var output = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\'')
            {
                if (quoted && i + 1 < pattern.Length && pattern[i + 1] == '\'')
                {
                    output.Append('\'');
                    i++;
                    continue;
                }

                quoted = !quoted;
                continue;
            }

            if (quoted || !char.IsAsciiLetter(c))
            {
                output.Append(c);
                continue;
            }

            var count = 1;
            while (i + count < pattern.Length && pattern[i + count] == c)
            {
                count++;
            }

            var text = symbol(c, count);
            if (text is null)
            {
                output.Append(c, count);
            }
            else
            {
                output.Append(text);
            }

            i += count - 1;
        }

        return output.ToString();
    }
}

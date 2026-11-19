using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Sheets;

/// <summary>
/// Tells numbers and dates from text in cells that hold only text (CSV), the way spreadsheets decide to right-align
/// them. Nothing is converted: the cell keeps its text as written.
/// </summary>
internal static partial class CellValues
{
    /// <summary>The longest text taken for a number or date.</summary>
    private const int MaxLength = 64;

    /// <summary>What a cell's text is, with <paramref name="decimalSeparator"/> as the decimal separator.</summary>
    public static CellKind Classify(string text, char decimalSeparator)
    {
        var value = text.AsSpan().Trim();
        if (value.IsEmpty)
        {
            return CellKind.Empty;
        }

        if (value.Length > MaxLength)
        {
            return CellKind.Text;
        }

        return IsNumber(value, decimalSeparator) ? CellKind.Number : DateRegex().IsMatch(value) ? CellKind.Date : CellKind.Text;
    }

    /// <summary>
    /// Whether the text is a number: digits with an optional sign (or in parentheses, as accounting writes negative
    /// amounts), grouped in thousands or not, with an optional decimal part and exponent, and an optional currency sign
    /// before or after it or a percent sign after it. The group separator is one of <c>, . ' </c>, a space, a no-break
    /// space or a narrow no-break space, other than the decimal separator, and the same throughout.
    /// </summary>
    public static bool IsNumber(ReadOnlySpan<char> value, char decimalSeparator)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '(' && value[^1] == ')')
        {
            value = value[1..^1].Trim();
        }

        var i = 0;
        bool sign = false, currency = false;
        while (i < value.Length)
        {
            if (!sign && IsSign(value[i]))
            {
                sign = true;
                i++;
            }
            else if (!currency && IsCurrency(value[i]))
            {
                currency = true;
                i++;
                while (i < value.Length && IsSpace(value[i]))
                {
                    i++;
                }
            }
            else
            {
                break;
            }
        }

        // The integer part: plain digits, or groups of three after a first group of one to three digits.
        int digits = 0, run = 0;
        var group = '\0';
        while (i < value.Length)
        {
            var c = value[i];
            if (char.IsAsciiDigit(c))
            {
                run++;
                digits++;
                i++;
            }
            else if (run > 0 && IsGroupSeparator(c, decimalSeparator) && (group == '\0' || group == c)
                && i + 1 < value.Length && char.IsAsciiDigit(value[i + 1]))
            {
                if (group == '\0' ? run > 3 : run != 3)
                {
                    return false;
                }

                group = c;
                run = 0;
                i++;
            }
            else
            {
                break;
            }
        }

        if (group != '\0' && run != 3)
        {
            return false;
        }

        if (i < value.Length && value[i] == decimalSeparator)
        {
            i++;
            while (i < value.Length && char.IsAsciiDigit(value[i]))
            {
                i++;
                digits++;
            }
        }

        if (digits == 0)
        {
            return false;
        }

        if (i < value.Length && value[i] is 'e' or 'E')
        {
            var j = i + 1;
            if (j < value.Length && value[j] is '+' or '-')
            {
                j++;
            }

            var exponent = j;
            while (j < value.Length && char.IsAsciiDigit(value[j]))
            {
                j++;
            }

            if (j == exponent)
            {
                return false;
            }

            i = j;
        }

        var end = i;
        while (i < value.Length && IsSpace(value[i]))
        {
            i++;
        }

        if (i < value.Length && (value[i] is '%' or '\u2030' || (!currency && IsCurrency(value[i]))))
        {
            end = i + 1;
        }

        return end == value.Length;
    }

    /// <summary>
    /// The decimal separator of the numbers in <paramref name="cells"/>: a comma if more of them are numbers only with a
    /// comma (<c>12,5</c>, <c>1.234,56</c>) than only with a point (<c>12.5</c>, <c>1,234.56</c>), a point if fewer;
    /// where none tells (<c>1,234</c> could be either), a comma for semicolon-delimited data (as spreadsheets write it
    /// where the comma is the decimal separator) and a point otherwise.
    /// </summary>
    public static char DetectDecimalSeparator(IEnumerable<string> cells, char delimiter)
    {
        int points = 0, commas = 0;
        foreach (var cell in cells)
        {
            var value = cell.AsSpan().Trim();
            if (value.IsEmpty || value.Length > MaxLength)
            {
                continue;
            }

            var point = IsNumber(value, '.');
            var comma = IsNumber(value, ',');
            points += point && !comma ? 1 : 0;
            commas += comma && !point ? 1 : 0;
        }

        return commas > points ? ',' : points > commas ? '.' : delimiter == ';' ? ',' : '.';
    }

    private static bool IsSign(char c) => c is '+' or '-' or '\u2212';

    private static bool IsSpace(char c) => c is ' ' or '\u00A0' or '\u202F';

    private static bool IsGroupSeparator(char c, char decimalSeparator) =>
        c != decimalSeparator && c is ',' or '.' or '\'' or '\u2019' or ' ' or '\u00A0' or '\u202F';

    /// <summary>A currency sign: any Unicode currency symbol (dollar, euro, pound, yen, rupee and the others).</summary>
    private static bool IsCurrency(char c) => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.CurrencySymbol;

    /// <summary>
    /// A date, a time or both, as spreadsheets write them: ISO 8601 (<c>2026-11-19</c>, <c>2026-11-19T08:30:00Z</c>),
    /// day, month and year with dots, slashes or hyphens (<c>19.11.2026</c>, <c>11/19/26</c>), optionally with a time,
    /// and times (<c>8:30</c>, <c>08:30:15.250</c>, <c>8:30 PM</c>).
    /// </summary>
    [GeneratedRegex(
        @"^(?:(?:\d{4}-\d{1,2}-\d{1,2}|\d{1,2}([./-])\d{1,2}\1(?:\d{4}|\d{2}))(?:[T ]\d{1,2}:\d{2}(?::\d{2}(?:[.,]\d{1,9})?)?(?:\s?[AaPp]\.?[Mm]\.?)?(?:Z|[+-]\d{2}:?\d{2})?)?|\d{1,2}:\d{2}(?::\d{2}(?:[.,]\d{1,9})?)?(?:\s?[AaPp]\.?[Mm]\.?)?)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();
}

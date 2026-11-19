using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Documents.Model;

/// <summary>Writes numbers as list labels, page numbers and note marks do.</summary>
internal static class Numbering
{
    /// <summary>Writes <paramref name="number"/> in a format (invariant: Latin letters and digits).</summary>
    public static string Format(int number, NumberFormat format) => format switch
    {
        NumberFormat.DecimalZero => number is >= 0 and < 10 ? "0" + number.ToString(CultureInfo.InvariantCulture) : number.ToString(CultureInfo.InvariantCulture),
        NumberFormat.LowerLetter => Letters(number, 'a'),
        NumberFormat.UpperLetter => Letters(number, 'A'),
        NumberFormat.LowerRoman => Roman(number).ToLowerInvariant(),
        NumberFormat.UpperRoman => Roman(number),
        NumberFormat.Ordinal => number.ToString(CultureInfo.InvariantCulture) + OrdinalSuffix(number),
        NumberFormat.None or NumberFormat.Bullet => string.Empty,
        _ => number.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>a..z, then aa..zz, aaa..zzz (as word processors repeat the letter); decimal for 0 and below.</summary>
    private static string Letters(int number, char first)
    {
        if (number <= 0)
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        var letter = (char)(first + ((number - 1) % 26));
        var count = Math.Min(((number - 1) / 26) + 1, 64);
        return new string(letter, count);
    }

    /// <summary>Roman numerals up to 3999; decimal outside 1..3999.</summary>
    private static string Roman(int number)
    {
        if (number is <= 0 or > 3999)
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        ReadOnlySpan<int> values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
        var result = new StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            while (number >= values[i])
            {
                result.Append(symbols[i]);
                number -= values[i];
            }
        }

        return result.ToString();
    }

    private static string OrdinalSuffix(int number)
    {
        var lastTwo = Math.Abs(number % 100);
        if (lastTwo is >= 11 and <= 13)
        {
            return "th";
        }

        return Math.Abs(number % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
    }
}

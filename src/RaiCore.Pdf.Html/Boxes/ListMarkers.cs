using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Html.Boxes;

/// <summary>Marker text for list items (CSS <c>list-style-type</c>).</summary>
internal static class ListMarkers
{
    public static string? Format(string type, int index) => type switch
    {
        "none" => null,
        "disc" => "\u2022",
        "circle" => "\u25E6",
        "square" => "\u25AA",
        "decimal" => $"{index.ToString(CultureInfo.InvariantCulture)}.",
        "decimal-leading-zero" => $"{index.ToString("00", CultureInfo.InvariantCulture)}.",
        "lower-alpha" or "lower-latin" => $"{Alphabetic(index, 'a')}.",
        "upper-alpha" or "upper-latin" => $"{Alphabetic(index, 'A')}.",
        "lower-roman" => $"{Roman(index).ToLowerInvariant()}.",
        "upper-roman" => $"{Roman(index)}.",
        "lower-greek" => $"{Greek(index)}.",
        _ => "\u2022",
    };

    /// <summary>a, b, ..., z, aa, ab, ... (bijective base 26). Non-positive values fall back to decimal.</summary>
    private static string Alphabetic(int index, char first)
    {
        if (index <= 0)
        {
            return index.ToString(CultureInfo.InvariantCulture);
        }

        var result = new StringBuilder();
        while (index > 0)
        {
            index--;
            result.Insert(0, (char)(first + (index % 26)));
            index /= 26;
        }

        return result.ToString();
    }

    private static string Roman(int index)
    {
        if (index is <= 0 or >= 4000)
        {
            return index.ToString(CultureInfo.InvariantCulture);
        }

        (int Value, string Symbol)[] numerals =
            [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        var result = new StringBuilder();
        foreach (var (value, symbol) in numerals)
        {
            while (index >= value)
            {
                result.Append(symbol);
                index -= value;
            }
        }

        return result.ToString();
    }

    private static string Greek(int index)
    {
        const string letters = "\u03B1\u03B2\u03B3\u03B4\u03B5\u03B6\u03B7\u03B8\u03B9\u03BA\u03BB\u03BC\u03BD\u03BE\u03BF\u03C0\u03C1\u03C3\u03C4\u03C5\u03C6\u03C7\u03C8\u03C9";
        return index >= 1 && index <= letters.Length ? letters[index - 1].ToString() : index.ToString(CultureInfo.InvariantCulture);
    }
}

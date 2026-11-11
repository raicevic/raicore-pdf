using System.Globalization;
using System.Text;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Html.Css;

/// <summary>Hyphenation: CSS <c>hyphens</c>, <c>hyphenate-character</c>, <c>hyphenate-limit-chars</c> and the language.</summary>
internal sealed partial record ComputedStyle
{
    /// <summary>CSS <c>hyphens</c> (inherited).</summary>
    public Hyphens Hyphens { get; init; } = Hyphens.Manual;

    /// <summary>CSS <c>hyphenate-character</c> (inherited); <c>null</c> for <c>auto</c>.</summary>
    public string? HyphenateCharacter { get; init; }

    /// <summary>CSS <c>hyphenate-limit-chars</c> (inherited); <c>null</c> values are <c>auto</c>.</summary>
    public HyphenationLimits HyphenationLimits { get; init; }

    /// <summary>The element's language: the HTML <c>lang</c> of it or its nearest ancestor that has one; <c>null</c> when unknown.</summary>
    public string? Language { get; init; }

    private static Hyphens ParseHyphens(string value) => value switch
    {
        "none" => Hyphens.None,
        "auto" => Hyphens.Auto,
        _ => Hyphens.Manual, // The initial value.
    };

    /// <summary>A CSS string (quoted, with escapes), or <c>null</c> for <c>auto</c> and invalid values.</summary>
    private static string? ParseHyphenateCharacter(string value)
    {
        value = value.Trim();
        if (value.Length < 2 || value[0] is not ('"' or '\'') || value[^1] != value[0])
        {
            return null;
        }

        var result = new StringBuilder();
        for (var i = 1; i < value.Length - 1; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length - 1)
            {
                result.Append(value[i]);
                continue;
            }

            // An escape: up to six hex digits (and one optional space after them), or the next character itself.
            var hex = 0;
            while (hex < 6 && i + 1 + hex < value.Length - 1 && char.IsAsciiHexDigit(value[i + 1 + hex]))
            {
                hex++;
            }

            if (hex == 0)
            {
                result.Append(value[++i]);
                continue;
            }

            var code = int.Parse(value.AsSpan(i + 1, hex), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            result.Append(code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(code) : "\uFFFD");
            i += hex;
            if (i + 1 < value.Length - 1 && value[i + 1] == ' ')
            {
                i++;
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// <c>hyphenate-limit-chars: [auto | &lt;integer&gt;]{1,3}</c>: the word length, then the characters before and
    /// after a hyphen; a missing after-value is the before-value. Invalid values are <c>auto</c> (the initial value).
    /// </summary>
    private static HyphenationLimits ParseHyphenationLimits(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > 3)
        {
            return default;
        }

        var limits = new int?[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "auto")
            {
                continue;
            }

            if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) || number < 0)
            {
                return default;
            }

            limits[i] = number;
        }

        return new HyphenationLimits(limits[0], limits[1], parts.Length == 2 ? limits[1] : limits[2]);
    }
}

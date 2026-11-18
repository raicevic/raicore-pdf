using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Extraction.Fonts;

/// <summary>
/// Maps glyph names to Unicode as the Adobe Glyph List specification does
/// (https://github.com/adobe-type-tools/agl-specification): the suffix after a period is dropped, underscores join
/// ligature components, and each component is an AGL name, <c>uniXXXX</c> (one or more groups of four hex digits) or
/// <c>uXXXX</c> to <c>uXXXXXX</c>. Lowercase hex digits are accepted too, as many fonts use them.
/// </summary>
internal static partial class GlyphNames
{
    private static readonly FrozenDictionary<string, string> List = Build();

    /// <summary>The text a glyph name stands for, or <c>null</c> when the name maps to nothing (such as <c>.notdef</c> or <c>g42</c>).</summary>
    public static string? ToUnicode(string name)
    {
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            name = name[..dot];
        }

        if (name.Length == 0)
        {
            return null;
        }

        if (!name.Contains('_', StringComparison.Ordinal))
        {
            return Component(name);
        }

        var text = new StringBuilder();
        foreach (var component in name.Split('_'))
        {
            text.Append(Component(component));
        }

        return text.Length > 0 ? text.ToString() : null;
    }

    private static string? Component(string name)
    {
        if (name.Length == 0)
        {
            return null;
        }

        if (List.TryGetValue(name, out var text))
        {
            return text;
        }

        if (name.Length >= 7 && (name.Length - 3) % 4 == 0 && name.StartsWith("uni", StringComparison.Ordinal))
        {
            var builder = new StringBuilder((name.Length - 3) / 4);
            for (var i = 3; i < name.Length; i += 4)
            {
                if (!TryHex(name.AsSpan(i, 4), out var unit) || unit is >= 0xD800 and <= 0xDFFF)
                {
                    return null;
                }

                builder.Append((char)unit);
            }

            return builder.ToString();
        }

        if (name.Length is >= 5 and <= 7 && name[0] == 'u' && TryHex(name.AsSpan(1), out var scalar)
            && scalar <= 0x10FFFF && scalar is not (>= 0xD800 and <= 0xDFFF))
        {
            return char.ConvertFromUtf32(scalar);
        }

        return null;
    }

    private static bool TryHex(ReadOnlySpan<char> digits, out int value) =>
        int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

    private static FrozenDictionary<string, string> Build()
    {
        var list = new Dictionary<string, string>(4400, StringComparer.Ordinal);
        foreach (var line in AdobeGlyphList.AsSpan().EnumerateLines())
        {
            var space = line.IndexOf(' ');
            if (space <= 0)
            {
                continue;
            }

            var text = new StringBuilder(2);
            foreach (var range in line[(space + 1)..].Split(' '))
            {
                text.Append((char)int.Parse(line[(space + 1)..][range], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
            }

            list[line[..space].ToString()] = text.ToString();
        }

        return list.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

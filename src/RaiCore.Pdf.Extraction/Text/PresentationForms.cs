using System.Collections.Frozen;
using System.Globalization;

namespace RaiCore.Pdf.Extraction.Text;

/// <summary>
/// The letters a presentation form stands for (its compatibility decomposition): ligatures such as fi, and the
/// contextual forms of Arabic letters. See PresentationForms.g.cs.
/// </summary>
internal static partial class PresentationForms
{
    private static readonly FrozenDictionary<char, string> Forms = Build();

    /// <summary>Whether a character is in the presentation-form blocks (U+FB00-FDFF, U+FE70-FEFF).</summary>
    public static bool IsPresentationForm(char c) => c is (>= 'ﬀ' and <= '﷿') or (>= 'ﹰ' and <= '﻿');

    /// <summary>The letters a presentation form decomposes to, or <c>null</c> when it has no decomposition.</summary>
    public static string? Decompose(char c) => Forms.GetValueOrDefault(c);

    private static FrozenDictionary<char, string> Build()
    {
        var forms = new Dictionary<char, string>(800);
        foreach (var line in Table.AsSpan().EnumerateLines())
        {
            var text = new System.Text.StringBuilder(2);
            foreach (var range in line[5..].Split(' '))
            {
                text.Append((char)int.Parse(line[5..][range], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
            }

            forms[(char)int.Parse(line[..4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)] = text.ToString();
        }

        return forms.ToFrozenDictionary();
    }
}

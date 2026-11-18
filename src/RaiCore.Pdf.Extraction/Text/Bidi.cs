using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Extraction.Text;

/// <summary>
/// Turns a line's text from visual order (as glyphs sit on the page, left to right) into logical order, for
/// right-to-left scripts. A simplified Unicode bidirectional algorithm (UAX #9) on tokens: each token is right-to-left
/// (Hebrew, Arabic and the other RTL scripts), left-to-right (other letters), a number (digits) or neutral (spaces,
/// punctuation). Numbers after left-to-right text are left-to-right (rule W7); neutrals between two tokens of the same
/// direction take it, numbers counting as right-to-left (N1), and the paragraph's otherwise (N2). Reversing runs by
/// embedding level (L2) is its own inverse, so it maps visual order back to logical order.
/// </summary>
internal static class Bidi
{
    /// <summary>Whether a paragraph (block) of tokens is right-to-left: it has more right-to-left letters than left-to-right ones.</summary>
    public static bool IsRightToLeft(IEnumerable<string> tokens) => Count(tokens) is var (rtl, ltr) && rtl > ltr;

    /// <summary>
    /// The direction text clearly has: 1 when it has more than twice as many right-to-left letters as left-to-right ones,
    /// -1 for the opposite (or no letters at all), 0 when neither clearly wins.
    /// </summary>
    public static int Direction(IEnumerable<string> tokens)
    {
        var (rtl, ltr) = Count(tokens);
        return rtl > 2 * ltr ? 1 : ltr > 2 * rtl || rtl + ltr == 0 ? -1 : 0;
    }

    private static (int Rtl, int Ltr) Count(IEnumerable<string> tokens)
    {
        int rtl = 0, ltr = 0;
        foreach (var token in tokens)
        {
            foreach (var rune in token.EnumerateRunes())
            {
                if (IsRtl(rune.Value))
                {
                    rtl++;
                }
                else if (Rune.IsLetter(rune))
                {
                    ltr++;
                }
            }
        }

        return (rtl, ltr);
    }

    /// <summary>The logical text of tokens in visual order, in a paragraph of the given direction (by default, the tokens' own).</summary>
    public static string ToLogical(IEnumerable<string> visual, bool? rightToLeft = null)
    {
        var tokens = visual as IReadOnlyList<string> ?? [.. visual];
        var kinds = new Kind[tokens.Count];
        var hasRtl = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            kinds[i] = KindOf(tokens[i]);
            hasRtl |= kinds[i] == Kind.Rtl;
        }

        if (!hasRtl)
        {
            return string.Concat(tokens);
        }

        var baseLevel = rightToLeft ?? IsRightToLeft(tokens) ? 1 : 0;

        // W4 and W5: a separator between two digits (1,250.00 or 00-000) and a currency or percent sign next to them
        // belong to the number.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                if (kinds[i] != Kind.Neutral || tokens[i].Length != 1)
                {
                    continue;
                }

                var before = i > 0 ? kinds[i - 1] : Kind.Neutral;
                var after = i + 1 < tokens.Count ? kinds[i + 1] : Kind.Neutral;
                if ((IsSeparator(tokens[i][0]) && before == Kind.Number && after == Kind.Number)
                    || (IsTerminator(tokens[i][0]) && (before == Kind.Number || after == Kind.Number)))
                {
                    kinds[i] = Kind.Number;
                }
            }
        }

        // W7: a number whose nearest strong token before it (or the paragraph) is left-to-right is left-to-right. In
        // a right-to-left paragraph, "before" in logical order is to the right.
        var previousStrong = baseLevel == 1 ? Kind.Rtl : Kind.Ltr;
        for (var step = 0; step < tokens.Count; step++)
        {
            var i = baseLevel == 1 ? tokens.Count - 1 - step : step;
            if (kinds[i] is Kind.Ltr or Kind.Rtl)
            {
                previousStrong = kinds[i];
            }
            else if (kinds[i] == Kind.Number && previousStrong == Kind.Ltr)
            {
                kinds[i] = Kind.Ltr;
            }
        }

        var levels = new int[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            levels[i] = kinds[i] switch
            {
                Kind.Rtl => 1,
                Kind.Ltr => baseLevel == 1 ? 2 : 0,
                Kind.Number => 2,
                _ => NeutralLevel(kinds, i, baseLevel),
            };
        }

        var order = Enumerable.Range(0, tokens.Count).ToArray();
        for (var level = levels.Max(); level >= 1; level--)
        {
            for (var start = 0; start < order.Length;)
            {
                if (levels[order[start]] < level)
                {
                    start++;
                    continue;
                }

                var end = start;
                while (end < order.Length && levels[order[end]] >= level)
                {
                    end++;
                }

                Array.Reverse(order, start, end - start);
                start = end;
            }
        }

        var text = new StringBuilder();
        foreach (var index in order)
        {
            text.Append(tokens[index]);
        }

        return text.ToString();
    }

    /// <summary>N1 and N2: a neutral between two tokens of the same direction (numbers count as right-to-left) takes it; otherwise the paragraph's.</summary>
    private static int NeutralLevel(Kind[] kinds, int index, int baseLevel)
    {
        var before = Kind.Neutral;
        for (var i = index - 1; i >= 0 && before == Kind.Neutral; i--)
        {
            before = kinds[i];
        }

        var after = Kind.Neutral;
        for (var i = index + 1; i < kinds.Length && after == Kind.Neutral; i++)
        {
            after = kinds[i];
        }

        var sos = baseLevel == 1 ? Kind.Rtl : Kind.Ltr;
        var left = before switch { Kind.Neutral => sos, Kind.Number => Kind.Rtl, _ => before };
        var right = after switch { Kind.Neutral => sos, Kind.Number => Kind.Rtl, _ => after };
        return left != right ? baseLevel : left == Kind.Rtl ? 1 : baseLevel == 1 ? 2 : 0;
    }

    private static Kind KindOf(string token)
    {
        var kind = Kind.Neutral;
        foreach (var rune in token.EnumerateRunes())
        {
            if (IsRtl(rune.Value))
            {
                return Kind.Rtl;
            }

            if (Rune.IsLetter(rune))
            {
                kind = Kind.Ltr;
            }
            else if (kind == Kind.Neutral && Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber)
            {
                kind = Kind.Number;
            }
        }

        return kind;
    }

    /// <summary>European and common separators between digits (bidi classes ES and CS).</summary>
    private static bool IsSeparator(char c) => c is '+' or '-' or '\u2212' or '.' or ',' or ':' or '/' or '\u00A0';

    /// <summary>Terminators that go with numbers (bidi class ET): currency signs, percent and degree signs, number sign.</summary>
    private static bool IsTerminator(char c) =>
        c is '%' or '#' or '\u00B0' or '\u2030' or '\u2031' or '\u2032' or '\u2033' || char.GetUnicodeCategory(c) == UnicodeCategory.CurrencySymbol;

    /// <summary>Whether a character is in a right-to-left script: Hebrew, Arabic, Syriac, Thaana, NKo and their presentation forms.</summary>
    internal static bool IsRtl(int c) => c is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF)
        or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF);

    private enum Kind
    {
        Neutral,
        Ltr,
        Rtl,
        Number,
    }
}

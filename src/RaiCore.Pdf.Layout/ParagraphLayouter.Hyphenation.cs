using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Layout;

/// <summary>A place where a line may end: <see cref="Hyphen"/> when the line then ends with a hyphen.</summary>
internal readonly record struct BreakOpportunity(int Position, bool Required, bool Hyphen);

/// <summary>Hyphenation: soft hyphens and automatic hyphenation (CSS <c>hyphens</c>) as line break opportunities.</summary>
internal static partial class ParagraphLayouter
{
    /// <summary>
    /// The paragraph's break opportunities in order: those of UAX #14, where breaks after a soft hyphen become hyphen
    /// breaks (or are dropped where <c>hyphens</c> is <c>none</c>), and the automatic hyphenation points of words whose
    /// first character has <c>hyphens: auto</c> and a language with patterns.
    /// </summary>
    private static List<BreakOpportunity> GetBreakOpportunities(IReadOnlyList<ParagraphSpan> spans, string text, int[] spanStarts)
    {
        var hyphenation = AutomaticHyphenationPoints(spans, text, spanStarts);
        var result = new List<BreakOpportunity>();
        var h = 0;
        foreach (var opportunity in LineBreaker.GetBreakOpportunities(text))
        {
            for (; h < hyphenation.Count && hyphenation[h] <= opportunity.Position; h++)
            {
                if (hyphenation[h] < opportunity.Position)
                {
                    result.Add(new BreakOpportunity(hyphenation[h], false, true));
                }
            }

            var softHyphen = !opportunity.Required && opportunity.Position < text.Length && text[opportunity.Position - 1] == SoftHyphen;
            if (softHyphen && StyleAt(spans, spanStarts, opportunity.Position - 1).Hyphens == Hyphens.None)
            {
                continue;
            }

            result.Add(new BreakOpportunity(opportunity.Position, opportunity.Required, softHyphen));
        }

        return result;
    }

    /// <summary>
    /// Where words may be hyphenated automatically, in order: inside each word (a run of letters and marks) whose first
    /// character's style has <c>hyphens: auto</c> and a language with patterns, is long enough, and has no soft hyphen
    /// (explicit hyphenation points win, as in browsers).
    /// </summary>
    private static List<int> AutomaticHyphenationPoints(IReadOnlyList<ParagraphSpan> spans, string text, int[] spanStarts)
    {
        var points = new List<int>();
        if (!spans.Any(s => s.Style.Hyphens == Hyphens.Auto))
        {
            return points;
        }

        var s = 0;
        for (var i = 0; i < text.Length;)
        {
            while (spanStarts[s + 1] <= i)
            {
                s++;
            }

            var span = spans[s];
            var style = span.Style;
            var hyphenator = span.Image is null && span.Box is null && style.Hyphens == Hyphens.Auto ? Hyphenator.ForLanguage(style.Language) : null;
            if (hyphenator is null)
            {
                // Skip the whole word, so a later span in it does not start a word of its own.
                i = char.IsLetter(text[i]) ? WordEnd(text, i, c => char.IsLetter(c) || IsMark(c) || c == SoftHyphen) : i + 1;
                continue;
            }

            if (!hyphenator.IsWordCharacter(text[i]) || !char.IsLetter(text[i]))
            {
                i++;
                continue;
            }

            var end = WordEnd(text, i, c => hyphenator.IsWordCharacter(c) || c == SoftHyphen);
            var limits = style.HyphenationLimits;
            var word = text[i..end];
            if (word.Length >= (limits.Word ?? HyphenationLimits.DefaultWord) && !word.Contains(SoftHyphen, StringComparison.Ordinal))
            {
                foreach (var point in hyphenator.Hyphenate(word, limits.Before ?? hyphenator.MinBefore, limits.After ?? hyphenator.MinAfter))
                {
                    points.Add(i + point);
                }
            }

            i = end;
        }

        return points;
    }

    private static int WordEnd(string text, int start, Func<char, bool> inWord)
    {
        var end = start;
        while (end < text.Length && inWord(text[end]))
        {
            end++;
        }

        return end;
    }

    private static bool IsMark(char c) => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) is
        System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark
        or System.Globalization.UnicodeCategory.EnclosingMark;

    /// <summary>
    /// The text that ends a hyphenated line: the style's <c>hyphenate-character</c>, or the hyphen U+2010 when the first
    /// font has it and the hyphen-minus otherwise.
    /// </summary>
    private static string HyphenText(TextStyle style) =>
        style.HyphenateCharacter ?? (style.Fonts[0].HasGlyph(0x2010) ? "\u2010" : "-");

    /// <summary>The width of <see cref="HyphenText"/>, shaped in the style's fonts with its letter spacing.</summary>
    private static double HyphenWidth(TextStyle style) => HyphenText(style) is { Length: > 0 } hyphen
        ? TextShaper.Shape(hyphen, style.Fonts, ShapingFor(style)).Runs.Sum(run => Spaced(run, style).Sum(g => g.Advance)) * style.FontSize
        : 0;
}

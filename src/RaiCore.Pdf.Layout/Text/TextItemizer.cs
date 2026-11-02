using System.Globalization;
using System.Text;
using HarfBuzzSharp;
using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Layout.Text;

/// <summary>A range of text to shape with one font, script and direction.</summary>
internal readonly record struct TextItem(int Start, int Length, OpenTypePdfFont Font, Script Script, TextDirection Direction);

/// <summary>
/// Splits text into runs that can each be shaped in one go: per grapheme cluster it picks the first font
/// that covers it, and it resolves each character's Unicode script (characters such as spaces and digits,
/// which belong to no script, join the surrounding run).
/// </summary>
internal static class TextItemizer
{
    public static IReadOnlyList<TextItem> Itemize(string text, IReadOnlyList<OpenTypePdfFont> fonts)
    {
        var items = new List<TextItem>();
        if (text.Length == 0)
        {
            return items;
        }

        var clusters = GetClusters(text);
        var scripts = ResolveScripts(text, clusters, out var neutral);

        OpenTypePdfFont? previousFont = null;
        var runStart = 0;
        var runScript = scripts[0];
        OpenTypePdfFont? runFont = null;
        for (var c = 0; c < clusters.Count; c++)
        {
            var (start, length) = clusters[c];
            var font = ChooseFont(text.AsSpan(start, length), fonts, previousFont, neutral[c]);
            if (runFont is not null && (!ReferenceEquals(font, runFont) || scripts[c] != runScript))
            {
                items.Add(new TextItem(runStart, start - runStart, runFont, runScript, DirectionOf(runScript)));
                runStart = start;
            }

            runFont = font;
            runScript = scripts[c];
            previousFont = font;
        }

        items.Add(new TextItem(runStart, text.Length - runStart, runFont!, runScript, DirectionOf(runScript)));
        return items;
    }

    private static List<(int Start, int Length)> GetClusters(string text)
    {
        var clusters = new List<(int, int)>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            clusters.Add((enumerator.ElementIndex, ((string)enumerator.Current).Length));
        }

        return clusters;
    }

    /// <summary>
    /// The script of each cluster. Common (spaces, punctuation, digits) and Inherited (combining marks)
    /// take the script of the preceding cluster, or of the following one at the start of the text.
    /// </summary>
    private static Script[] ResolveScripts(string text, List<(int Start, int Length)> clusters, out bool[] neutral)
    {
        var scripts = new Script[clusters.Count];
        neutral = new bool[clusters.Count];
        var unicode = UnicodeFunctions.Default;
        for (var c = 0; c < clusters.Count; c++)
        {
            var rune = Rune.GetRuneAt(text, clusters[c].Start);
            scripts[c] = unicode.GetScript(rune.Value);
        }

        var last = Script.Invalid;
        for (var c = 0; c < scripts.Length; c++)
        {
            if (IsNeutral(scripts[c]))
            {
                neutral[c] = true;
                scripts[c] = last;
            }
            else
            {
                last = scripts[c];
            }
        }

        // Leading neutral clusters take the first real script (or Common if there is none).
        var first = scripts.FirstOrDefault(s => s != Script.Invalid);
        for (var c = 0; c < scripts.Length && scripts[c] == Script.Invalid; c++)
        {
            scripts[c] = first == Script.Invalid ? Script.Common : first;
        }

        return scripts;
    }

    private static bool IsNeutral(Script script) =>
        script == Script.Common || script == Script.Inherited || script == Script.Unknown || script == Script.Invalid;

    private static TextDirection DirectionOf(Script script) =>
        script.HorizontalDirection == Direction.RightToLeft ? TextDirection.RightToLeft : TextDirection.LeftToRight;

    private static OpenTypePdfFont ChooseFont(ReadOnlySpan<char> cluster, IReadOnlyList<OpenTypePdfFont> fonts, OpenTypePdfFont? previous, bool neutral)
    {
        // Keep neutral characters (spaces, punctuation, digits) in the current font to avoid needless run splits.
        if (previous is not null && neutral && Covers(previous, cluster))
        {
            return previous;
        }

        foreach (var font in fonts)
        {
            if (Covers(font, cluster))
            {
                return font;
            }
        }

        // No font has the whole cluster: use the first that has its base character, else the primary font.
        var baseChar = Rune.GetRuneAt(cluster.ToString(), 0).Value;
        return fonts.FirstOrDefault(f => f.HasGlyph(baseChar)) ?? fonts[0];
    }

    private static bool Covers(OpenTypePdfFont font, ReadOnlySpan<char> cluster)
    {
        foreach (var rune in cluster.EnumerateRunes())
        {
            if (!IsDefaultIgnorable(rune.Value) && !font.HasGlyph(rune.Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Invisible formatting characters (joiners, variation selectors, direction marks) need no glyph.</summary>
    private static bool IsDefaultIgnorable(int c) =>
        c is 0x00AD or 0x034F or 0x061C or (>= 0x115F and <= 0x1160) or (>= 0x17B4 and <= 0x17B5) or (>= 0x180B and <= 0x180F)
            or (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E) or (>= 0x2060 and <= 0x206F) or 0x3164
            or (>= 0xFE00 and <= 0xFE0F) or 0xFEFF or 0xFFA0 or (>= 0xFFF0 and <= 0xFFF8)
            or (>= 0x1BCA0 and <= 0x1BCA3) or (>= 0x1D173 and <= 0x1D17A) or (>= 0xE0000 and <= 0xE0FFF);
}

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
/// which belong to no script, join the surrounding run). The direction is the script's unless one is given
/// (as the bidi algorithm resolves it for the text).
/// </summary>
internal static class TextItemizer
{
    /// <summary>The scripts of code points asked for so far (see <see cref="ScriptOf"/>), 256 per block; 0 when not known yet.</summary>
    private static readonly uint[]?[] ScriptBlocks = new uint[]?[0x110000 >> 8];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Script, TextDirection> Directions = new();

    public static IReadOnlyList<TextItem> Itemize(string text, IReadOnlyList<OpenTypePdfFont> fonts, TextDirection? direction = null)
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
                items.Add(new TextItem(runStart, start - runStart, runFont, runScript, direction ?? DirectionOf(runScript)));
                runStart = start;
            }

            runFont = font;
            runScript = scripts[c];
            previousFont = font;
        }

        items.Add(new TextItem(runStart, text.Length - runStart, runFont!, runScript, direction ?? DirectionOf(runScript)));
        return items;
    }

    /// <summary>The grapheme clusters (extended, UAX #29), without allocating a string for each as a text element enumerator does.</summary>
    private static List<(int Start, int Length)> GetClusters(string text)
    {
        var clusters = new List<(int, int)>(text.Length);
        for (var start = 0; start < text.Length;)
        {
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(start));
            clusters.Add((start, length));
            start += length;
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
        for (var c = 0; c < clusters.Count; c++)
        {
            scripts[c] = ScriptOf(Rune.GetRuneAt(text, clusters[c].Start).Value);
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
        Directions.GetOrAdd(script, static s => s.HorizontalDirection == Direction.RightToLeft ? TextDirection.RightToLeft : TextDirection.LeftToRight);

    /// <summary>
    /// The Unicode script of a code point. HarfBuzz is asked once per code point (a native call), and the answer is kept
    /// in blocks of 256 code points, allocated when first used. Threads may race to fill an entry; they write the same value.
    /// </summary>
    private static Script ScriptOf(int codePoint)
    {
        var block = Volatile.Read(ref ScriptBlocks[codePoint >> 8]);
        if (block is null)
        {
            block = new uint[256];
            block = Interlocked.CompareExchange(ref ScriptBlocks[codePoint >> 8], block, null) ?? block;
        }

        var tag = block[codePoint & 0xFF];
        if (tag == 0)
        {
            tag = UnicodeFunctions.Default.GetScript(codePoint);
            block[codePoint & 0xFF] = tag;
        }

        return tag;
    }

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

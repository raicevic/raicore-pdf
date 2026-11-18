using System.Text;
using System.Text.RegularExpressions;
using RaiCore.Pdf.Extraction.Content;

namespace RaiCore.Pdf.Extraction.Text;

/// <summary>Finds matches of a pattern in a page's text and where their characters are on the page.</summary>
internal static class TextSearch
{
    /// <summary>
    /// The pattern for plain text: the text's words, with any run of whitespace between them (spaces, a line break)
    /// where the text has whitespace.
    /// </summary>
    public static Regex PlainText(string text, bool ignoreCase)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            throw new ArgumentException("The text to find has no characters other than whitespace.", nameof(text));
        }

        var pattern = new StringBuilder();
        foreach (var word in words)
        {
            if (pattern.Length > 0)
            {
                pattern.Append(@"\s+");
            }

            pattern.Append(Regex.Escape(GlyphCollector.Normalize(word)));
        }

        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex(pattern.ToString(), options);
    }

    /// <summary>
    /// The matches of <paramref name="pattern"/> in a page's text, each with a rectangle (in the page's user space) per
    /// line it is on. Empty matches, and matches of nothing but whitespace (spaces and line breaks the layout put
    /// in), are left out.
    /// </summary>
    public static List<TextMatch> Find(MappedText text, Regex pattern, int pageIndex, Matrix displayMatrix)
    {
        var matches = new List<TextMatch>();
        if (text.Length == 0)
        {
            return matches;
        }

        var toUser = displayMatrix.Invert() ?? Matrix.Identity;
        foreach (Match match in pattern.Matches(text.ToString()))
        {
            var rectangles = new List<PdfRectangle>();
            Box? line = null;
            for (var i = match.Index; i < match.Index + match.Length; i++)
            {
                if (text[i] == '\n')
                {
                    Flush();
                }
                else if (text.SourceAt(i) is { } source)
                {
                    var box = source.Bounds;
                    line = line is { } l
                        ? new Box(Math.Min(l.Left, box.Left), Math.Min(l.Top, box.Top), Math.Max(l.Right, box.Right), Math.Max(l.Bottom, box.Bottom))
                        : box;
                }
            }

            Flush();
            if (rectangles.Count > 0)
            {
                matches.Add(new TextMatch(pageIndex, match.Value, rectangles));
            }

            void Flush()
            {
                if (line is { } box)
                {
                    rectangles.Add(ToUserSpace(box, toUser));
                    line = null;
                }
            }
        }

        return matches;
    }

    private static PdfRectangle ToUserSpace(Box box, Matrix toUser)
    {
        var a = toUser.Transform(box.Left, box.Top);
        var b = toUser.Transform(box.Right, box.Bottom);
        var (left, bottom) = (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
        return new PdfRectangle(left, bottom, Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }
}

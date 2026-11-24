using System.Globalization;
using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Sheets;

/// <summary>
/// A sheet's headers and footers as Excel stores them: the texts of odd pages (all pages unless the others are set),
/// even pages and the first page, each with its left, center and right sections and codes (<c>&amp;P</c>, <c>&amp;N</c>,
/// <c>&amp;D</c>, <c>&amp;"Arial,Bold"</c>...), and where they go.
/// </summary>
internal sealed record SheetHeaderFooter
{
    public string? OddHeader { get; init; }

    public string? OddFooter { get; init; }

    public string? EvenHeader { get; init; }

    public string? EvenFooter { get; init; }

    public string? FirstHeader { get; init; }

    public string? FirstFooter { get; init; }

    /// <summary>Whether even pages have their own texts.</summary>
    public bool DifferentOddEven { get; init; }

    /// <summary>Whether the first page has its own texts.</summary>
    public bool DifferentFirst { get; init; }

    /// <summary>Whether the texts scale with the sheet's print scale.</summary>
    public bool ScaleWithDocument { get; init; } = true;

    /// <summary>The distance of the header from the page's top edge, in points (Excel's default is 0.3 inch).</summary>
    public double HeaderMargin { get; init; } = 21.6;

    /// <summary>The distance of the footer from the page's bottom edge, in points.</summary>
    public double FooterMargin { get; init; } = 21.6;

    /// <summary>The number of the sheet's first page.</summary>
    public int FirstPageNumber { get; init; } = 1;

    /// <summary>Whether there is any text.</summary>
    public bool IsEmpty => new[] { OddHeader, OddFooter, EvenHeader, EvenFooter, FirstHeader, FirstFooter }.All(string.IsNullOrEmpty);

    /// <summary>The header and footer texts of a page of the sheet (from 1).</summary>
    public (string? Header, string? Footer) For(int page) =>
        DifferentFirst && page == 1 ? (FirstHeader, FirstFooter)
        : DifferentOddEven && page % 2 == 0 ? (EvenHeader, EvenFooter)
        : (OddHeader, OddFooter);
}

/// <summary>What the codes of a header or footer stand for on a page.</summary>
/// <param name="Page">The page's number (<c>&amp;P</c>).</param>
/// <param name="Pages">The number of pages of the sheet (<c>&amp;N</c>).</param>
/// <param name="Sheet">The sheet's name (<c>&amp;A</c>).</param>
/// <param name="File">The workbook's file name (<c>&amp;F</c>, and <c>&amp;Z</c> for its folder, which is not known).</param>
/// <param name="Date">The date and time printed (<c>&amp;D</c>, <c>&amp;T</c>).</param>
/// <param name="Culture">The culture of the date and time.</param>
internal readonly record struct HeaderFooterContext(int Page, int Pages, string? Sheet, string? File, DateTime Date, CultureInfo Culture);

/// <summary>A run of a header or footer section: its text and format (a font family or <c>null</c> for the default).</summary>
internal sealed record HeaderFooterRun(string Text, string? Font, double? Size, bool Bold, bool Italic, CellUnderline Underline, bool Strike, bool Superscript, bool Subscript, PdfColor? Color);

/// <summary>
/// Parses and draws header and footer texts: the sections (<c>&amp;L</c>, <c>&amp;C</c>, <c>&amp;R</c>), the fields
/// (page number with an offset, pages, date, time, file, sheet) and the font codes (family and style, size, bold,
/// italic, underline, double underline, strikethrough, superscript, subscript, color). Pictures (<c>&amp;G</c>) are
/// left out; unknown codes are skipped.
/// </summary>
internal static class HeaderFooterText
{
    /// <summary>The runs of the left, center and right sections of a text, its fields resolved for a page.</summary>
    public static List<HeaderFooterRun>[] Parse(string text, HeaderFooterContext context)
    {
        var sections = new[] { new List<HeaderFooterRun>(), new List<HeaderFooterRun>(), new List<HeaderFooterRun>() };
        var current = 1; // Text before any section code is centered.
        string? font = null;
        double? size = null;
        bool bold = false, italic = false, strike = false, superscript = false, subscript = false;
        var underline = CellUnderline.None;
        PdfColor? color = null;
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length > 0)
            {
                sections[current].Add(new HeaderFooterRun(buffer.ToString(), font, size, bold, italic, underline, strike, superscript, subscript, color));
                buffer.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '&' || i + 1 >= text.Length)
            {
                buffer.Append(c);
                continue;
            }

            var code = text[++i];
            switch (char.ToUpperInvariant(code))
            {
                case '&':
                    buffer.Append('&');
                    break;
                case 'L' or 'C' or 'R':
                    Flush();
                    current = char.ToUpperInvariant(code) switch { 'L' => 0, 'C' => 1, _ => 2 };
                    break;
                case 'P':
                    // &P, &P+n, &P-n.
                    var page = context.Page;
                    if (i + 2 < text.Length && text[i + 1] is '+' or '-' && char.IsAsciiDigit(text[i + 2]))
                    {
                        var sign = text[i + 1] == '-' ? -1 : 1;
                        var n = 0;
                        for (i += 2; i < text.Length && char.IsAsciiDigit(text[i]) && n < 100_000; i++)
                        {
                            n = (n * 10) + (text[i] - '0');
                        }

                        i--;
                        page += sign * n;
                    }

                    buffer.Append(page.ToString(CultureInfo.InvariantCulture));
                    break;
                case 'N':
                    buffer.Append(context.Pages.ToString(CultureInfo.InvariantCulture));
                    break;
                case 'D':
                    // The culture's short date; ISO 8601 for the invariant culture, as the built-in date format.
                    buffer.Append(context.Date.ToString(context.Culture.Equals(CultureInfo.InvariantCulture) ? "yyyy-MM-dd" : "d", context.Culture));
                    break;
                case 'T':
                    buffer.Append(context.Date.ToString("t", context.Culture));
                    break;
                case 'A':
                    buffer.Append(context.Sheet);
                    break;
                case 'F':
                    buffer.Append(context.File);
                    break;
                case 'Z' or 'G':
                    break; // The folder is not known; pictures are left out.
                case 'B' or 'I' or 'U' or 'E' or 'S' or 'X' or 'Y':
                    Flush();
                    switch (char.ToUpperInvariant(code))
                    {
                        case 'B':
                            bold = !bold;
                            break;
                        case 'I':
                            italic = !italic;
                            break;
                        case 'U':
                            underline = underline == CellUnderline.Single ? CellUnderline.None : CellUnderline.Single;
                            break;
                        case 'E':
                            underline = underline == CellUnderline.Double ? CellUnderline.None : CellUnderline.Double;
                            break;
                        case 'S':
                            strike = !strike;
                            break;
                        case 'X':
                            (superscript, subscript) = (!superscript, false);
                            break;
                        default:
                            (subscript, superscript) = (!subscript, false);
                            break;
                    }

                    break;
                case '"':
                {
                    // &"Family,Style": "-" keeps the family; the style names bold and italic.
                    Flush();
                    var end = text.IndexOf('"', i + 1);
                    var spec = end < 0 ? text[(i + 1)..] : text[(i + 1)..end];
                    i = end < 0 ? text.Length : end;
                    var comma = spec.IndexOf(',');
                    var family = (comma < 0 ? spec : spec[..comma]).Trim();
                    var style = comma < 0 ? "Regular" : spec[(comma + 1)..];
                    font = family is "" or "-" ? font : family;
                    bold = style.Contains("Bold", StringComparison.OrdinalIgnoreCase);
                    italic = style.Contains("Italic", StringComparison.OrdinalIgnoreCase) || style.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
                    break;
                }

                case 'K':
                {
                    // &Krrggbb, or a theme color (&KttSnnn), which is shown as black.
                    Flush();
                    var hex = text.Length >= i + 7 ? text.Substring(i + 1, 6) : string.Empty;
                    color = uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)
                        ? PdfColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
                        : PdfColor.Black;
                    i += Math.Min(6, text.Length - i - 1);
                    break;
                }

                default:
                    if (char.IsAsciiDigit(code))
                    {
                        // &nn: the font size in points.
                        Flush();
                        var n = code - '0';
                        while (i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]) && n < 1000)
                        {
                            n = (n * 10) + (text[++i] - '0');
                        }

                        size = Math.Clamp(n, 1, 409);
                    }

                    break;
            }
        }

        Flush();
        return sections;
    }

    /// <summary>
    /// The paragraphs of a header or footer's sections (left, center, right; <c>null</c> where empty) in the sheet's
    /// fonts at a scale.
    /// </summary>
    public static Paragraph?[] Paragraphs(List<HeaderFooterRun>[] sections, Func<string?, bool, bool, PdfFont[]> fonts, double defaultSize, double scale)
    {
        var result = new Paragraph?[3];
        for (var s = 0; s < 3; s++)
        {
            if (sections[s].Count == 0)
            {
                continue;
            }

            TextStyle Style(HeaderFooterRun run)
            {
                var color = run.Color ?? PdfColor.Black;
                var decorations = new List<TextDecoration>();
                if (run.Underline != CellUnderline.None)
                {
                    decorations.Add(new TextDecoration(TextDecorationLines.Underline, color)
                    {
                        Style = run.Underline == CellUnderline.Double ? TextDecorationStyle.Double : TextDecorationStyle.Solid,
                    });
                }

                if (run.Strike)
                {
                    decorations.Add(new TextDecoration(TextDecorationLines.LineThrough, color));
                }

                var size = (run.Size ?? defaultSize) * scale;
                return new TextStyle(fonts(run.Font, run.Bold, run.Italic), run.Superscript || run.Subscript ? size * 0.66 : size)
                {
                    Color = color,
                    Decorations = decorations,
                    VerticalAlign = InlineAlignment.Raised(run.Superscript ? size * 0.33 : run.Subscript ? -size * 0.15 : 0),
                };
            }

            var paragraph = new Paragraph(Style(sections[s][0]))
            {
                Alignment = s switch { 0 => TextAlignment.Left, 1 => TextAlignment.Center, _ => TextAlignment.Right },
            };
            foreach (var run in sections[s])
            {
                paragraph.Add(SheetRenderer.DisplayText(run.Text), Style(run));
            }

            result[s] = paragraph;
        }

        return result;
    }
}

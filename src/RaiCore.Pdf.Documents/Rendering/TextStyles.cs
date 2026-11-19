using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Documents.Rendering;

/// <summary>
/// Turns run formats into layout text styles: fonts found by family (with metric-compatible substitutes, the generic
/// family of known serif and monospace fonts, the options' family and fallbacks, and Liberation Sans last), sizes,
/// decorations, raised and lowered text, and the line height of the paragraph's line spacing. Styles are made once.
/// </summary>
internal sealed class TextStyles
{
    /// <summary>The size of superscript and subscript text, relative to the run's size.</summary>
    internal const double ScriptScale = 2 / 3.0;

    /// <summary>The size of small capitals, relative to the run's size.</summary>
    internal const double SmallCapsScale = 0.8;

    private static readonly FontCollection Bundled = BundledFonts.CreateCollection();

    private readonly DocumentConverterOptions _options;
    private readonly FontCollection _collection;
    private readonly Dictionary<(string?, bool, bool), PdfFont[]> _fonts = [];
    private readonly Dictionary<(RunFormat, LineSpacingRule, double, double), TextStyle> _styles = [];

    public TextStyles(DocumentConverterOptions options)
    {
        _options = options;
        _collection = options.Fonts ?? Bundled;
    }

    /// <summary>The fonts of a family and style, with substitutes and fallbacks, at least one.</summary>
    public PdfFont[] Fonts(string? family, bool bold, bool italic)
    {
        if (_fonts.TryGetValue((family, bold, italic), out var fonts))
        {
            return fonts;
        }

        var weight = bold ? 700 : 400;
        var families = new List<string>();
        if (family is { Length: > 0 })
        {
            families.Add(family);
            if (Substitute(family) is { } substitute)
            {
                families.Add(substitute);
            }

            if (GenericFamily(family) is { } generic)
            {
                families.Add(generic);
            }
        }

        families.Add(_options.FontFamily);
        families.AddRange(_options.FallbackFontFamilies);
        fonts =
        [
            .. families.Select(f => _collection.Find(f, weight, italic))
                .Append(Bundled.Find("Liberation Sans", weight, italic))
                .OfType<PdfFont>()
                .Distinct(),
        ];
        _fonts[(family, bold, italic)] = fonts;
        return fonts;
    }

    /// <summary>The metric-compatible substitute of a common font, or <c>null</c>.</summary>
    internal static string? Substitute(string family) => family.ToUpperInvariant() switch
    {
        "CALIBRI" => "Carlito",
        "CAMBRIA" => "Caladea",
        "SEGOE UI" => "Selawik",
        _ => null,
    };

    /// <summary>The generic family of a font a collection may lack, from its name: serif, monospace or none.</summary>
    internal static string? GenericFamily(string name)
    {
        if (name.Contains("Mono", StringComparison.OrdinalIgnoreCase) || name.Contains("Courier", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Consolas", StringComparison.OrdinalIgnoreCase) || name.Contains("Console", StringComparison.OrdinalIgnoreCase))
        {
            return "monospace";
        }

        if (name.Contains("Sans", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] serif = ["Times", "Cambria", "Georgia", "Garamond", "Antiqua", "Palatino", "Century", "Bookman", "Baskerville", "Serif", "Caladea", "Constantia"];
        return serif.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)) ? "serif" : null;
    }

    /// <summary>The text style of a run in a paragraph with a line spacing.</summary>
    public TextStyle Style(RunFormat format, ParagraphFormat paragraph) => Style(format, paragraph.LineRule, paragraph.LineSpacing, 1);

    /// <summary>The text style of a run, its size scaled by <paramref name="scale"/> (small capitals).</summary>
    public TextStyle Style(RunFormat format, LineSpacingRule rule, double spacing, double scale)
    {
        var key = (format, rule, spacing, scale);
        if (_styles.TryGetValue(key, out var style))
        {
            return style;
        }

        var fonts = Fonts(format.FontFamily, format.Bold, format.Italic);
        var size = format.FontSize * scale;
        var shift = format.BaselineShift;
        if (format.Position == VerticalPosition.Superscript)
        {
            shift += size / 3;
            size *= ScriptScale;
        }
        else if (format.Position == VerticalPosition.Subscript)
        {
            shift -= size / 7;
            size *= ScriptScale;
        }

        var color = format.Color ?? PdfColor.Black;
        var decorations = new List<TextDecoration>();
        if (format.Underline != UnderlineStyle.None)
        {
            decorations.Add(new TextDecoration(TextDecorationLines.Underline, color)
            {
                Style = format.Underline switch
                {
                    UnderlineStyle.Double => TextDecorationStyle.Double,
                    UnderlineStyle.Dotted => TextDecorationStyle.Dotted,
                    UnderlineStyle.Dashed => TextDecorationStyle.Dashed,
                    UnderlineStyle.Wavy => TextDecorationStyle.Wavy,
                    _ => TextDecorationStyle.Solid,
                },
            });
        }

        if (format.Strike)
        {
            decorations.Add(new TextDecoration(TextDecorationLines.LineThrough, color));
        }

        var natural = fonts[0].GetLineHeight(format.FontSize);
        style = new TextStyle(fonts, size)
        {
            Color = color,
            Decorations = decorations,
            LetterSpacing = format.LetterSpacing,
            VerticalAlign = shift != 0 ? InlineAlignment.Raised(shift) : InlineAlignment.Baseline,
            Language = format.Language,
            LineHeight = rule switch
            {
                // Word's single spacing is the font's own line height; Layout's multiples are of the font size.
                LineSpacingRule.Multiple => LineHeight.Multiple(spacing * natural / format.FontSize),
                LineSpacingRule.Exact => LineHeight.Points(spacing),
                _ => LineHeight.Points(Math.Max(spacing, natural)),
            },
        };
        _styles[key] = style;
        return style;
    }
}

using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Xfa.Layout;

/// <summary>
/// The fonts of a conversion: the fonts the PDF file embeds for the form, then the options' collection, a generic family
/// for typefaces neither has (by name: serif, monospace, else sans-serif), the fallback families, and the bundled
/// Liberation Sans last.
/// </summary>
internal sealed class XfaFonts
{
    private static readonly string[] SerifNames =
        ["Times", "Minion", "Georgia", "Garamond", "Palatino", "Book Antiqua", "Century", "Cambria", "Baskerville", "Bodoni", "Serif"];

    private static readonly string[] MonospaceNames = ["Courier", "Consolas", "Lucida Console", "Mono", "Letter Gothic", "OCR"];

    private readonly FontCollection _embedded = new();
    private readonly FontCollection _collection;
    private readonly IReadOnlyList<string> _fallbacks;
    private readonly Action<XfaDiagnosticKind, string>? _report;
    private readonly Dictionary<(string, bool, bool), PdfFont[]> _cache = [];
    private readonly bool _hasEmbedded;

    public XfaFonts(XfaConverterOptions options, IReadOnlyList<PdfFont> embedded, Action<XfaDiagnosticKind, string>? report)
    {
        _collection = options.Fonts ?? BundledFonts.CreateCollection();
        _fallbacks = options.FallbackFontFamilies;
        _report = report;
        foreach (var font in embedded)
        {
            _embedded.AddFont(font);
            _hasEmbedded = true;
        }
    }

    /// <summary>The fonts for a typeface and style: the typeface (or its substitute), then the fallbacks.</summary>
    public PdfFont[] Get(string typeface, bool bold, bool italic)
    {
        var key = (typeface, bold, italic);
        if (_cache.TryGetValue(key, out var fonts))
        {
            return fonts;
        }

        var weight = bold ? 700 : 400;
        PdfFont? own = null;
        foreach (var name in Names(typeface))
        {
            own = (_hasEmbedded ? Exact(_embedded.Find(name, weight, italic), bold, italic) : null) ?? _collection.Find(name, weight, italic);
            if (own is not null)
            {
                break;
            }
        }

        if (own is null)
        {
            _report?.Invoke(XfaDiagnosticKind.FontSubstituted, $"The typeface '{typeface}' is not available; a {Generic(typeface)} font is used instead.");
        }

        var families = new List<string> { Generic(typeface) };
        families.AddRange(_fallbacks);
        fonts =
        [
            .. new[] { own }
                .Concat(families.Select(f => _collection.Find(f, weight, italic)))
                .Append(BundledFonts.CreateCollection().Find("Liberation Sans", weight, italic))
                .OfType<PdfFont>()
                .Distinct(),
        ];
        _cache[key] = fonts;
        return fonts;
    }

    /// <summary>
    /// The names a typeface is looked for by: as written, then its family as PostScript names write it without the
    /// suffixes (<c>ArialMT</c>, <c>TimesNewRomanPSMT</c>, <c>Arial-BoldMT</c>) and with spaces between its words.
    /// </summary>
    internal static IEnumerable<string> Names(string typeface)
    {
        yield return typeface;
        var family = typeface.Split('-')[0];
        foreach (var suffix in new[] { "PSMT", "MT", "PS" })
        {
            if (family.Length > suffix.Length && family.EndsWith(suffix, StringComparison.Ordinal))
            {
                family = family[..^suffix.Length];
                break;
            }
        }

        if (family != typeface)
        {
            yield return family;
        }

        var spaced = new System.Text.StringBuilder();
        for (var i = 0; i < family.Length; i++)
        {
            if (i > 0 && char.IsUpper(family[i]) && char.IsLower(family[i - 1]))
            {
                spaced.Append(' ');
            }

            spaced.Append(family[i]);
        }

        if (spaced.ToString() != family)
        {
            yield return spaced.ToString();
        }
    }

    /// <summary>An embedded font only if it has the style asked for (a regular face does not stand in for a bold one).</summary>
    private static PdfFont? Exact(PdfFont? font, bool bold, bool italic) =>
        font is not null && (font.Weight >= 600) == bold && font.IsItalic == italic ? font : null;

    /// <summary>The generic family a typeface belongs to, by its name.</summary>
    internal static string Generic(string typeface) =>
        MonospaceNames.Any(n => typeface.Contains(n, StringComparison.OrdinalIgnoreCase)) ? "monospace"
        : SerifNames.Any(n => typeface.Contains(n, StringComparison.OrdinalIgnoreCase)) && !typeface.Contains("Sans", StringComparison.OrdinalIgnoreCase) ? "serif"
        : "sans-serif";
}

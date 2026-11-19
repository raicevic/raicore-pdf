using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Slides.Rendering;

/// <summary>
/// The fonts of a conversion: a presentation's font families found in the options' collection, with metric-compatible
/// substitutes, a generic family, the fallback families and the bundled Liberation Sans after them.
/// </summary>
internal sealed class SlideFonts
{
    /// <summary>Metric-compatible substitutes of common Office fonts (not in the collections' own substitutes).</summary>
    private static readonly Dictionary<string, string[]> Substitutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Calibri"] = ["Carlito"],
        ["Calibri Light"] = ["Calibri", "Carlito"],
        ["Cambria"] = ["Caladea"],
        ["Cambria Math"] = ["Cambria", "Caladea"],
        ["Segoe UI"] = ["Selawik", "Open Sans"],
        ["Segoe UI Light"] = ["Segoe UI", "Selawik"],
        ["Aptos"] = ["Aptos Display"],
        ["Aptos Display"] = ["Aptos"],
        ["Arial Narrow"] = ["Liberation Sans Narrow"],
        ["Symbol"] = ["Standard Symbols PS", "DejaVu Sans"],
    };

    private static readonly string[] SerifNames =
        ["Times", "Cambria", "Georgia", "Garamond", "Palatino", "Book Antiqua", "Century", "Constantia", "Baskerville", "Bodoni", "Rockwell", "Serif", "Mincho"];

    private static readonly string[] MonospaceNames = ["Courier", "Consolas", "Lucida Console", "Mono", "Menlo", "Monaco"];

    private readonly FontCollection _collection;
    private readonly IReadOnlyList<string> _fallbacks;
    private readonly Dictionary<(string, string?, string?, bool, bool), PdfFont[]> _cache = [];

    public SlideFonts(PptxConverterOptions options)
    {
        _collection = options.Fonts ?? BundledFonts.CreateCollection();
        _fallbacks = options.FallbackFontFamilies;
    }

    /// <summary>The fonts for a run: its Latin family and style, then its East Asian and complex script families, then the fallbacks.</summary>
    public PdfFont[] Get(string family, string? eastAsian, string? complex, bool bold, bool italic)
    {
        var key = (family, eastAsian, complex, bold, italic);
        if (_cache.TryGetValue(key, out var fonts))
        {
            return fonts;
        }

        var weight = bold ? 700 : 400;
        var families = new List<string> { family };
        if (Substitutes.TryGetValue(family, out var substitutes))
        {
            families.AddRange(substitutes);
        }

        if (eastAsian is { Length: > 0 })
        {
            families.Add(eastAsian);
        }

        if (complex is { Length: > 0 })
        {
            families.Add(complex);
        }

        families.Add(Generic(family));
        families.AddRange(_fallbacks);
        fonts =
        [
            .. families.Select(f => _collection.Find(f, weight, italic))
                .Append(BundledFonts.CreateCollection().Find("Liberation Sans", weight, italic))
                .OfType<PdfFont>()
                .Distinct(),
        ];
        _cache[key] = fonts;
        return fonts;
    }

    /// <summary>The generic family a font family belongs to, by its name.</summary>
    internal static string Generic(string family) =>
        MonospaceNames.Any(n => family.Contains(n, StringComparison.OrdinalIgnoreCase)) ? "monospace"
        : SerifNames.Any(n => family.Contains(n, StringComparison.OrdinalIgnoreCase)) && !family.Contains("Sans", StringComparison.OrdinalIgnoreCase) ? "serif"
        : "sans-serif";
}

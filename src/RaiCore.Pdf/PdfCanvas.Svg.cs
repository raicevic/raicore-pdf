using System.Globalization;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf;

/// <summary>Operations used to draw SVG images into a form XObject.</summary>
public sealed partial class PdfCanvas
{
    private readonly List<(PdfDictionary Pattern, string Name)> _patterns = [];

    /// <summary>
    /// The pattern resources used by this canvas. Only the content of SVG form XObjects uses patterns; page resources
    /// do not include them.
    /// </summary>
    internal IReadOnlyList<(PdfDictionary Pattern, string Name)> Patterns => _patterns;

    /// <summary>Paints fills (and text) with a pattern, such as a gradient (<c>/Pattern cs /P1 scn</c>), at an alpha.</summary>
    internal PdfCanvas SetFillPattern(PdfDictionary pattern, double alpha = 1)
    {
        Op($"/Pattern cs /{UsePattern(pattern)} scn");
        return SetAlpha(alpha, _strokeAlpha);
    }

    /// <summary>Paints strokes with a pattern (<c>/Pattern CS /P1 SCN</c>), at an alpha.</summary>
    internal PdfCanvas SetStrokePattern(PdfDictionary pattern, double alpha = 1)
    {
        Op($"/Pattern CS /{UsePattern(pattern)} SCN");
        return SetAlpha(_fillAlpha, alpha);
    }

    /// <summary>Sets the text rendering mode (<c>Tr</c>): 0 fills glyphs, 1 strokes them, 2 does both.</summary>
    internal PdfCanvas SetTextRenderingMode(int mode) => Op(string.Create(CultureInfo.InvariantCulture, $"{mode} Tr"));

    private string UsePattern(PdfDictionary pattern)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"P{_patterns.Count + 1}");
        _patterns.Add((pattern, name));
        return UseResource(name);
    }
}

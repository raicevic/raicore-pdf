using System.Text.RegularExpressions;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// Reads <c>@page { size; margin }</c> from raw CSS text. AngleSharp drops the <c>size</c> descriptor, so the
/// page rules are scanned directly. Only the plain <c>@page</c> selector is used (no <c>:first</c>/named pages yet).
/// </summary>
internal static partial class PageRules
{
    public static PageSetup Apply(PageSetup setup, IEnumerable<string> styleSheets)
    {
        var size = setup.Size;
        var margins = setup.Margins;
        foreach (var css in styleSheets)
        {
            foreach (Match rule in PageRuleRegex().Matches(StripComments(css)))
            {
                foreach (var declaration in rule.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var colon = declaration.IndexOf(':', StringComparison.Ordinal);
                    if (colon < 0)
                    {
                        continue;
                    }

                    var name = declaration[..colon].Trim().ToUpperInvariant();
                    var value = declaration[(colon + 1)..].Replace("!important", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                    switch (name)
                    {
                        case "SIZE":
                            size = ParseSize(value, size);
                            break;
                        case "MARGIN":
                            margins = ParseMargin(value, margins);
                            break;
                        case "MARGIN-TOP":
                            margins = margins with { Top = Length(value, margins.Top) };
                            break;
                        case "MARGIN-RIGHT":
                            margins = margins with { Right = Length(value, margins.Right) };
                            break;
                        case "MARGIN-BOTTOM":
                            margins = margins with { Bottom = Length(value, margins.Bottom) };
                            break;
                        case "MARGIN-LEFT":
                            margins = margins with { Left = Length(value, margins.Left) };
                            break;
                    }
                }
            }
        }

        return new PageSetup(size, margins);
    }

    internal static PdfPageSize ParseSize(string value, PdfPageSize current)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p.ToUpperInvariant()).ToList();
        var landscape = parts.Remove("LANDSCAPE");
        var portrait = parts.Remove("PORTRAIT");
        PdfPageSize size = current;
        if (parts.Count == 1 && NamedSize(parts[0]) is { } named)
        {
            size = named;
        }
        else if (parts.Count == 1 && parts[0] == "AUTO")
        {
            size = current;
        }
        else if (parts.Count is 1 or 2)
        {
            var width = CssValues.ParseLength(parts[0]);
            var height = parts.Count == 2 ? CssValues.ParseLength(parts[1]) : width;
            if (width.Kind == CssLengthKind.Points && height.Kind == CssLengthKind.Points && width.Value > 0 && height.Value > 0)
            {
                size = new PdfPageSize(width.Value, height.Value);
            }
        }
        else if (parts.Count == 0 && (landscape || portrait))
        {
            size = current;
        }

        if (landscape)
        {
            return size.Landscape;
        }

        return portrait && size.Width > size.Height ? new PdfPageSize(size.Height, size.Width) : size;
    }

    private static PdfPageSize? NamedSize(string name) => name switch
    {
        "A3" => PdfPageSize.A3,
        "A4" => PdfPageSize.A4,
        "A5" => PdfPageSize.A5,
        "B4" => PdfPageSize.FromMillimeters(250, 353),
        "B5" => PdfPageSize.FromMillimeters(176, 250),
        "LETTER" => PdfPageSize.Letter,
        "LEGAL" => PdfPageSize.Legal,
        "LEDGER" => new PdfPageSize(792, 1224),
        _ => null,
    };

    private static Thickness ParseMargin(string value, Thickness current)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => Length(p, double.NaN)).ToArray();
        if (parts.Length == 0 || parts.Any(double.IsNaN))
        {
            return current;
        }

        return parts.Length switch
        {
            1 => Thickness.All(parts[0]),
            2 => new(parts[0], parts[1], parts[0], parts[1]),
            3 => new(parts[0], parts[1], parts[2], parts[1]),
            _ => new(parts[0], parts[1], parts[2], parts[3]),
        };
    }

    private static double Length(string value, double fallback)
    {
        var length = CssValues.ParseLength(value);
        return length.Kind == CssLengthKind.Points ? length.Value : fallback;
    }

    private static string StripComments(string css) => CommentRegex().Replace(css, " ");

    [GeneratedRegex(@"@page\s*(?:[^{]*?)\{([^{}]*)\}", RegexOptions.IgnoreCase)]
    private static partial Regex PageRuleRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();
}

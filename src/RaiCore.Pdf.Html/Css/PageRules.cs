using System.Text.RegularExpressions;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// Page geometry from <c>@page</c> rules (CSS Paged Media): <c>size</c> and <c>margin</c> for all pages, named pages
/// (<c>@page wide</c>, chosen by the <c>page</c> property) and the <c>:first</c>, <c>:left</c>, <c>:right</c> and
/// <c>:blank</c> pseudo-classes, combined like <c>@page wide:first</c> and in selector lists. Rules cascade by page selector
/// specificity, then source order. AngleSharp drops the <c>size</c> descriptor, so the page rules are scanned directly.
/// </summary>
internal sealed partial class PageRules
{
    private readonly PageSetup _defaults;
    private readonly List<CssPageRule> _rules;
    private readonly Dictionary<PageContext, PageSetup> _resolved = [];

    private PageRules(PageSetup defaults, List<CssPageRule> rules)
    {
        _defaults = defaults;
        _rules = rules;
        Setup = Cascade(rules.Where(r => r.Selector.Trim().Length == 0));
        VariesPerPage = rules.Any(r => r.Selector.Trim().Length > 0 && Parse(r.Selector) is not null && r.Declarations.Any(d => IsGeometry(d.Property)));
    }

    /// <summary>The setup from the plain <c>@page</c> rules: of unnamed pages without pseudo-classes.</summary>
    public PageSetup Setup { get; }

    /// <summary>Some pages get another size or other margins than <see cref="Setup"/>.</summary>
    public bool VariesPerPage { get; }

    /// <summary>Reads the <c>@page</c> rules of the style sheets, in order; <paramref name="defaults"/> applies before them.</summary>
    public static PageRules Load(PageSetup defaults, IEnumerable<string> styleSheets) =>
        new(defaults, [.. styleSheets.SelectMany(css => CssRuleScanner.Scan(css).PageRules)]);

    /// <summary>The setup of a page: the declarations of the rules matching it, in cascade order.</summary>
    public PageSetup Resolve(PageContext context)
    {
        if (!_resolved.TryGetValue(context, out var setup))
        {
            _resolved[context] = setup = Cascade(Matching(_rules, context));
        }

        return setup;
    }

    /// <summary>
    /// The page rules that apply to a page, in cascade order: by the specificity of their (best) matching selector, then
    /// in source order, so that later ones override earlier ones. Rules with an invalid selector match nothing.
    /// </summary>
    public static IEnumerable<CssPageRule> Matching(IEnumerable<CssPageRule> rules, PageContext context) =>
        rules.Select((rule, order) => (Rule: rule, Order: order, Specificity: Parse(rule.Selector)?.Where(s => s.Matches(context)).Select(s => (int?)s.Specificity).Max()))
            .Where(r => r.Specificity is not null)
            .OrderBy(r => r.Specificity)
            .ThenBy(r => r.Order)
            .Select(r => r.Rule);

    private static bool IsGeometry(string property) => property is "size" or "margin" or "margin-top" or "margin-right" or "margin-bottom" or "margin-left";

    private PageSetup Cascade(IEnumerable<CssPageRule> rules)
    {
        var size = _defaults.Size;
        var margins = _defaults.Margins;
        foreach (var rule in rules)
        {
            foreach (var (name, value) in rule.Declarations)
            {
                switch (name)
                {
                    case "size":
                        size = ParseSize(value, size);
                        break;
                    case "margin":
                        margins = ParseMargin(value, margins);
                        break;
                    case "margin-top":
                        margins = margins with { Top = Length(value, margins.Top) };
                        break;
                    case "margin-right":
                        margins = margins with { Right = Length(value, margins.Right) };
                        break;
                    case "margin-bottom":
                        margins = margins with { Bottom = Length(value, margins.Bottom) };
                        break;
                    case "margin-left":
                        margins = margins with { Left = Length(value, margins.Left) };
                        break;
                }
            }
        }

        // A page too small for its margins (fuzzing found "size: 5") or of no usable size keeps the content area
        // positive: margins that leave no room are dropped, sizes that are not numbers are not used.
        if (!(size.Width > 0 && size.Height > 0 && double.IsFinite(size.Width) && double.IsFinite(size.Height)))
        {
            size = _defaults.Size;
        }

        if (!(size.Width - margins.Horizontal > 0) || !double.IsFinite(margins.Horizontal))
        {
            margins = margins with { Left = 0, Right = 0 };
        }

        if (!(size.Height - margins.Vertical > 0) || !double.IsFinite(margins.Vertical))
        {
            margins = margins with { Top = 0, Bottom = 0 };
        }

        return new PageSetup(size, margins);
    }

    /// <summary>A page selector list ("" for all pages); <c>null</c> when it is invalid.</summary>
    internal static List<PageSelector>? Parse(string selectorList)
    {
        var selectors = new List<PageSelector>();
        foreach (var part in selectorList.Split(','))
        {
            var match = SelectorRegex().Match(part.Trim());
            if (!match.Success || (part.Trim().Length == 0 && selectorList.Trim().Length > 0))
            {
                return null;
            }

            var selector = new PageSelector(match.Groups["name"].Success ? match.Groups["name"].Value : null);
            foreach (Capture pseudo in match.Groups["pseudo"].Captures)
            {
                switch (pseudo.Value.ToLowerInvariant())
                {
                    case "first":
                        selector = selector with { First = true };
                        break;
                    case "blank":
                        selector = selector with { Blank = true };
                        break;
                    case "left":
                        selector = selector with { Left = true };
                        break;
                    case "right":
                        selector = selector with { Right = true };
                        break;
                    default:
                        return null; // E.g. :nth(), which is not supported: the rule is ignored.
                }
            }

            selectors.Add(selector);
        }

        return selectors;
    }

    [GeneratedRegex(@"^(?<name>-?[A-Za-z_][A-Za-z0-9_-]*)?(?::(?<pseudo>[A-Za-z-]+))*$")]
    private static partial Regex SelectorRegex();

    internal static PdfPageSize ParseSize(string value, PdfPageSize current)
    {
        var parts = CssValues.SplitComponents(value).Select(p => p.ToUpperInvariant()).ToList();
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

    /// <summary>
    /// A page size keyword (upper case): the CSS ones (A3-A5, B4, B5, JIS-B4, JIS-B5, letter, legal, ledger) and the
    /// rest of the ISO 216 A and B series (A0-A10, B0-B10), portrait.
    /// </summary>
    private static PdfPageSize? NamedSize(string name) => name switch
    {
        "A3" => PdfPageSize.A3,
        "A4" => PdfPageSize.A4,
        "A5" => PdfPageSize.A5,
        "JIS-B4" => PdfPageSize.FromMillimeters(257, 364),
        "JIS-B5" => PdfPageSize.FromMillimeters(182, 257),
        "LETTER" => PdfPageSize.Letter,
        "LEGAL" => PdfPageSize.Legal,
        "LEDGER" => new PdfPageSize(792, 1224),
        _ when IsoSizes.TryGetValue(name, out var mm) => PdfPageSize.FromMillimeters(mm.Width, mm.Height),
        _ => null,
    };

    /// <summary>The ISO 216 A and B series in millimeters.</summary>
    private static readonly Dictionary<string, (double Width, double Height)> IsoSizes = new(StringComparer.Ordinal)
    {
        ["A0"] = (841, 1189), ["A1"] = (594, 841), ["A2"] = (420, 594), ["A6"] = (105, 148), ["A7"] = (74, 105),
        ["A8"] = (52, 74), ["A9"] = (37, 52), ["A10"] = (26, 37),
        ["B0"] = (1000, 1414), ["B1"] = (707, 1000), ["B2"] = (500, 707), ["B3"] = (353, 500), ["B4"] = (250, 353),
        ["B5"] = (176, 250), ["B6"] = (125, 176), ["B7"] = (88, 125), ["B8"] = (62, 88), ["B9"] = (44, 62), ["B10"] = (31, 44),
    };

    private static Thickness ParseMargin(string value, Thickness current)
    {
        var parts = CssValues.SplitComponents(value).Select(p => Length(p, double.NaN)).ToArray();
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
}

/// <summary>
/// One page selector (CSS Paged Media section 3.2): an optional page name and pseudo-classes. Its specificity counts the
/// name, then <c>:first</c> and <c>:blank</c>, then <c>:left</c> and <c>:right</c>.
/// </summary>
/// <param name="Name">The page name, or <c>null</c> for any page.</param>
internal sealed record PageSelector(string? Name)
{
    public bool First { get; init; }

    public bool Blank { get; init; }

    public bool Left { get; init; }

    public bool Right { get; init; }

    /// <summary>The specificity (name, first/blank, left/right) as one comparable number.</summary>
    public int Specificity => ((Name is null ? 0 : 1) * 10000) + (((First ? 1 : 0) + (Blank ? 1 : 0)) * 100) + (Left ? 1 : 0) + (Right ? 1 : 0);

    public bool Matches(PageContext page) =>
        (Name is null || string.Equals(Name, page.Name, StringComparison.Ordinal))
        && (!First || page.IsFirst)
        && (!Blank || page.IsBlank)
        && (!Left || page.Side == PageSide.Left)
        && (!Right || page.Side == PageSide.Right);
}

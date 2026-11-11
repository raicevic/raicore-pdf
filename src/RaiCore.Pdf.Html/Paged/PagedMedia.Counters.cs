using System.Text.RegularExpressions;
using AngleSharp.Css;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using RaiCore.Pdf.Html.Boxes;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Html.Paged;

/// <summary>
/// Counters of the page context (CSS Paged Media 3): the <c>page</c> counter, incremented on every page unless the
/// page's <c>counter-increment</c> names it, and named counters, all changed with <c>counter-reset</c>,
/// <c>counter-increment</c> and <c>counter-set</c> in <c>@page</c> rules and shown in margin boxes with
/// <c>counter()</c>. Also the <c>content</c> values of pseudo-elements that AngleSharp drops (those with
/// <c>target-counter()</c>, <c>target-counters()</c>, <c>target-text()</c> or <c>leader()</c>), cascaded here.
/// </summary>
internal sealed partial class PagedMedia
{
    /// <summary>The winning <c>content</c> declaration of each pseudo-element, when some use functions AngleSharp drops.</summary>
    private readonly Dictionary<(IElement Element, string Pseudo), (Priority Specificity, int Order, string Value)> _contents = [];

    private int _contentOrder;

    /// <summary>
    /// The value of the <c>page</c> counter on each page: its number, unless @page rules (for all pages, named
    /// pages, <c>:first</c>, <c>:left</c>, <c>:right</c> or <c>:blank</c>) change it.
    /// </summary>
    public IReadOnlyList<int> PageCounterValues(IReadOnlyList<PageContext> pages)
    {
        var values = new int[pages.Count];
        Dictionary<string, int>? counters = null;
        for (var i = 0; i < pages.Count; i++)
        {
            counters = NextPageCounters(counters, pages[i]);
            values[i] = counters.GetValueOrDefault("page", pages[i].Number);
        }

        return values;
    }

    /// <summary>
    /// The <c>content</c> of an element's <c>::before</c> or <c>::after</c> when it uses functions AngleSharp drops
    /// (and so is not in its computed style); <c>null</c> otherwise.
    /// </summary>
    public string? ContentFor(IElement element, string pseudo) =>
        _contents.TryGetValue((element, pseudo), out var content) && DropsContent(content.Value) ? content.Value : null;

    /// <summary>
    /// The page context's counters on a page: those of the previous page (none before the first), then the page's
    /// resets, increments (the <c>page</c> counter by 1, unless named) and sets, from the @page rules matching it (the
    /// last declaration in cascade order wins). Blank pages count like any other.
    /// </summary>
    private Dictionary<string, int> NextPageCounters(Dictionary<string, int>? previous, PageContext page)
    {
        var counters = previous is null ? new Dictionary<string, int>(StringComparer.Ordinal) : new Dictionary<string, int>(previous, StringComparer.Ordinal);
        var declarations = PageRules.Matching(_pageRules, page).SelectMany(r => r.Declarations).ToList();
        string Last(string property) => declarations.LastOrDefault(d => d.Property == property).Value ?? string.Empty;

        foreach (var (name, value) in BoxBuilder.ParseCounters(Last("counter-reset"), 0))
        {
            counters[name] = value;
        }

        var increments = BoxBuilder.ParseCounters(Last("counter-increment"), 1);
        if (!increments.Any(i => i.Name == "page"))
        {
            increments.Add(("page", 1));
        }

        foreach (var (name, value) in increments)
        {
            counters[name] = counters.GetValueOrDefault(name) + value;
        }

        foreach (var (name, value) in BoxBuilder.ParseCounters(Last("counter-set"), 0))
        {
            counters[name] = value;
        }

        return counters;
    }

    /// <summary>
    /// The text of <c>counter()</c> or <c>counters()</c> in a margin box: <c>pages</c> is the page count, other names
    /// the page context's counters (0 when it has none of that name; document counters are not in scope).
    /// </summary>
    private static string PageCounterText(string name, string style, Dictionary<string, int> counters, PageInfo page) =>
        name.Equals("pages", StringComparison.OrdinalIgnoreCase) ? ListMarkers.FormatNumber(style, page.Count)
        : ListMarkers.FormatNumber(style, counters.GetValueOrDefault(name.Equals("page", StringComparison.OrdinalIgnoreCase) ? "page" : name));

    /// <summary>Whether a <c>content</c> value uses functions AngleSharp drops the whole declaration for.</summary>
    private static bool DropsContent(string value) => DroppedContentRegex().IsMatch(value);

    /// <summary>
    /// Records the <c>content</c> declaration of a style rule for the pseudo-elements its selectors select, if it
    /// wins the cascade (by specificity, then source order) over the ones recorded so far.
    /// </summary>
    private void AddContentRule(IDocument document, CssSelectorParser parser, string selectorList, IReadOnlyList<CssDeclaration> declarations)
    {
        var content = declarations.LastOrDefault(d => d.Property == "content");
        if (content.Property is null)
        {
            return;
        }

        var order = _contentOrder++;
        foreach (var selector in SplitTopLevel(selectorList, ','))
        {
            var match = PseudoSuffixRegex().Match(selector);
            if (!match.Success)
            {
                continue;
            }

            // "a::after" selects a; a bare "::after" (or "li > ::after") selects any element.
            var elementSelector = selector[..match.Index].Trim();
            if (elementSelector.Length == 0 || elementSelector[^1] is '>' or '+' or '~')
            {
                elementSelector += "*";
            }

            if ((parser.ParseSelector(selector.Trim()) ?? parser.ParseSelector(elementSelector)) is not { } parsed)
            {
                continue;
            }

            var pseudo = match.Groups[1].Value.ToLowerInvariant();
            foreach (var element in Select(document, elementSelector))
            {
                if (!_contents.TryGetValue((element, pseudo), out var current) || parsed.Specificity > current.Specificity
                    || (parsed.Specificity == current.Specificity && order > current.Order))
                {
                    _contents[(element, pseudo)] = (parsed.Specificity, order, content.Value);
                }
            }
        }
    }

    [GeneratedRegex(@"(?<![\w-])(?:target-counters?|target-text|leader)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex DroppedContentRegex();

    [GeneratedRegex(@"::?(before|after)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PseudoSuffixRegex();
}

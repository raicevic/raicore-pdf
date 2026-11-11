using System.Text.RegularExpressions;
using AngleSharp.Css;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using RaiCore.Pdf.Html.Css;

namespace RaiCore.Pdf.Html.Paged;

/// <summary>
/// Named pages (CSS Paged Media section 4): the <c>page</c> property (<c>auto</c> or a page name, which
/// <c>@page name</c> rules style). AngleSharp drops it, so it is cascaded here: by selector specificity, then source
/// order, with style attributes winning.
/// </summary>
internal sealed partial class PagedMedia
{
    /// <summary>The winning <c>page</c> declaration per element: its name, or <c>null</c> for <c>auto</c>.</summary>
    private readonly Dictionary<IElement, (Priority Specificity, int Order, string? Name)> _pageNames = [];

    private int _pageNameOrder;

    /// <summary>The specified page name of <paramref name="element"/>; <c>null</c> for <c>auto</c> (its parent's).</summary>
    public string? PageName(IElement element) => _pageNames.TryGetValue(element, out var declaration) ? declaration.Name : null;

    /// <summary>Whether any element has a page name.</summary>
    public bool HasPageNames => _pageNames.Values.Any(d => d.Name is not null);

    /// <summary>Applies the <c>page</c> declarations of a style rule to the elements its selectors match.</summary>
    private void AddPageNameRule(IDocument document, CssSelectorParser parser, string selectorList, IReadOnlyList<CssDeclaration> declarations)
    {
        if (PageNameDeclaration(declarations) is not { } declaration)
        {
            return;
        }

        foreach (var selector in SplitTopLevel(selectorList, ','))
        {
            if (parser.ParseSelector(selector) is not { } parsed)
            {
                continue;
            }

            foreach (var element in Select(document, selector))
            {
                SetPageName(element, parsed.Specificity, declaration);
            }
        }
    }

    /// <summary>The last valid <c>page</c> declaration (a name, or <c>null</c> for <c>auto</c>) with its source order.</summary>
    private (string? Name, int Order)? PageNameDeclaration(IEnumerable<CssDeclaration> declarations)
    {
        (string? Name, int Order)? result = null;
        foreach (var (property, raw) in declarations)
        {
            if (property != "page")
            {
                continue;
            }

            var value = raw.Trim();
            if (value.ToLowerInvariant() is "auto" or "initial" or "unset")
            {
                result = (null, _pageNameOrder++);
            }
            else if (PageNameRegex().IsMatch(value) && value.ToLowerInvariant() is not ("inherit" or "default" or "revert"))
            {
                result = (value, _pageNameOrder++);
            }
        }

        return result;
    }

    private void SetPageName(IElement element, Priority specificity, (string? Name, int Order) declaration)
    {
        if (!_pageNames.TryGetValue(element, out var current) || specificity > current.Specificity
            || (specificity == current.Specificity && declaration.Order > current.Order))
        {
            _pageNames[element] = (specificity, declaration.Order, declaration.Name);
        }
    }

    [GeneratedRegex(@"^-?[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex PageNameRegex();
}

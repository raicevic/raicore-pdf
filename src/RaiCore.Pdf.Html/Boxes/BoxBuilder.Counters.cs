using System.Globalization;
using AngleSharp.Dom;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Html.Paged;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Html.Boxes;

/// <summary>
/// CSS counters (CSS Lists 3): <c>counter-reset</c>, <c>counter-increment</c> and <c>counter-set</c> with nested
/// scopes, the implicit <c>list-item</c> counter of list items (reset by <c>ol</c>/<c>ul</c>/<c>menu</c>, honoring
/// <c>start</c>, <c>reversed</c> and <c>value</c>), <c>counter()</c>/<c>counters()</c> in generated content, and the
/// cross-references of CSS Generated Content for Paged Media: <c>target-counter()</c>, <c>target-counters()</c> and
/// <c>target-text()</c>.
/// </summary>
/// <remarks>
/// Counters are computed once for the whole document, in document order (with <c>::before</c> as the element's
/// first child and <c>::after</c> as its last), before any box is built, so references to counters of later
/// elements resolve directly. Page numbers are only known after pagination: the first layout uses a placeholder
/// and <see cref="ResolvePageReferences"/> gives the pages found for the next one (see <c>HtmlToPdf</c>).
/// </remarks>
internal sealed partial class BoxBuilder
{
    private const string ListItemCounter = "list-item";

    /// <summary>How deep <c>target-text()</c> follows references to generated content that has references itself.</summary>
    private const int MaxReferenceDepth = 4;

    /// <summary>The counters in scope at each element (pseudo "") and pseudo-element ("before", "after").</summary>
    private readonly Dictionary<(IElement Element, string Pseudo), Counter[]> _counterSets = [];

    /// <summary>The page numbers (page counter values) of the elements referenced by id, once a layout found them.</summary>
    private Dictionary<string, int>? _referencePages;

    /// <summary>The page numbers this build used for each referenced id (null: the placeholder or not found).</summary>
    private readonly Dictionary<string, int?> _usedPages = new(StringComparer.Ordinal);

    private bool _countersComputed;

    /// <summary>The content of this build refers to the page of an element (<c>target-counter(..., page)</c>).</summary>
    public bool UsesPageReferences => _usedPages.Count > 0;

    /// <summary>
    /// Gives the pages a layout made, and the pages it put the referenced elements on, for the next build. Returns
    /// <c>true</c> when the build used other values, so the document must be built and laid out again.
    /// </summary>
    public bool ResolvePageReferences(PageMap map)
    {
        var values = _paged?.PageCounterValues(map.Pages);
        var pages = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, page) in map.Destinations)
        {
            pages[id] = values is not null && page <= values.Count ? values[page - 1] : page;
        }

        var changed = _referencePages is null
            ? _usedPages.Count > 0
            : _usedPages.Any(used => used.Value != (pages.TryGetValue(used.Key, out var page) ? page : null));
        _referencePages = pages;
        return changed;
    }

    /// <summary>The text a counter function, cross-reference or attribute stands for at an element; null for other items.</summary>
    internal string? CounterText(IElement element, string pseudo, ContentItem item, int depth = 0) => item switch
    {
        TextItem text => text.Text,
        AttributeItem attribute => element.GetAttribute(attribute.Name) ?? string.Empty,
        CounterItem { Name: "page" or "pages" } => string.Empty, // Page counters belong to the page context (margin boxes).
        CounterItem counter => FormatCounter(CountersAt(element, pseudo), counter.Name, null, counter.Style),
        CountersItem counters => FormatCounter(CountersAt(element, pseudo), counters.Name, counters.Separator, counters.Style),
        TargetCounterItem { Name: "page" } target => TargetPage(element, target.Target, target.Style),
        TargetCountersItem { Name: "page" } target => TargetPage(element, target.Target, target.Style),
        TargetCounterItem target => FindTarget(element, target.Target) is { Element: { } found }
            ? FormatCounter(CountersAt(found, string.Empty), target.Name, null, target.Style)
            : string.Empty,
        TargetCountersItem target => FindTarget(element, target.Target) is { Element: { } found }
            ? FormatCounter(CountersAt(found, string.Empty), target.Name, target.Separator, target.Style)
            : string.Empty,
        TargetTextItem target => FindTarget(element, target.Target) is { Element: { } found } ? TargetText(found, target.Part, depth) : string.Empty,
        _ => null,
    };

    /// <summary>
    /// The value of the innermost counter of a name (or, with a separator, of all of them, outermost first) in a
    /// counter style; 0 when none is in scope.
    /// </summary>
    private static string FormatCounter(Counter[] counters, string name, string? separator, string style)
    {
        if (separator is null)
        {
            return ListMarkers.FormatNumber(style, Array.FindLast(counters, c => c.Name == name)?.Value ?? 0);
        }

        var values = counters.Where(c => c.Name == name).Select(c => ListMarkers.FormatNumber(style, c.Value)).ToList();
        return values.Count == 0 ? ListMarkers.FormatNumber(style, 0) : string.Join(separator, values);
    }

    /// <summary>The value of the <c>list-item</c> counter at a list item, or null when it has none.</summary>
    private int? ListItemValue(IElement item) => Array.FindLast(CountersAt(item, string.Empty), c => c.Name == ListItemCounter)?.Value;

    private Counter[] CountersAt(IElement element, string pseudo)
    {
        ComputeCounters();
        return _counterSets.GetValueOrDefault((element, pseudo), []);
    }

    /// <summary>
    /// The page of the element a reference points to, recording it (the document is laid out again when it
    /// changes). Before the first layout, it is a placeholder: 0 in the counter style.
    /// </summary>
    private string TargetPage(IElement element, TargetReference reference, string style)
    {
        if (FindTarget(element, reference) is not { Element: not null } target)
        {
            return string.Empty;
        }

        int? page = _referencePages is not null && _referencePages.TryGetValue(target.Id, out var found) ? found : null;
        _usedPages[target.Id] = page;
        return page is { } value ? ListMarkers.FormatNumber(style, value)
            : _referencePages is null ? ListMarkers.FormatNumber(style, 0)
            : string.Empty; // Laid out, but not on any page (e.g. display: none).
    }

    /// <summary>The id a reference names (the fragment of a same-document URL) and the element with it, if any.</summary>
    private (string Id, IElement? Element)? FindTarget(IElement element, TargetReference reference)
    {
        var url = (reference.Attribute is { } attribute ? element.GetAttribute(attribute) : reference.Url)?.Trim();
        if (url is null || !url.StartsWith('#') || url.Length < 2)
        {
            return null;
        }

        var id = Uri.UnescapeDataString(url[1..]);
        return (id, Document.GetElementById(id));
    }

    /// <summary>The text of <c>target-text()</c>: the element's text, its first letter, or its <c>::before</c>/<c>::after</c>.</summary>
    private string TargetText(IElement target, string part, int depth) => part switch
    {
        "before" or "after" => PseudoText(target, part, depth + 1),
        "first-letter" => PagedMedia.FirstLetter(PagedMedia.TextOf(target)),
        _ => PagedMedia.TextOf(target),
    };

    /// <summary>
    /// The text of a pseudo-element: what it generated, or (for one not generated yet, further on in the document)
    /// what its content evaluates to. Quotes are left out of the latter.
    /// </summary>
    private string PseudoText(IElement element, string pseudo, int depth)
    {
        if (_generatedText.TryGetValue((element, pseudo), out var generated))
        {
            return generated;
        }

        if (depth > MaxReferenceDepth || PseudoElement(element, pseudo) is not { } generatedContent)
        {
            return string.Empty;
        }

        return string.Concat(generatedContent.Content.Select(item => CounterText(element, pseudo, item, depth) ?? string.Empty));
    }

    /// <summary>Computes the counters of every element and pseudo-element, once.</summary>
    private void ComputeCounters()
    {
        if (_countersComputed)
        {
            return;
        }

        _countersComputed = true;
        if (Document.DocumentElement is { } root)
        {
            Counter[] last = [];
            VisitCounters(root, [], ref last);
        }
    }

    /// <summary>
    /// Computes the counters of an element, then of its pseudo-elements and descendants (CSS Lists 3 section 4.5):
    /// it inherits the counters of its preceding sibling (or its parent), with the values of the element just before
    /// it in document order, then applies its own <c>counter-reset</c>, <c>counter-increment</c> and
    /// <c>counter-set</c>. Elements without a box (<c>display: none</c>) change no counter.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="source">The counters of its preceding sibling, or of its parent if it is the first child.</param>
    /// <param name="last">The counters of the element (or pseudo-element) just before it in document order.</param>
    /// <returns>The element's counters, the source for its next sibling.</returns>
    private Counter[] VisitCounters(IElement element, Counter[] source, ref Counter[] last)
    {
        var style = Style(element);
        var own = Inherit(source, last);
        if (style.Display != CssDisplay.None)
        {
            own = Apply(own, element, element.ParentElement, style, element);
        }

        _counterSets[(element, string.Empty)] = own;
        last = own;
        if (style.Display == CssDisplay.None || NoGeneratedContent.Contains(element.LocalName))
        {
            return own; // No boxes inside (replaced elements such as an inline svg have no counters inside either).
        }

        var childSource = own;
        if (PseudoElement(element, "before") is { } before)
        {
            childSource = last = _counterSets[(element, "before")] = Apply(Inherit(own, last), new object(), element, before.Style, null);
        }

        foreach (var child in element.Children)
        {
            childSource = VisitCounters(child, childSource, ref last);
        }

        if (PseudoElement(element, "after") is { } after)
        {
            last = _counterSets[(element, "after")] = Apply(Inherit(childSource, last), new object(), element, after.Style, null);
        }

        return own;
    }

    /// <summary>The counters of <paramref name="source"/>, with their values in <paramref name="values"/> where it has them.</summary>
    private static Counter[] Inherit(Counter[] source, Counter[] values)
    {
        if (source.Length == 0 || ReferenceEquals(source, values))
        {
            return source;
        }

        var result = new Counter[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            var counter = source[i];
            result[i] = Array.FindLast(values, v => ReferenceEquals(v.Origin, counter.Origin) && v.Name == counter.Name) ?? counter;
        }

        return result;
    }

    /// <summary>
    /// Applies an element's (or pseudo-element's) counter properties: resets, then increments, then sets, with the
    /// implicit ones of lists and list items.
    /// </summary>
    /// <param name="counters">The inherited counters.</param>
    /// <param name="origin">The element or pseudo-element (an identity for the counters it creates).</param>
    /// <param name="parent">Its parent: the counters created by its previous siblings have the same.</param>
    /// <param name="style">Its computed style.</param>
    /// <param name="element">The element, or null for a pseudo-element.</param>
    private static Counter[] Apply(Counter[] counters, object origin, object? parent, ComputedStyle style, IElement? element)
    {
        var resets = ParseCounters(style.CounterReset, 0);
        var increments = ParseCounters(style.CounterIncrement, 1);
        var sets = ParseCounters(style.CounterSet, 0);
        var listReset = element?.LocalName is "ol" or "ul" or "menu" && !resets.Any(r => r.Name == ListItemCounter);
        var listItem = style.Display == CssDisplay.ListItem && !increments.Any(i => i.Name == ListItemCounter);
        if (resets.Count == 0 && increments.Count == 0 && sets.Count == 0 && !listReset && !listItem)
        {
            return counters;
        }

        var list = new List<Counter>(counters);
        foreach (var (name, value) in resets)
        {
            Instantiate(list, name, value, origin, parent);
        }

        if (listReset)
        {
            // The HTML user-agent style sheet's "ol, ul, menu { counter-reset: list-item }", with start and reversed.
            var reversed = IsReversed(element!);
            var start = ListStart(element!);
            Instantiate(list, ListItemCounter, reversed ? start + 1 : start - 1, origin, parent, reversed);
        }

        foreach (var (name, value) in increments)
        {
            Increment(list, name, value, origin, parent);
        }

        if (listItem)
        {
            // List items increment list-item, by -1 in a reversed list.
            var reversed = list.FindLast(c => c.Name == ListItemCounter)?.Reversed == true;
            Increment(list, ListItemCounter, reversed ? -1 : 1, origin, parent);
            if (element?.LocalName == "li" && !sets.Any(s => s.Name == ListItemCounter)
                && int.TryParse(element.GetAttribute("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                sets.Add((ListItemCounter, value)); // HTML: li value="n" is counter-set: list-item n.
            }
        }

        foreach (var (name, value) in sets)
        {
            var index = list.FindLastIndex(c => c.Name == name);
            if (index < 0)
            {
                Instantiate(list, name, value, origin, parent);
            }
            else
            {
                list[index] = list[index] with { Value = value };
            }
        }

        return [.. list];
    }

    /// <summary>
    /// Creates a counter on an element: it replaces the innermost counter of that name when the element itself or a
    /// previous sibling created that one (siblings share a scope), and nests inside it otherwise.
    /// </summary>
    private static void Instantiate(List<Counter> counters, string name, int value, object origin, object? parent, bool reversed = false)
    {
        var index = counters.FindLastIndex(c => c.Name == name);
        if (index >= 0 && (ReferenceEquals(counters[index].Origin, origin) || ReferenceEquals(counters[index].Parent, parent)))
        {
            counters.RemoveAt(index);
        }

        counters.Add(new Counter(name, origin, parent, value, reversed));
    }

    /// <summary>Increments the innermost counter of a name, creating one (at 0) on the element when none is in scope.</summary>
    private static void Increment(List<Counter> counters, string name, int value, object origin, object? parent)
    {
        if (counters.FindLastIndex(c => c.Name == name) < 0)
        {
            Instantiate(counters, name, 0, origin, parent);
        }

        var index = counters.FindLastIndex(c => c.Name == name);
        counters[index] = counters[index] with { Value = unchecked(counters[index].Value + value) };
    }

    /// <summary>Parses a computed counter property: names, each optionally followed by an integer (else <paramref name="defaultValue"/>).</summary>
    internal static List<(string Name, int Value)> ParseCounters(string value, int defaultValue)
    {
        var result = new List<(string, int)>();
        foreach (var token in value.Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            {
                if (result.Count > 0)
                {
                    result[^1] = (result[^1].Item1, number);
                }
            }
            else if (!token.Contains('(', StringComparison.Ordinal) && token.ToLowerInvariant() is not ("none" or "initial" or "inherit" or "unset"))
            {
                result.Add((token, defaultValue));
            }
        }

        return result;
    }

    /// <summary>A counter: its name, the element (or pseudo-element) that created it, that one's parent, and its value.</summary>
    private sealed record Counter(string Name, object Origin, object? Parent, int Value, bool Reversed = false);
}

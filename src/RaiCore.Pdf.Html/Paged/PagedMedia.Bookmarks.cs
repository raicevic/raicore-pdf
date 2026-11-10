using System.Globalization;
using System.Text;
using AngleSharp.Css;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Html.Paged;

/// <summary>
/// PDF bookmarks from CSS Generated Content for Paged Media: <c>bookmark-level</c> (<c>none</c> or a positive
/// integer; the user-agent stylesheet gives <c>h1</c>-<c>h6</c> levels 1-6), <c>bookmark-label</c> (a content list of
/// strings, <c>content()</c> and <c>attr()</c>; by default the element's text) and <c>bookmark-state</c>
/// (<c>open</c> or <c>closed</c>). AngleSharp drops these properties, so they are cascaded here: by selector
/// specificity, then source order, with style attributes winning.
/// </summary>
internal sealed partial class PagedMedia
{
    /// <summary>The winning declaration of each bookmark property, per element.</summary>
    private readonly Dictionary<IElement, Dictionary<string, (Priority Specificity, int Order, string Value)>> _bookmarkProperties = [];

    private int _bookmarkOrder;

    /// <summary>The bookmark for the block of <paramref name="element"/>, or null when it has none.</summary>
    /// <param name="element">The element.</param>
    /// <param name="generatedText">
    /// The text of the element's <c>::before</c> or <c>::after</c> (given "before" or "after"), for
    /// <c>content(before)</c> and <c>content(after)</c>.
    /// </param>
    public Bookmark? BookmarkFor(IElement element, Func<string, string>? generatedText = null)
    {
        if (!_bookmarkProperties.TryGetValue(element, out var properties)
            || !properties.TryGetValue("bookmark-level", out var levelDeclaration)
            || !int.TryParse(levelDeclaration.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var level))
        {
            return null; // bookmark-level: none (the initial value).
        }

        var label = properties.TryGetValue("bookmark-label", out var labelDeclaration) ? ContentValue.Parse(labelDeclaration.Value) : null;
        var title = CollapseWhiteSpace(string.Concat((label ?? [new ElementTextItem()]).Select(item => item switch
        {
            TextItem text => text.Text,
            ElementTextItem { Part: "text" } => TextOf(element),
            ElementTextItem { Part: "first-letter" } => FirstLetter(TextOf(element)),
            ElementTextItem { Part: "before" or "after" } part => generatedText?.Invoke(part.Part) ?? string.Empty,
            AttributeItem attribute => element.GetAttribute(attribute.Name) ?? string.Empty,
            _ => string.Empty, // content(marker) and counters.
        })));
        if (title.Length == 0)
        {
            return null; // An entry without a title (e.g. a heading with only an image) would show as a blank line.
        }

        var closed = properties.TryGetValue("bookmark-state", out var state) && state.Value == "closed";
        return new Bookmark(title, level) { IsOpen = !closed };
    }

    /// <summary>Applies the bookmark declarations of a style rule to the elements its selectors match.</summary>
    private void AddBookmarkRule(IDocument document, CssSelectorParser parser, string selectorList, IReadOnlyList<CssDeclaration> declarations)
    {
        var bookmarkDeclarations = BookmarkDeclarations(declarations);
        if (bookmarkDeclarations.Count == 0)
        {
            return;
        }

        // Each selector in a list has its own specificity.
        foreach (var selector in SplitTopLevel(selectorList, ','))
        {
            if (parser.ParseSelector(selector) is not { } parsed)
            {
                continue;
            }

            foreach (var element in Select(document, selector))
            {
                SetBookmarkProperties(element, parsed.Specificity, bookmarkDeclarations);
            }
        }
    }

    /// <summary>The valid bookmark declarations, normalized (initial values spelled out), each with its source order.</summary>
    private List<(string Property, string Value, int Order)> BookmarkDeclarations(IEnumerable<CssDeclaration> declarations)
    {
        var result = new List<(string, string, int)>();
        foreach (var (property, raw) in declarations)
        {
            var value = raw.Trim();
            var keyword = value.ToLowerInvariant();
            var initial = keyword is "initial" or "unset";
            string? normalized = property switch
            {
                "bookmark-level" when initial || keyword == "none" => "none",
                "bookmark-level" when int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var level) && level > 0 => level.ToString(CultureInfo.InvariantCulture),
                "bookmark-state" when initial => "open",
                "bookmark-state" when keyword is "open" or "closed" => keyword,
                "bookmark-label" when initial => "content(text)",
                "bookmark-label" when ContentValue.Parse(value) is { Count: > 0 } => value,
                _ => null, // Not a bookmark property, or an invalid value (ignored, as CSS does).
            };
            if (normalized is not null)
            {
                result.Add((property, normalized, _bookmarkOrder++));
            }
        }

        return result;
    }

    private void SetBookmarkProperties(IElement element, Priority specificity, List<(string Property, string Value, int Order)> declarations)
    {
        if (!_bookmarkProperties.TryGetValue(element, out var properties))
        {
            _bookmarkProperties[element] = properties = new(StringComparer.Ordinal);
        }

        foreach (var (property, value, order) in declarations)
        {
            if (!properties.TryGetValue(property, out var current) || specificity > current.Specificity
                || (specificity == current.Specificity && order > current.Order))
            {
                properties[property] = (specificity, order, value);
            }
        }
    }

    /// <summary>The element's text, with a space for each line break.</summary>
    private static string TextOf(IElement element)
    {
        var text = new StringBuilder();
        Append(element);
        return CollapseWhiteSpace(text.ToString());

        void Append(INode node)
        {
            foreach (var child in node.ChildNodes)
            {
                switch (child)
                {
                    case IText data:
                        text.Append(data.Data);
                        break;
                    case IElement { LocalName: "br" }:
                        text.Append(' ');
                        break;
                    case IElement { LocalName: "script" or "style" or "template" }:
                        break;
                    case IElement nested:
                        Append(nested);
                        break;
                }
            }
        }
    }

    private static string FirstLetter(string text) =>
        text.Length == 0 ? text : text[..StringInfo.GetNextTextElementLength(text)];
}

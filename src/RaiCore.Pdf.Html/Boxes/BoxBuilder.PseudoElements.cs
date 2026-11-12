using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Html.Paged;

namespace RaiCore.Pdf.Html.Boxes;

/// <summary>
/// Generated content: the CSS <c>::before</c> and <c>::after</c> pseudo-elements (also written <c>:before</c> and
/// <c>:after</c>). AngleSharp cascades a pseudo-element's rules and inherits from its element; the pseudo-element is
/// then materialized as a detached element holding its content (text nodes, <c>img</c> elements for <c>url()</c> and
/// leader elements for <c>leader()</c>; counters and cross-references become text, see BoxBuilder.Counters), which is laid out like any other child: inline (with an inline box for its background, border and
/// padding), as an atomic inline box or as a block, as its <c>display</c> says.
/// </summary>
internal sealed partial class BoxBuilder
{
    /// <summary>The quotes for <c>quotes: auto</c>: typographic double quotes, then single quotes for nested quotes.</summary>
    private static readonly string[] DefaultQuotes = ["\u201C", "\u201D", "\u2018", "\u2019"];

    /// <summary>Replaced and void elements, which have no <c>::before</c>/<c>::after</c> in browsers.</summary>
    private static readonly HashSet<string> NoGeneratedContent = new(StringComparer.Ordinal)
    {
        "img", "svg", "br", "input", "textarea", "select", "iframe", "object", "embed", "video", "audio", "canvas",
    };

    /// <summary>The detached element standing in for a <c>leader()</c> in generated content (with its pattern).</summary>
    private const string LeaderElement = "raicore-leader";

    /// <summary>The detached elements standing in for pseudo-elements (they have no pseudo-elements of their own).</summary>
    private readonly HashSet<IElement> _generated = [];

    /// <summary>The text each pseudo-element generated, for <c>content(before)</c> and <c>content(after)</c> in bookmark labels.</summary>
    private readonly Dictionary<(IElement Element, string Pseudo), string> _generatedText = [];

    /// <summary>The content and style of each pseudo-element asked for (see <see cref="PseudoElement"/>).</summary>
    private readonly Dictionary<(IElement Element, string Pseudo), (IReadOnlyList<ContentItem> Content, ComputedStyle Style, IReadOnlyList<string> Quotes)?> _pseudoElements = [];

    /// <summary>The elements some rule could give a pseudo-element (the others are not asked for its style, which is slow).</summary>
    private HashSet<IElement>? _pseudoCandidates;

    /// <summary>The nesting depth of quotes (CSS <c>open-quote</c>/<c>close-quote</c>), in document order.</summary>
    private int _quoteDepth;

    /// <summary>
    /// The element's child nodes, with its <c>::before</c> first and its <c>::after</c> last. Lazy, so the
    /// <c>::after</c> is generated after the children (quotes nest in document order).
    /// </summary>
    private IEnumerable<INode> WithGeneratedContent(IElement element)
    {
        if (Generate(element, "before") is { } before)
        {
            yield return before;
        }

        foreach (var node in element.ChildNodes)
        {
            yield return node;
        }

        if (Generate(element, "after") is { } after)
        {
            yield return after;
        }
    }

    /// <summary>The text an element's <c>::before</c> or <c>::after</c> generated (empty if none).</summary>
    private string GeneratedText(IElement element, string pseudo) => _generatedText.GetValueOrDefault((element, pseudo), string.Empty);

    /// <summary>
    /// The element standing in for a pseudo-element, or <c>null</c> when it generates no box (<c>content</c> is
    /// <c>none</c> or <c>normal</c>, or <c>display: none</c>).
    /// </summary>
    private IElement? Generate(IElement element, string pseudo)
    {
        if (_generated.Contains(element) || PseudoElement(element, pseudo) is not { } generated)
        {
            return null;
        }

        var (content, style, quotes) = generated;
        var box = Document.CreateElement("raicore-" + pseudo);
        var text = new StringBuilder();
        foreach (var item in content)
        {
            switch (item)
            {
                case QuoteItem quote:
                    text.Append(Quote(quote.Kind, quotes));
                    break;
                case ImageItem image when image.Url.Length > 0:
                    AppendText();
                    var img = Document.CreateElement("img");
                    img.SetAttribute("src", image.Url);
                    _styles[img] = new ComputedStyle(); // Inline, at its natural size.
                    box.AppendChild(img);
                    break;
                case LeaderItem leader when leader.Pattern.Length > 0:
                    AppendText();
                    var filler = Document.CreateElement(LeaderElement);
                    filler.SetAttribute("pattern", leader.Pattern);
                    _styles[filler] = style with { Display = CssDisplay.Inline };
                    box.AppendChild(filler);
                    break;
                default:
                    // Strings, attr(), counters and cross-references.
                    text.Append(CounterText(element, pseudo, item));
                    break;
            }
        }

        AppendText();
        _styles[box] = style;
        _generated.Add(box);
        _generatedText[(element, pseudo)] = box.TextContent;
        return box;

        void AppendText()
        {
            if (text.Length > 0)
            {
                box.AppendChild(Document.CreateTextNode(text.ToString()));
                text.Clear();
            }
        }
    }

    /// <summary>
    /// The content, style (with its display as generated) and quotes of a pseudo-element, or <c>null</c> when it
    /// generates no box. Cached: counters need it before the boxes are built.
    /// </summary>
    private (IReadOnlyList<ContentItem> Content, ComputedStyle Style, IReadOnlyList<string> Quotes)? PseudoElement(IElement element, string pseudo)
    {
        if (_pseudoElements.TryGetValue((element, pseudo), out var cached))
        {
            return cached;
        }

        (IReadOnlyList<ContentItem>, ComputedStyle, IReadOnlyList<string>)? result = null;
        var scanned = _paged?.ContentFor(element, pseudo); // Content values AngleSharp drops (target-counter(), leader()...).
        if (!NoGeneratedContent.Contains(element.LocalName) && (scanned is not null || PseudoCandidates().Contains(element)))
        {
            try
            {
                var css = ComputeStyle(element, "::" + pseudo);
                if (ContentValue.Parse(scanned ?? css.GetPropertyValue("content") ?? string.Empty) is { } content)
                {
                    var style = ComputedStyle.From(css, _rootFontSize, _viewport);
                    if (style.Display != CssDisplay.None)
                    {
                        result = (content, style with { Display = GeneratedDisplay(style.Display) }, Quotes(css.GetPropertyValue("quotes") ?? string.Empty));
                    }
                }
            }
            catch (NullReferenceException)
            {
                // AngleSharp cannot compute the pseudo-element's style (malformed CSS, see ComputeStyle): no content.
            }
        }

        _pseudoElements[(element, pseudo)] = result;
        return result;
    }

    /// <summary>
    /// A pseudo-element is inline, an atomic inline box or a block (or flex container); table and list-item displays
    /// are laid out as blocks, since a pseudo-element has no rows, cells or list marker.
    /// </summary>
    private static CssDisplay GeneratedDisplay(CssDisplay display) => display switch
    {
        CssDisplay.Inline or CssDisplay.InlineBlock or CssDisplay.InlineFlex or CssDisplay.Flex => display,
        CssDisplay.InlineTable => CssDisplay.InlineBlock,
        _ => CssDisplay.Block,
    };

    /// <summary>The quote marks of the <c>quotes</c> property: pairs of open and close quotes, outer first.</summary>
    private static IReadOnlyList<string> Quotes(string value)
    {
        if (value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var strings = ContentValue.Parse(value)?.OfType<TextItem>().Select(t => t.Text).ToList();
        return strings is { Count: > 0 } && strings.Count % 2 == 0 ? strings : DefaultQuotes;
    }

    /// <summary>
    /// The text of <c>open-quote</c>, <c>close-quote</c>, <c>no-open-quote</c> or <c>no-close-quote</c>, updating the
    /// quote depth: each level uses the next pair of quotes (the last pair for deeper levels); a close quote without an
    /// open one generates nothing.
    /// </summary>
    private string Quote(string kind, IReadOnlyList<string> quotes)
    {
        var pairs = quotes.Count / 2;
        if (kind is "open-quote" or "no-open-quote")
        {
            var depth = _quoteDepth++;
            return kind == "open-quote" && pairs > 0 ? quotes[2 * Math.Min(depth, pairs - 1)] : string.Empty;
        }

        if (_quoteDepth == 0)
        {
            return string.Empty;
        }

        _quoteDepth--;
        return kind == "close-quote" && pairs > 0 ? quotes[(2 * Math.Min(_quoteDepth, pairs - 1)) + 1] : string.Empty;
    }

    /// <summary>
    /// The elements matched by a rule for a pseudo-element, with the pseudo-element taken off its selector (a superset:
    /// the cascade decides whether they get one).
    /// </summary>
    private HashSet<IElement> PseudoCandidates()
    {
        if (_pseudoCandidates is null)
        {
            _pseudoCandidates = [];
            foreach (var sheet in Document.StyleSheets.OfType<ICssStyleSheet>())
            {
                AddPseudoCandidates(sheet.Rules, _pseudoCandidates);
            }
        }

        return _pseudoCandidates;
    }

    private void AddPseudoCandidates(ICssRuleList rules, HashSet<IElement> candidates)
    {
        foreach (var rule in rules)
        {
            switch (rule)
            {
                case ICssStyleRule style when style.SelectorText is { } text && PseudoElementRegex().IsMatch(text): // Null when AngleSharp cannot parse it (li::marker).
                    // "p::before" selects p; a bare "::before" (or "ul > ::after") selects any element.
                    var selector = PseudoElementRegex().Replace(
                        text,
                        match => match.Index == 0 || text[match.Index - 1] is ' ' or ',' or '>' or '+' or '~' or '(' ? "*" : string.Empty);
                    try
                    {
                        candidates.UnionWith(Document.QuerySelectorAll(selector));
                    }
                    catch (DomException)
                    {
                        // A selector AngleSharp cannot query matches nothing here.
                    }

                    break;
                case ICssGroupingRule group:
                    AddPseudoCandidates(group.Rules, candidates);
                    break;
            }
        }
    }

    [GeneratedRegex(@"::?(?:before|after)(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex PseudoElementRegex();
}

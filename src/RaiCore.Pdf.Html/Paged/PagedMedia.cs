using System.Text.RegularExpressions;
using AngleSharp.Dom;
using RaiCore.Pdf.Html.Boxes;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Html.Paged;

/// <summary>Marks the block of an element with <c>string-set</c>: the values it assigns.</summary>
internal sealed record StringSetMarker(IReadOnlyList<(string Name, string Value)> Assignments);

/// <summary>Marks where a running element (<c>position: running(name)</c>) is in the flow, with its content.</summary>
internal sealed record RunningMarker(string Name, IReadOnlyList<Block> Blocks);

/// <summary>
/// CSS Paged Media and Generated Content for Paged Media: <c>@page</c> margin boxes (headers and footers) for all,
/// first, left, right, blank and named pages, page counters, running strings (<c>string-set</c>/<c>string()</c>), running
/// elements (<c>position: running()</c>/<c>element()</c>) and bookmarks (<c>bookmark-level</c> and friends).
/// </summary>
internal sealed partial class PagedMedia
{
    private static readonly string[] BoxNames =
    [
        "top-left-corner", "top-left", "top-center", "top-right", "top-right-corner",
        "bottom-left-corner", "bottom-left", "bottom-center", "bottom-right", "bottom-right-corner",
    ];

    private readonly List<CssPageRule> _pageRules;
    private readonly Dictionary<IElement, Dictionary<string, IReadOnlyList<ContentItem>>> _stringSets = [];
    private readonly Dictionary<IElement, string> _running = [];

    private PagedMedia(List<CssPageRule> pageRules) => _pageRules = pageRules;

    public bool HasMarginBoxes => _pageRules.Any(r => r.MarginBoxes.Count > 0);

    /// <summary>Reads @page rules, string-set, running and bookmark declarations from the document's CSS.</summary>
    public static PagedMedia Load(IDocument document, IEnumerable<string> styleSheets)
    {
        var pageRules = new List<CssPageRule>();
        var styleRules = new List<CssStyleRule>();
        foreach (var css in styleSheets)
        {
            var (styles, pages) = CssRuleScanner.Scan(css);
            styleRules.AddRange(styles);
            pageRules.AddRange(pages);
        }

        var media = new PagedMedia(pageRules);
        var selectorParser = new AngleSharp.Css.Parser.CssSelectorParser();
        var scanContent = styleRules.Any(rule => rule.Declarations.Any(d => d.Property == "content" && DropsContent(d.Value)));
        foreach (var rule in styleRules)
        {
            media.AddBookmarkRule(document, selectorParser, rule.Selector, rule.Declarations);
            media.AddPageNameRule(document, selectorParser, rule.Selector, rule.Declarations);
            if (scanContent)
            {
                media.AddContentRule(document, selectorParser, rule.Selector, rule.Declarations);
            }

            foreach (var declaration in rule.Declarations)
            {
                if (declaration.Property == "string-set")
                {
                    foreach (var element in Select(document, rule.Selector))
                    {
                        media.AddStringSet(element, declaration.Value);
                    }
                }
                else if (declaration.Property == "position" && RunningRegex().Match(declaration.Value) is { Success: true } running)
                {
                    foreach (var element in Select(document, rule.Selector))
                    {
                        media._running[element] = running.Groups[1].Value;
                    }
                }
            }
        }

        foreach (var element in document.QuerySelectorAll("[style]"))
        {
            var inline = CssRuleScanner.ParseDeclarations(element.GetAttribute("style")!);
            media.SetBookmarkProperties(element, AngleSharp.Css.Priority.Inline, media.BookmarkDeclarations(inline));
            if (media.PageNameDeclaration(inline) is { } pageName)
            {
                media.SetPageName(element, AngleSharp.Css.Priority.Inline, pageName);
            }

            foreach (var declaration in inline)
            {
                if (declaration.Property == "string-set")
                {
                    media.AddStringSet(element, declaration.Value);
                }
                else if (declaration.Property == "position" && RunningRegex().Match(declaration.Value) is { Success: true } running)
                {
                    media._running[element] = running.Groups[1].Value;
                }
            }
        }

        return media;
    }

    /// <summary>The name of the running element <paramref name="element"/> is, or null.</summary>
    public string? RunningName(IElement element) => _running.GetValueOrDefault(element);

    /// <summary>The marker to put on the block of <paramref name="element"/> if it assigns running strings.</summary>
    /// <param name="element">The element.</param>
    /// <param name="resolve">The text of other items, such as counters (null if unsupported).</param>
    public StringSetMarker? MarkerFor(IElement element, Func<ContentItem, string?>? resolve = null)
    {
        if (!_stringSets.TryGetValue(element, out var sets))
        {
            return null;
        }

        var assignments = new List<(string, string)>();
        foreach (var (name, items) in sets)
        {
            var value = string.Concat(items.Select(item => item switch
            {
                TextItem text => text.Text,
                ElementTextItem => CollapseWhiteSpace(element.TextContent),
                AttributeItem attribute => element.GetAttribute(attribute.Name) ?? string.Empty,
                _ => resolve?.Invoke(item) ?? string.Empty,
            }));
            assignments.Add((name, value));
        }

        return new StringSetMarker(assignments);
    }

    /// <summary>A page decorator that draws the margin boxes of each page.</summary>
    public Action<PdfCanvas, PageInfo> CreateDecorator(BoxBuilder builder, ComputedStyle rootStyle)
    {
        var entryStrings = new Dictionary<string, string>(StringComparer.Ordinal);
        var entryElements = new Dictionary<string, IReadOnlyList<Block>>(StringComparer.Ordinal);
        Dictionary<string, int>? pageCounters = null;
        return (canvas, page) =>
        {
            if (page.Number == 1)
            {
                entryStrings.Clear();
                entryElements.Clear();
            }

            var assignments = page.Markers.OfType<StringSetMarker>().SelectMany(m => m.Assignments).ToList();
            var runnings = page.Markers.OfType<RunningMarker>().ToList();

            string ResolveString(StringItem item)
            {
                var onPage = assignments.Where(a => a.Name == item.Name).Select(a => a.Value).ToList();
                var entry = entryStrings.GetValueOrDefault(item.Name, string.Empty);
                return item.Policy switch
                {
                    "last" => onPage.Count > 0 ? onPage[^1] : entry,
                    "start" => entry.Length > 0 || onPage.Count == 0 ? entry : onPage[0],
                    "first-except" => onPage.Count > 0 ? string.Empty : entry,
                    _ => onPage.Count > 0 ? onPage[0] : entry,
                };
            }

            IReadOnlyList<Block>? ResolveElement(ElementItem item)
            {
                var onPage = runnings.Where(r => r.Name == item.Name).Select(r => r.Blocks).ToList();
                var entry = entryElements.GetValueOrDefault(item.Name);
                return item.Policy switch
                {
                    "last" => onPage.Count > 0 ? onPage[^1] : entry,
                    "first-except" => onPage.Count > 0 ? null : entry,
                    "start" => entry ?? (onPage.Count > 0 ? onPage[0] : null),
                    _ => onPage.Count > 0 ? onPage[0] : entry,
                };
            }

            pageCounters = NextPageCounters(page.Number == 1 ? null : pageCounters, page.Context);
            var counters = pageCounters;
            var boxes = BoxesFor(page.Context);
            foreach (var (name, declarations) in boxes)
            {
                var content = declarations.LastOrDefault(d => d.Property == "content") is { Property: not null } d ? ContentValue.Parse(d.Value) : null;
                if (content is null || content.Count == 0)
                {
                    continue;
                }

                var style = BoxStyle(rootStyle, declarations, name);
                IReadOnlyList<Block>? blocks;
                if (content.OfType<ElementItem>().FirstOrDefault() is { } element)
                {
                    blocks = ResolveElement(element);
                }
                else
                {
                    var text = string.Concat(content.Select(item => item switch
                    {
                        TextItem t => t.Text,
                        CounterItem c => PageCounterText(c.Name, c.Style, counters, page),
                        CountersItem c => PageCounterText(c.Name, c.Style, counters, page),
                        StringItem s => ResolveString(s),
                        _ => string.Empty,
                    }));
                    blocks = text.Length == 0
                        ? null
                        : [new ParagraphBlock(new Paragraph(builder.TextStyleFor(style)) { Alignment = style.TextAlign }.Add(text, builder.TextStyleFor(style)))];
                }

                if (blocks is null || blocks.Count == 0)
                {
                    continue;
                }

                var (x, top, width, height) = BoxRect(name, page.Setup, boxes.Keys);
                var contentHeight = FlowDocument.MeasureBlocks(blocks, width);
                var y = style.VerticalAlign switch
                {
                    "top" => top,
                    "bottom" => top + height - contentHeight,
                    _ => top + ((height - contentHeight) / 2),
                };
                FlowDocument.DrawBlocks(canvas, blocks, x, page.Setup.Size.Height - y, width);
            }

            foreach (var (name, value) in assignments)
            {
                entryStrings[name] = value;
            }

            foreach (var running in runnings)
            {
                entryElements[running.Name] = running.Blocks;
            }
        };
    }

    /// <summary>
    /// The margin boxes of a page: from the page rules matching it (by name, :first, :blank, :left, :right), each
    /// overriding the declarations of earlier ones (CSS cascade by page selector specificity, then source order).
    /// </summary>
    private Dictionary<string, List<CssDeclaration>> BoxesFor(PageContext page)
    {
        var applicable = PageRules.Matching(_pageRules, page);
        var boxes = new Dictionary<string, List<CssDeclaration>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in applicable)
        {
            foreach (var (name, declarations) in rule.MarginBoxes)
            {
                if (!BoxNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!boxes.TryGetValue(name, out var list))
                {
                    boxes[name] = list = [];
                }

                list.AddRange(declarations);
            }
        }

        return boxes;
    }

    /// <summary>
    /// The rectangle of a margin box (top measured from the top of the page, in the page's own size and margins): top boxes fill the top margin and bottom
    /// boxes the bottom margin; across the content width, left/center/right share it in thirds when a center box is
    /// used, otherwise left and right share it in halves. Corner boxes fill the page margin corners.
    /// </summary>
    private static (double X, double Top, double Width, double Height) BoxRect(string name, PageSetup setup, IEnumerable<string> present)
    {
        var (width0, height0) = (setup.Size.Width, setup.Size.Height); // Percentages: of the page's width or height.
        var margins = (Top: setup.Margins.Top.Resolve(height0), Right: setup.Margins.Right.Resolve(width0),
            Bottom: setup.Margins.Bottom.Resolve(height0), Left: setup.Margins.Left.Resolve(width0));
        var isTop = name.StartsWith("top", StringComparison.OrdinalIgnoreCase);
        var top = isTop ? 0 : setup.Size.Height - margins.Bottom;
        var height = isTop ? margins.Top : margins.Bottom;
        var prefix = isTop ? "top" : "bottom";
        var hasCenter = present.Contains($"{prefix}-center", StringComparer.OrdinalIgnoreCase);
        var x0 = margins.Left;
        var width = setup.ContentWidth;
        var position = name[(prefix.Length + 1)..].ToLowerInvariant();
        return position switch
        {
            "left-corner" => (0, top, margins.Left, height),
            "right-corner" => (setup.Size.Width - margins.Right, top, margins.Right, height),
            "center" => (x0 + (width / 3), top, width / 3, height),
            "left" => (x0, top, hasCenter ? width / 3 : width / 2, height),
            _ => hasCenter ? (x0 + (2 * width / 3), top, width / 3, height) : (x0 + (width / 2), top, width / 2, height),
        };
    }

    /// <summary>The style of a margin box: the root element's font and color, overridden by the box's declarations.</summary>
    private static ComputedStyle BoxStyle(ComputedStyle root, List<CssDeclaration> declarations, string name)
    {
        var style = root with
        {
            TextAlign = name.EndsWith("center", StringComparison.OrdinalIgnoreCase) ? TextAlignment.Center
                : name.EndsWith("right", StringComparison.OrdinalIgnoreCase) || name.Equals("top-left-corner", StringComparison.OrdinalIgnoreCase) || name.Equals("bottom-left-corner", StringComparison.OrdinalIgnoreCase) ? TextAlignment.Right
                : TextAlignment.Left,
            VerticalAlign = "middle",
            LineHeight = LineHeight.Normal,
        };
        var lineHeight = "normal"; // Resolved last: an em or percentage refers to the box's own font size.
        foreach (var (property, value) in declarations)
        {
            if (property == "font")
            {
                // The shorthand sets every font longhand; those it leaves out go back to their initial values.
                if (FontLonghands(value) is { } longhands)
                {
                    foreach (var (longhand, longhandValue) in longhands)
                    {
                        Apply(longhand, longhandValue);
                    }
                }
            }
            else
            {
                Apply(property, value);
            }
        }

        return style with { LineHeight = ComputedStyle.ParseLineHeight(lineHeight, style.FontSize, root.FontSize, default) };

        void Apply(string property, string value)
        {
            var lower = value.Trim().ToLowerInvariant();
            style = property switch
            {
                "font-size" => FontSizeKeyword(lower, root.FontSize) is { } keyword ? style with { FontSize = keyword } : CssValues.ParseLength(lower, root.FontSize, root.FontSize) switch
                {
                    { Kind: CssLengthKind.Points } points => style with { FontSize = points.Value },
                    { Kind: CssLengthKind.Percent } percent => style with { FontSize = root.FontSize * percent.Value / 100 },
                    { Kind: CssLengthKind.Calc } calc => style with { FontSize = Math.Max(0, calc.Resolve(root.FontSize)) },
                    _ => style,
                },
                "font-family" => style with { FontFamilies = CssValues.ParseFontFamilies(value) },
                "font-weight" => style with { FontWeight = CssValues.ParseFontWeight(lower) },
                "font-style" => style with { Italic = lower is "italic" or "oblique" },
                "color" => CssValues.ParseColor(lower) is { } color ? style with { Color = color } : style,
                "text-align" => style with { TextAlign = lower switch { "center" => TextAlignment.Center, "right" or "end" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => TextAlignment.Left } },
                "vertical-align" => style with { VerticalAlign = lower },
                _ => style,
            };
            if (property == "line-height")
            {
                lineHeight = lower;
            }
        }
    }

    /// <summary>
    /// The longhands of a <c>font</c> shorthand, with initial values for those it leaves out, or <c>null</c> when the
    /// value is invalid, a system font (<c>caption</c>, ...) or holds variables.
    /// </summary>
    internal static List<(string Property, string Value)>? FontLonghands(string value)
    {
        var trimmed = value.Trim();
        try
        {
            var declaration = new AngleSharp.Css.Parser.CssParser().ParseDeclaration($"font: {trimmed}");
            if (declaration is null)
            {
                return null;
            }

            var size = declaration.GetPropertyValue("font-size");
            var family = declaration.GetPropertyValue("font-family");
            if (string.IsNullOrEmpty(size) || string.IsNullOrEmpty(family))
            {
                // AngleSharp reads a system font (caption, menu, ...) as a family name without a size.
                return null;
            }

            string Or(string longhand, string initial) => declaration.GetPropertyValue(longhand) is { Length: > 0 } v ? v : initial;
            return
            [
                ("font-style", Or("font-style", "normal")), ("font-weight", Or("font-weight", "normal")), ("font-size", size),
                ("line-height", Or("line-height", "normal")), ("font-family", family),
            ];
        }
        catch (NullReferenceException)
        {
            return null; // AngleSharp throws on some invalid values (see CssDiagnostics.IsAccepted).
        }
    }

    /// <summary>The size of a font-size keyword in points (<c>small</c>, <c>larger</c>, ...), like Chrome's, or <c>null</c>.</summary>
    internal static double? FontSizeKeyword(string keyword, double parentSize) => keyword switch
    {
        "xx-small" => 6.75,
        "x-small" => 7.5,
        "small" => 9.75,
        "medium" => 12,
        "large" => 13.5,
        "x-large" => 18,
        "xx-large" => 24,
        "xxx-large" => 36,
        "larger" => parentSize * 1.2,
        "smaller" => parentSize / 1.2,
        _ => null,
    };

    private void AddStringSet(IElement element, string value)
    {
        if (value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            _stringSets.Remove(element);
            return;
        }

        if (!_stringSets.TryGetValue(element, out var sets))
        {
            _stringSets[element] = sets = new Dictionary<string, IReadOnlyList<ContentItem>>(StringComparer.Ordinal);
        }

        // "name content-list [, name content-list]*"
        foreach (var assignment in SplitTopLevel(value, ','))
        {
            var trimmed = assignment.Trim();
            var space = trimmed.IndexOfAny([' ', '\t']);
            if (space <= 0)
            {
                continue;
            }

            sets[trimmed[..space]] = ContentValue.Parse(trimmed[(space + 1)..]) ?? [];
        }
    }

    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var depth = 0;
        var start = 0;
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == separator && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }

    private static List<IElement> Select(IDocument document, string selector)
    {
        // CSS nesting (#149) is not supported, and AngleSharp throws NullReferenceException matching a nesting selector
        // (found by fuzzing): such rules match nothing, as in CssDiagnostics.
        if (selector.Contains('&', StringComparison.Ordinal))
        {
            return [];
        }

        try
        {
            return document.QuerySelectorAll(selector).ToList();
        }
        catch (DomException)
        {
            return []; // Selectors AngleSharp does not understand (e.g. vendor pseudo-classes) match nothing.
        }
    }

    private static string CollapseWhiteSpace(string text) => WhiteSpaceRegex().Replace(text, " ").Trim();

    [GeneratedRegex(@"running\(\s*([^)\s]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex RunningRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpaceRegex();
}

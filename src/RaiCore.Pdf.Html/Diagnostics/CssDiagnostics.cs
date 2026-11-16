using System.Text.RegularExpressions;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using RaiCore.Pdf.Html.Css;

namespace RaiCore.Pdf.Html.Diagnostics;

/// <summary>
/// Reports the CSS of a document that the converter does not support: properties it ignores (or that have no effect in
/// a PDF), values AngleSharp rejects or that are laid out differently, selectors it cannot match and at-rules it skips.
/// A declaration is reported only where its rule applies to an element, at those elements; at-rules have no element.
/// </summary>
internal static partial class CssDiagnostics
{
    /// <summary>Pseudo-elements the converter generates boxes for.</summary>
    private static readonly HashSet<string> SupportedPseudoElements = new(StringComparer.OrdinalIgnoreCase) { "before", "after" };

    /// <summary>Pseudo-elements that only exist on screen or in forms.</summary>
    private static readonly HashSet<string> ScreenPseudoElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "selection", "placeholder", "backdrop", "file-selector-button", "cue", "target-text", "spelling-error", "grammar-error",
    };

    /// <summary>At-rules the converter reads.</summary>
    private static readonly HashSet<string> SupportedAtRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "media", "supports", "page", "font-face", "charset", "namespace",

        // The margin boxes of @page.
        "top-left-corner", "top-left", "top-center", "top-right", "top-right-corner", "left-top", "left-middle", "left-bottom",
        "right-top", "right-middle", "right-bottom", "bottom-left-corner", "bottom-left", "bottom-center", "bottom-right",
        "bottom-right-corner",
    };

    /// <summary>
    /// Checks the document's style sheets (the user agent's left out) and <c>style</c> attributes. Does nothing unless
    /// <paramref name="diagnostics"/> collects.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="styleSheets">The style sheets for print, as written.</param>
    /// <param name="originalInlineStyles">The <c>style</c> attributes as written, of the elements whose attribute was rewritten.</param>
    /// <param name="diagnostics">Where to report.</param>
    public static void Check(IDocument document, IEnumerable<string> styleSheets, IReadOnlyDictionary<IElement, string> originalInlineStyles, DiagnosticCollector diagnostics)
    {
        if (!diagnostics.IsEnabled)
        {
            return;
        }

        var parser = new CssParser();
        foreach (var css in styleSheets)
        {
            CheckAtRules(css, diagnostics);
            foreach (var rule in CssRuleScanner.Scan(css).StyleRules)
            {
                var elements = Match(document, rule.Selector, diagnostics);
                if (elements.Count > 0)
                {
                    CheckDeclarations(rule.Declarations, elements, parser, diagnostics);
                }
            }
        }

        foreach (var element in document.QuerySelectorAll("[style]"))
        {
            var style = originalInlineStyles.TryGetValue(element, out var original) ? original : element.GetAttribute("style")!;
            CheckDeclarations(CssRuleScanner.ParseDeclarations(style), [element], parser, diagnostics);
        }
    }

    /// <summary>
    /// The elements a selector list matches. Selectors with a pseudo-element the converter does not generate, and
    /// selectors AngleSharp cannot match, are reported and match nothing.
    /// </summary>
    private static List<IElement> Match(IDocument document, string selectorList, DiagnosticCollector diagnostics)
    {
        var elements = new List<IElement>();
        foreach (var selector in SplitSelectors(selectorList))
        {
            var query = selector;
            var skip = false;
            foreach (Match pseudo in PseudoElementRegex().Matches(selector))
            {
                var name = pseudo.Groups["name"].Value;
                var isElement = pseudo.Groups["colons"].Value == "::" || SupportedPseudoElements.Contains(name) || name is "first-line" or "first-letter";
                if (!isElement)
                {
                    continue; // A pseudo-class (:hover, :nth-child), which AngleSharp matches or not.
                }

                if (SupportedPseudoElements.Contains(name))
                {
                    query = query.Replace(pseudo.Value, string.Empty, StringComparison.Ordinal);
                    continue;
                }

                var screen = ScreenPseudoElements.Contains(name) || name.StartsWith('-');
                diagnostics.Report(
                    HtmlDiagnosticKind.UnsupportedSelector,
                    screen ? HtmlDiagnosticSeverity.Info : HtmlDiagnosticSeverity.Warning,
                    "::" + name,
                    screen
                        ? $"The ::{name} pseudo-element has no effect in a PDF."
                        : $"The ::{name} pseudo-element is not supported: rules for it are ignored.");
                skip = true;
            }

            if (skip)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                query = "*";
            }

            try
            {
                elements.AddRange(document.QuerySelectorAll(query));
            }
            catch (DomException)
            {
                diagnostics.Report(
                    HtmlDiagnosticKind.UnsupportedSelector,
                    HtmlDiagnosticSeverity.Warning,
                    Shorten(selector.Trim()),
                    "The selector is not supported: the rule is ignored.");
            }
        }

        return elements;
    }

    private static void CheckDeclarations(IReadOnlyList<CssDeclaration> declarations, IReadOnlyList<IElement> elements, CssParser parser, DiagnosticCollector diagnostics)
    {
        foreach (var (property, rawValue) in declarations)
        {
            if (property.StartsWith("--", StringComparison.Ordinal))
            {
                continue; // A custom property: whatever it holds is checked where it is used.
            }

            var value = ImportantRegex().Replace(rawValue, string.Empty).Trim();
            if (CssSupport.NoEffectInPrint.Contains(property) || (CssSupport.IsVendorPrefixed(property) && !CssSupport.Supported.Contains(property)))
            {
                var vendor = CssSupport.IsVendorPrefixed(property) && !CssSupport.NoEffectInPrint.Contains(property);
                Report(HtmlDiagnosticKind.UnsupportedProperty, HtmlDiagnosticSeverity.Info, property,
                    vendor ? $"The vendor-prefixed property {property} is ignored." : $"The property {property} has no effect in a PDF.");
                continue;
            }

            if (!CssSupport.Supported.Contains(property))
            {
                Report(HtmlDiagnosticKind.UnsupportedProperty, HtmlDiagnosticSeverity.Warning, property, $"The property {property} is not supported: it is ignored.");
                continue;
            }

            var keyword = value.ToLowerInvariant();
            if (CssSupport.ValueIssue(property, keyword) is { } issue)
            {
                Report(HtmlDiagnosticKind.UnsupportedValue, HtmlDiagnosticSeverity.Warning, $"{property}: {keyword}", issue);
                continue;
            }

            // Values with variables are only known per element; properties read from the CSS text AngleSharp does not know.
            if (CssSupport.ReadFromText.Contains(property) || value.Contains("var(", StringComparison.OrdinalIgnoreCase) || value.Length == 0)
            {
                continue;
            }

            // A text-decoration shorthand with a thickness is split into longhands before AngleSharp sees it.
            var accepted = property == "text-decoration" && CssTextDecoration.Longhands(value) is { } longhands
                ? longhands.TrueForAll(l => IsAccepted(parser, l.Property, l.Value))
                : IsAccepted(parser, property, value);
            if (!accepted)
            {
                Report(HtmlDiagnosticKind.UnsupportedValue, HtmlDiagnosticSeverity.Warning, Shorten($"{property}: {value}"),
                    $"The value of {property} is not valid or not supported: the declaration is ignored.");
            }
        }

        void Report(HtmlDiagnosticKind kind, HtmlDiagnosticSeverity severity, string subject, string message)
        {
            foreach (var element in elements)
            {
                diagnostics.Report(kind, severity, subject, message, element);
            }
        }
    }

    /// <summary>Whether AngleSharp keeps the declaration; one it fails on (it throws on some malformed values) is not kept.</summary>
    private static bool IsAccepted(CssParser parser, string property, string value)
    {
        try
        {
            return parser.ParseDeclaration($"{property}: {value}") is { Length: > 0 };
        }
        catch (NullReferenceException)
        {
            return false;
        }
    }

    /// <summary>At-rules the converter skips: <c>@import</c>, cascade layers, container queries, animations, ...</summary>
    private static void CheckAtRules(string css, DiagnosticCollector diagnostics)
    {
        foreach (Match match in AtRuleRegex().Matches(CssRuleScanner.StripComments(css)))
        {
            var name = match.Groups["name"].Value.ToLowerInvariant();
            if (SupportedAtRules.Contains(name))
            {
                continue;
            }

            var (severity, message) = name switch
            {
                "import" => (HtmlDiagnosticSeverity.Warning, "@import is not followed: link the style sheet with <link rel=\"stylesheet\"> instead."),
                "keyframes" or "-webkit-keyframes" or "property" or "view-transition" => (HtmlDiagnosticSeverity.Info, $"@{name} has no effect in a PDF."),
                _ => (HtmlDiagnosticSeverity.Warning, $"@{name} rules are not supported: they and the rules inside them are ignored."),
            };
            diagnostics.Report(HtmlDiagnosticKind.UnsupportedRule, severity, "@" + name, message);
        }
    }

    /// <summary>A selector list split at its top-level commas.</summary>
    private static IEnumerable<string> SplitSelectors(string selectorList)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < selectorList.Length; i++)
        {
            switch (selectorList[i])
            {
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return selectorList[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return selectorList[start..];
    }

    private static string Shorten(string text) => text.Length > 80 ? string.Concat(text.AsSpan(0, 80), "\u2026") : text;

    [GeneratedRegex(@"(?<colons>::?)(?<name>-?[a-zA-Z][a-zA-Z0-9-]*)")]
    private static partial Regex PseudoElementRegex();

    [GeneratedRegex(@"\s*!\s*important\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ImportantRegex();

    [GeneratedRegex(@"(?:^|[{};])\s*@(?<name>-?[a-zA-Z][a-zA-Z0-9-]*)")]
    private static partial Regex AtRuleRegex();
}

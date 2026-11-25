namespace RaiCore.Pdf.Html.Diagnostics;

/// <summary>
/// What the converter does with CSS, for diagnostics: the properties it supports, those that have no effect in a PDF,
/// and the values of supported properties that it lays out differently from browsers.
/// </summary>
/// <remarks>Keep in step with <see cref="Css.ComputedStyle"/>, the paged media code and the README's list.</remarks>
internal static class CssSupport
{
    /// <summary>Properties (longhands and the shorthands that set them) the converter uses.</summary>
    public static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "align-content", "align-items", "align-self", "background", "background-clip", "background-color",
        "background-image", "background-origin", "background-position", "background-position-x", "background-position-y",
        "background-repeat", "background-size", "bookmark-label", "bookmark-level", "bookmark-state", "border",
        "border-bottom", "border-bottom-color", "border-bottom-left-radius", "border-bottom-right-radius", "border-bottom-style",
        "border-bottom-width", "border-collapse", "border-color", "border-left", "border-left-color", "border-left-style",
        "border-left-width", "border-radius", "border-right", "border-right-color", "border-right-style", "border-right-width",
        "border-spacing", "border-style", "border-top", "border-top-color", "border-top-left-radius", "border-top-right-radius",
        "border-top-style", "border-top-width", "border-width", "bottom", "box-sizing", "break-after", "break-before",
        "break-inside", "clear", "color", "column-gap", "content", "counter-increment", "counter-reset", "counter-set",
        "direction", "display", "flex", "flex-basis", "flex-direction", "flex-flow", "flex-grow", "flex-shrink", "flex-wrap",
        "float", "font", "font-family", "font-feature-settings", "font-size", "font-style", "font-weight", "gap", "height",
        "hyphenate-character", "hyphenate-limit-chars", "hyphens", "inset", "justify-content", "left", "letter-spacing",
        "line-height", "list-style", "list-style-position", "list-style-type", "margin", "margin-block", "margin-block-end",
        "margin-block-start", "margin-bottom", "margin-inline", "margin-inline-end", "margin-inline-start", "margin-left",
        "margin-right", "margin-top", "max-height", "max-width", "min-height", "min-width", "opacity", "order", "orphans",
        "overflow", "overflow-x", "overflow-y", "padding", "padding-block", "padding-block-end", "padding-block-start",
        "padding-bottom", "padding-inline", "padding-inline-end", "padding-inline-start", "padding-left", "padding-right",
        "padding-top", "page", "page-break-after", "page-break-before", "page-break-inside", "place-content", "position",
        "quotes", "right", "row-gap", "string-set", "table-layout", "text-align", "text-decoration", "text-decoration-color",
        "text-decoration-line", "text-decoration-style", "text-decoration-thickness", "text-indent", "text-overflow",
        "text-transform", "text-underline-offset", "top", "unicode-bidi", "vertical-align", "visibility", "white-space",
        "widows", "width", "word-spacing", "z-index",

        // Logical borders: AngleSharp maps them to physical sides by the element's direction.
        "border-block", "border-block-color", "border-block-end", "border-block-end-color", "border-block-end-style",
        "border-block-end-width", "border-block-start", "border-block-start-color", "border-block-start-style",
        "border-block-start-width", "border-block-style", "border-block-width", "border-inline", "border-inline-color",
        "border-inline-end", "border-inline-end-color", "border-inline-end-style", "border-inline-end-width",
        "border-inline-start", "border-inline-start-color", "border-inline-start-style", "border-inline-start-width",
        "border-inline-style", "border-inline-width",

        // Aliases the converter rewrites to the standard names.
        "-webkit-hyphens", "-webkit-hyphenate-character",
    };

    /// <summary>
    /// Supported properties the converter reads from the CSS text itself, because AngleSharp does not keep them: their
    /// values are not checked against AngleSharp.
    /// </summary>
    public static readonly HashSet<string> ReadFromText = new(StringComparer.Ordinal)
    {
        "bookmark-label", "bookmark-level", "bookmark-state", "content", "hyphenate-limit-chars", "page", "position", "string-set",
        "page-break-after", "page-break-before", "page-break-inside", "break-after", "break-before",
        "margin-block", "margin-block-end", "margin-block-start", "margin-inline", "margin-inline-end", "margin-inline-start",
        "padding-block", "padding-block-end", "padding-block-start", "padding-inline", "padding-inline-end", "padding-inline-start",
        "background", "background-image", "background-position", "background-size", "background-repeat", "background-origin", "background-clip",
        "-webkit-hyphens", "-webkit-hyphenate-character",
    };

    /// <summary>Properties that only matter on screen or for interaction: they change nothing in a PDF.</summary>
    public static readonly HashSet<string> NoEffectInPrint = new(StringComparer.Ordinal)
    {
        "accent-color", "animation", "animation-delay", "animation-direction", "animation-duration", "animation-fill-mode",
        "animation-iteration-count", "animation-name", "animation-play-state", "animation-timing-function", "appearance",
        "caret-color", "color-scheme", "content-visibility", "cursor", "forced-color-adjust", "overscroll-behavior",
        "overscroll-behavior-x", "overscroll-behavior-y", "pointer-events", "print-color-adjust", "resize", "scroll-behavior",
        "scroll-margin", "scroll-margin-top", "scroll-margin-bottom", "scroll-padding", "scroll-padding-top", "scroll-snap-align",
        "scroll-snap-type", "scrollbar-color", "scrollbar-gutter", "scrollbar-width", "text-rendering", "touch-action",
        "transition", "transition-delay", "transition-duration", "transition-property", "transition-timing-function",
        "user-select", "will-change",
    };

    /// <summary>The issue with a supported property's value that the converter lays out differently, or <c>null</c>.</summary>
    public static string? ValueIssue(string property, string value) => property switch
    {
        "display" => value switch
        {
            "grid" => "Grid layout is not supported: the element is laid out as a block, its children one below the other.",
            "inline-grid" => "Grid layout is not supported: the element is laid out as an inline-block.",
            "contents" => "display: contents is not supported: the element keeps a box (laid out inline).",
            "none" or "block" or "inline" or "inline-block" or "flex" or "inline-flex" or "flow-root" or "flow" or "list-item"
                or "table" or "inline-table" or "table-row" or "table-cell" or "table-row-group" or "table-header-group"
                or "table-footer-group" or "table-caption" => null,
            _ => $"display: {value} is not supported: the element is laid out as a block.",
        },
        "position" when value == "sticky" => "position: sticky is laid out as static (there is no scrolling in print).",
        _ => null,
    };

    /// <summary>Whether a property name is a vendor-prefixed one (<c>-webkit-</c>, <c>-moz-</c>, ...).</summary>
    public static bool IsVendorPrefixed(string property) => property.Length > 1 && property[0] == '-' && property[1] != '-';

    /// <summary>Whether a property is one of Outlook's own (<c>mso-line-height-rule</c>, <c>mso-table-lspace</c>, ...).</summary>
    public static bool IsOutlookOnly(string property) => property.StartsWith("mso-", StringComparison.Ordinal);
}

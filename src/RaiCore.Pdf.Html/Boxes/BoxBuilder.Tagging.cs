using AngleSharp.Dom;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Html.Boxes;

/// <summary>
/// Structure tags from HTML semantics, for tagged PDF and PDF/UA: headings, paragraphs, lists (with labels and bodies),
/// tables (header cells with scopes), figures with their <c>alt</c> text, sections and quotations. Links are tagged by
/// the layout engine. Other elements add no element of their own; their text belongs to the nearest tagged ancestor.
/// </summary>
internal sealed partial class BoxBuilder
{
    /// <summary>The structure tag of an element's block, or <c>null</c> for none of its own.</summary>
    private static StructureTag? TagFor(IElement element, ComputedStyle style)
    {
        var language = OwnLanguage(element);
        StructureTag Tag(string type) => new(type) { Language = language };
        return element.LocalName switch
        {
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => new StructureTag("H" + element.LocalName[1]) { Language = language },
            "p" or "address" or "pre" or "dt" or "dd" => Tag(PdfStructureTypes.Paragraph),
            "blockquote" => Tag(PdfStructureTypes.BlockQuote),
            "ul" or "ol" or "menu" => new StructureTag(PdfStructureTypes.List) { Language = language, ListNumbering = Numbering(style.ListStyleType) },
            "li" when style.Display == CssDisplay.ListItem => new StructureTag(PdfStructureTypes.ListItem) { Language = language, ContentType = PdfStructureTypes.ListBody },
            "section" => Tag(PdfStructureTypes.Section),
            "article" => Tag(PdfStructureTypes.Article),
            "figcaption" or "caption" => Tag(PdfStructureTypes.Caption),
            "hr" => StructureTag.Artifact,
            _ => language is null ? null : Tag(PdfStructureTypes.Division), // A part in another language.
        };
    }

    /// <summary>
    /// The tag of an image: a figure described by its <c>alt</c> text (an SVG's <c>&lt;title&gt;</c> or
    /// <c>aria-label</c>), or an artifact for an image with an empty <c>alt</c> (decorative, as HTML defines it).
    /// </summary>
    private static StructureTag ImageTag(IElement element)
    {
        var alt = element.LocalName == "svg"
            ? element.GetAttribute("aria-label") ?? element.Children.FirstOrDefault(child => child.LocalName == "title")?.TextContent
            : element.GetAttribute("alt");
        if (alt is not null && string.IsNullOrWhiteSpace(alt) && element.LocalName != "svg")
        {
            return StructureTag.Artifact;
        }

        return new StructureTag(PdfStructureTypes.Figure) { AlternateText = alt?.Trim() is { Length: > 0 } text ? text : null, Language = OwnLanguage(element) };
    }

    /// <summary>
    /// The tag of a table cell: <c>th</c> is a header cell for its column when it is in a header row group or a row of
    /// header cells only, else for its row, unless <c>scope</c> says otherwise; <c>td</c> a data cell.
    /// </summary>
    private static StructureTag CellTag(IElement cell, bool headerGroup)
    {
        if (cell.LocalName != "th")
        {
            return new StructureTag(PdfStructureTypes.TableDataCell) { Language = OwnLanguage(cell) };
        }

        var scope = cell.GetAttribute("scope")?.Trim().ToUpperInvariant() switch
        {
            "COL" or "COLGROUP" => PdfTableScope.Column,
            "ROW" or "ROWGROUP" => PdfTableScope.Row,
            _ when headerGroup || cell.ParentElement?.Children.All(c => c.LocalName != "td") == true => PdfTableScope.Column,
            _ => PdfTableScope.Row,
        };

        return new StructureTag(PdfStructureTypes.TableHeaderCell) { Scope = scope, Language = OwnLanguage(cell) };
    }

    /// <summary>The label of a list item's marker.</summary>
    internal static StructureTag ListLabel { get; } = new(PdfStructureTypes.Label);

    /// <summary>The language an element declares itself (<c>lang</c> or <c>xml:lang</c>), except the root's (the document's).</summary>
    private static string? OwnLanguage(IElement element)
    {
        if (element.ParentElement is null)
        {
            return null;
        }

        var language = element.GetAttribute("lang") ?? element.GetAttribute("xml:lang");
        return string.IsNullOrWhiteSpace(language) ? null : language.Trim();
    }

    /// <summary>How a list's items are labelled, from its <c>list-style-type</c>.</summary>
    private static PdfListNumbering Numbering(string listStyleType) => listStyleType switch
    {
        "disc" => PdfListNumbering.Disc,
        "circle" => PdfListNumbering.Circle,
        "square" => PdfListNumbering.Square,
        "decimal" or "decimal-leading-zero" => PdfListNumbering.DecimalDigits,
        "lower-roman" => PdfListNumbering.LowerRoman,
        "upper-roman" => PdfListNumbering.UpperRoman,
        "lower-alpha" or "lower-latin" => PdfListNumbering.LowerAlpha,
        "upper-alpha" or "upper-latin" => PdfListNumbering.UpperAlpha,
        _ => PdfListNumbering.None,
    };
}

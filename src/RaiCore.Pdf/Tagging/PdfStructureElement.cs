namespace RaiCore.Pdf;

/// <summary>
/// An element of a tagged document's logical structure (ISO 32000-1, section 14.7): a heading, paragraph, list, table,
/// figure, link and so on, nested in the order a screen reader reads them. Content drawn between
/// <see cref="PdfCanvas.BeginTag(PdfStructureElement)"/> and <see cref="PdfCanvas.EndTag"/> belongs to the element.
/// </summary>
/// <remarks>
/// Created with <see cref="AddChild(string)"/>, starting from <see cref="PdfDocument.StructureRoot"/>. Use the standard
/// structure types (<see cref="PdfStructureTypes"/>), which PDF/UA requires. Elements that end up with no content and no
/// children are left out of the file.
/// </remarks>
public sealed class PdfStructureElement
{
    private readonly List<PdfStructureElement> _children = [];

    /// <summary>The element's content and children, in reading order.</summary>
    private readonly List<StructureKid> _kids = [];

    internal PdfStructureElement(string type, PdfStructureElement? parent, PdfDocument document)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        if (type.Any(c => c <= ' ' || c > '~' || "()<>[]{}/%#".Contains(c, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"\"{type}\" is not a valid structure type name.", nameof(type));
        }

        Type = type;
        Parent = parent;
        Document = document;
    }

    /// <summary>The structure type, such as <c>P</c>, <c>H1</c> or <c>Table</c> (see <see cref="PdfStructureTypes"/>).</summary>
    public string Type { get; }

    /// <summary>The element this one is part of; <c>null</c> for the root.</summary>
    public PdfStructureElement? Parent { get; }

    /// <summary>The child elements, in reading order.</summary>
    public IReadOnlyList<PdfStructureElement> Children => _children;

    /// <summary>
    /// A description of the element for readers who cannot see it (<c>/Alt</c>): required for figures and formulas by
    /// PDF/UA.
    /// </summary>
    public string? AlternateText { get; set; }

    /// <summary>The exact text the element's content stands for (<c>/ActualText</c>), e.g. for a ligature or a symbol.</summary>
    public string? ActualText { get; set; }

    /// <summary>The expansion of an abbreviation (<c>/E</c>).</summary>
    public string? Expansion { get; set; }

    /// <summary>A title for the element (<c>/T</c>), such as a section's name.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// The natural language of the element's content as a BCP 47 tag (<c>/Lang</c>), where it differs from the
    /// document's <see cref="PdfDocument.Language"/>.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>For a table header cell (<c>TH</c>): which cells it is the header of (the Table attribute <c>/Scope</c>).</summary>
    public PdfTableScope? Scope { get; set; }

    /// <summary>For a table cell: the number of rows it spans (written when above 1).</summary>
    public int RowSpan { get; set; } = 1;

    /// <summary>For a table cell: the number of columns it spans (written when above 1).</summary>
    public int ColumnSpan { get; set; } = 1;

    /// <summary>For a list (<c>L</c>): how its items are numbered or bulleted (the List attribute <c>/ListNumbering</c>).</summary>
    public PdfListNumbering? ListNumbering { get; set; }

    /// <summary>The document the element belongs to.</summary>
    internal PdfDocument Document { get; }

    /// <summary>The element's content (marked content, annotations) and child elements, in reading order.</summary>
    internal IReadOnlyList<StructureKid> Kids => _kids;

    /// <summary>Adds a child element at the end, after the content and children added so far.</summary>
    /// <param name="type">The structure type (see <see cref="PdfStructureTypes"/>).</param>
    public PdfStructureElement AddChild(string type)
    {
        var child = new PdfStructureElement(type, this, Document);
        _children.Add(child);
        _kids.Add(new ElementKid(child));
        return child;
    }

    /// <summary>Records marked content or an annotation as the element's next piece of content.</summary>
    internal void AddContent(StructureKid kid) => _kids.Add(kid);

    /// <summary>Whether the element, or one of its descendants, has content: elements without any are not written.</summary>
    internal bool HasContent => _kids.Any(kid => kid is not ElementKid { Element.HasContent: false });

    /// <inheritdoc />
    public override string ToString() => Type;
}

/// <summary>Which cells a table header cell is the header of (ISO 32000-1, table 349, <c>/Scope</c>).</summary>
public enum PdfTableScope
{
    /// <summary>The cells of its row.</summary>
    Row,

    /// <summary>The cells of its column.</summary>
    Column,

    /// <summary>The cells of its row and column.</summary>
    Both,
}

/// <summary>How the items of a list are labelled (ISO 32000-1, table 347, <c>/ListNumbering</c>).</summary>
public enum PdfListNumbering
{
    /// <summary>No labels, or labels that are neither bullets nor numbers.</summary>
    None,

    /// <summary>A filled circle.</summary>
    Disc,

    /// <summary>An open circle.</summary>
    Circle,

    /// <summary>A filled square.</summary>
    Square,

    /// <summary>Decimal numbers (<c>/Decimal</c>).</summary>
    DecimalDigits,

    /// <summary>Uppercase Roman numerals.</summary>
    UpperRoman,

    /// <summary>Lowercase Roman numerals.</summary>
    LowerRoman,

    /// <summary>Uppercase letters.</summary>
    UpperAlpha,

    /// <summary>Lowercase letters.</summary>
    LowerAlpha,
}

/// <summary>
/// What kind of artifact (content that is not part of the document's logical content) is drawn between
/// <see cref="PdfCanvas.BeginArtifact(PdfArtifactKind)"/> and <see cref="PdfCanvas.EndTag"/> (ISO 32000-1, section 14.8.2.2).
/// </summary>
public enum PdfArtifactKind
{
    /// <summary>Layout decoration: backgrounds, borders, rules, list bullets that repeat what the structure says.</summary>
    Layout,

    /// <summary>Pagination: running headers and footers, page numbers, repeated table headers.</summary>
    Pagination,

    /// <summary>Production marks of the page itself, such as cut marks.</summary>
    Page,

    /// <summary>A background image or color behind the page's content.</summary>
    Background,
}

/// <summary>The standard structure types of ISO 32000-1, section 14.8.4, which PDF/UA requires.</summary>
public static class PdfStructureTypes
{
    /// <summary>The whole document.</summary>
    public const string Document = "Document";

    /// <summary>A part of the document, such as a chapter.</summary>
    public const string Part = "Part";

    /// <summary>A self-contained article.</summary>
    public const string Article = "Art";

    /// <summary>A section.</summary>
    public const string Section = "Sect";

    /// <summary>A generic block grouping.</summary>
    public const string Division = "Div";

    /// <summary>A quotation of one or more paragraphs.</summary>
    public const string BlockQuote = "BlockQuote";

    /// <summary>A caption of a table or figure.</summary>
    public const string Caption = "Caption";

    /// <summary>A table of contents.</summary>
    public const string TableOfContents = "TOC";

    /// <summary>An entry of a table of contents.</summary>
    public const string TableOfContentsItem = "TOCI";

    /// <summary>An index.</summary>
    public const string Index = "Index";

    /// <summary>A paragraph.</summary>
    public const string Paragraph = "P";

    /// <summary>A heading without a level.</summary>
    public const string Heading = "H";

    /// <summary>A level 1 heading.</summary>
    public const string Heading1 = "H1";

    /// <summary>A level 2 heading.</summary>
    public const string Heading2 = "H2";

    /// <summary>A level 3 heading.</summary>
    public const string Heading3 = "H3";

    /// <summary>A level 4 heading.</summary>
    public const string Heading4 = "H4";

    /// <summary>A level 5 heading.</summary>
    public const string Heading5 = "H5";

    /// <summary>A level 6 heading.</summary>
    public const string Heading6 = "H6";

    /// <summary>A list.</summary>
    public const string List = "L";

    /// <summary>A list item.</summary>
    public const string ListItem = "LI";

    /// <summary>A list item's label (its bullet or number).</summary>
    public const string Label = "Lbl";

    /// <summary>A list item's body.</summary>
    public const string ListBody = "LBody";

    /// <summary>A table.</summary>
    public const string Table = "Table";

    /// <summary>A table's header row group.</summary>
    public const string TableHead = "THead";

    /// <summary>A table's body row group.</summary>
    public const string TableBody = "TBody";

    /// <summary>A table's footer row group.</summary>
    public const string TableFoot = "TFoot";

    /// <summary>A table row.</summary>
    public const string TableRow = "TR";

    /// <summary>A table header cell.</summary>
    public const string TableHeaderCell = "TH";

    /// <summary>A table data cell.</summary>
    public const string TableDataCell = "TD";

    /// <summary>A generic inline span, e.g. text in another language.</summary>
    public const string Span = "Span";

    /// <summary>A quotation inside a paragraph.</summary>
    public const string Quote = "Quote";

    /// <summary>A footnote or endnote.</summary>
    public const string Note = "Note";

    /// <summary>A reference to elsewhere in the document.</summary>
    public const string Reference = "Reference";

    /// <summary>Computer code.</summary>
    public const string Code = "Code";

    /// <summary>A link (its text and its link annotations).</summary>
    public const string Link = "Link";

    /// <summary>An annotation other than a link.</summary>
    public const string Annotation = "Annot";

    /// <summary>An image or drawing; needs <see cref="PdfStructureElement.AlternateText"/>.</summary>
    public const string Figure = "Figure";

    /// <summary>A mathematical formula; needs <see cref="PdfStructureElement.AlternateText"/>.</summary>
    public const string Formula = "Formula";

    /// <summary>A form field.</summary>
    public const string Form = "Form";
}

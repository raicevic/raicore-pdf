namespace RaiCore.Pdf.Documents.Model;

/// <summary>
/// A word-processing document in a form independent of its file format: sections of blocks (paragraphs and tables)
/// with resolved formatting, their page setup, headers and footers, and the notes the text refers to. The DOCX reader
/// builds it; RTF, ODT and Word 97-2003 readers can build the same model, and <see cref="Rendering.DocumentRenderer"/>
/// lays it out. All lengths are in points.
/// </summary>
internal sealed class WordDocument
{
    /// <summary>The sections, in order; a document has at least one.</summary>
    public List<WordSection> Sections { get; } = [];

    /// <summary>The footnotes by the id their references use.</summary>
    public Dictionary<string, List<WordBlock>> Footnotes { get; } = new(StringComparer.Ordinal);

    /// <summary>The endnotes by the id their references use.</summary>
    public Dictionary<string, List<WordBlock>> Endnotes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The comments by the id their references use (<see cref="CommentReference"/>); read only when they are shown or
    /// extracted (<see cref="ReadExtras.Comments"/>).
    /// </summary>
    public Dictionary<string, WordComment> Comments { get; } = new(StringComparer.Ordinal);

    /// <summary>How footnote numbers are written.</summary>
    public NumberFormat FootnoteFormat { get; set; } = NumberFormat.Decimal;

    /// <summary>How endnote numbers are written.</summary>
    public NumberFormat EndnoteFormat { get; set; } = NumberFormat.LowerRoman;

    /// <summary>Whether even pages have their own headers and footers (<see cref="HeaderFooterKind.Even"/>).</summary>
    public bool EvenAndOddHeaders { get; set; }

    /// <summary>The distance between the default tab stops.</summary>
    public double DefaultTabStop { get; set; } = 36;

    /// <summary>The document's title (its properties), or <c>null</c>.</summary>
    public string? Title { get; set; }

    /// <summary>The document's author (its properties), or <c>null</c>.</summary>
    public string? Author { get; set; }

    /// <summary>The document's language (a BCP 47 tag such as <c>en-US</c>), or <c>null</c>.</summary>
    public string? Language { get; set; }
}

/// <summary>A comment: its text, who wrote it and when, and the comment it replies to.</summary>
internal sealed class WordComment
{
    /// <summary>The comment's text.</summary>
    public List<WordBlock> Blocks { get; } = [];

    /// <summary>Who wrote it, or <c>null</c>.</summary>
    public string? Author { get; set; }

    /// <summary>The author's initials, or <c>null</c>.</summary>
    public string? Initials { get; set; }

    /// <summary>When it was written (as stored, in no particular time zone), or <c>null</c>.</summary>
    public DateTime? Date { get; set; }

    /// <summary>The id of the comment it replies to, or <c>null</c>.</summary>
    public string? ParentId { get; set; }
}

/// <summary>How a section starts.</summary>
internal enum SectionStart
{
    /// <summary>On a new page.</summary>
    NextPage,

    /// <summary>On the same page as the section before it (when the page setup allows).</summary>
    Continuous,

    /// <summary>On the next even page.</summary>
    EvenPage,

    /// <summary>On the next odd page.</summary>
    OddPage,
}

/// <summary>Which pages a header or footer is for.</summary>
internal enum HeaderFooterKind
{
    /// <summary>All pages the others are not for (odd pages, when even pages have their own).</summary>
    Default,

    /// <summary>The first page of the section, when the section has a title page.</summary>
    First,

    /// <summary>Even pages, when the document has different even and odd headers.</summary>
    Even,
}

/// <summary>A part of a document with its own page setup, headers and footers.</summary>
internal sealed class WordSection
{
    /// <summary>The page width.</summary>
    public double PageWidth { get; set; } = 612;

    /// <summary>The page height.</summary>
    public double PageHeight { get; set; } = 792;

    /// <summary>The margin above the text.</summary>
    public double MarginTop { get; set; } = 72;

    /// <summary>The margin right of the text.</summary>
    public double MarginRight { get; set; } = 72;

    /// <summary>The margin below the text.</summary>
    public double MarginBottom { get; set; } = 72;

    /// <summary>The margin left of the text.</summary>
    public double MarginLeft { get; set; } = 72;

    /// <summary>The distance from the top edge of the page to the header.</summary>
    public double HeaderDistance { get; set; } = 36;

    /// <summary>The distance from the bottom edge of the page to the footer.</summary>
    public double FooterDistance { get; set; } = 36;

    /// <summary>How the section starts.</summary>
    public SectionStart Start { get; set; }

    /// <summary>Whether the first page of the section has its own header and footer (<see cref="HeaderFooterKind.First"/>).</summary>
    public bool TitlePage { get; set; }

    /// <summary>The page number of the section's first page, or <c>null</c> to continue from the section before.</summary>
    public int? PageNumberStart { get; set; }

    /// <summary>How page numbers are written in the section.</summary>
    public NumberFormat PageNumberFormat { get; set; } = NumberFormat.Decimal;

    /// <summary>The number of text columns the section asks for (laid out as one).</summary>
    public int Columns { get; set; } = 1;

    /// <summary>The headers by kind; a kind the section lacks is inherited from the section before it.</summary>
    public Dictionary<HeaderFooterKind, List<WordBlock>> Headers { get; } = [];

    /// <summary>The footers by kind; a kind the section lacks is inherited from the section before it.</summary>
    public Dictionary<HeaderFooterKind, List<WordBlock>> Footers { get; } = [];

    /// <summary>The content.</summary>
    public List<WordBlock> Blocks { get; } = [];

    /// <summary>Whether two sections have the same page size and margins (a continuous section can share a page).</summary>
    public bool SamePageAs(WordSection other) =>
        PageWidth == other.PageWidth && PageHeight == other.PageHeight && MarginTop == other.MarginTop && MarginBottom == other.MarginBottom
        && MarginLeft == other.MarginLeft && MarginRight == other.MarginRight;
}

/// <summary>How numbers (list labels, page numbers, note marks) are written.</summary>
internal enum NumberFormat
{
    /// <summary>1, 2, 3.</summary>
    Decimal,

    /// <summary>01, 02, 03.</summary>
    DecimalZero,

    /// <summary>a, b, c, ..., z, aa, bb.</summary>
    LowerLetter,

    /// <summary>A, B, C.</summary>
    UpperLetter,

    /// <summary>i, ii, iii.</summary>
    LowerRoman,

    /// <summary>I, II, III.</summary>
    UpperRoman,

    /// <summary>1st, 2nd, 3rd.</summary>
    Ordinal,

    /// <summary>A bullet: the level text as it is.</summary>
    Bullet,

    /// <summary>No number.</summary>
    None,
}

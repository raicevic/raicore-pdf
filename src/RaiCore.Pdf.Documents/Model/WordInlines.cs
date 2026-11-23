namespace RaiCore.Pdf.Documents.Model;

/// <summary>Underline styles.</summary>
internal enum UnderlineStyle
{
    /// <summary>No underline.</summary>
    None,

    /// <summary>A single line.</summary>
    Single,

    /// <summary>Two lines.</summary>
    Double,

    /// <summary>Dots.</summary>
    Dotted,

    /// <summary>Dashes.</summary>
    Dashed,

    /// <summary>A wave.</summary>
    Wavy,
}

/// <summary>Raised or lowered text.</summary>
internal enum VerticalPosition
{
    /// <summary>On the baseline.</summary>
    Baseline,

    /// <summary>Superscript.</summary>
    Superscript,

    /// <summary>Subscript.</summary>
    Subscript,
}

/// <summary>The formatting of a run of text, resolved from the styles and its own properties.</summary>
internal sealed record RunFormat
{
    /// <summary>The default format: 10 points in the default font.</summary>
    public static RunFormat Default { get; } = new();

    /// <summary>The font family, or <c>null</c> for the default font.</summary>
    public string? FontFamily { get; init; }

    /// <summary>The font size in points.</summary>
    public double FontSize { get; init; } = 10;

    /// <summary>Bold.</summary>
    public bool Bold { get; init; }

    /// <summary>Italic.</summary>
    public bool Italic { get; init; }

    /// <summary>The underline.</summary>
    public UnderlineStyle Underline { get; init; }

    /// <summary>Struck through (once or twice).</summary>
    public bool Strike { get; init; }

    /// <summary>The text color, or <c>null</c> for automatic (black).</summary>
    public PdfColor? Color { get; init; }

    /// <summary>The background (highlight or shading), or <c>null</c>.</summary>
    public PdfColor? Background { get; init; }

    /// <summary>Superscript or subscript.</summary>
    public VerticalPosition Position { get; init; }

    /// <summary>A shift of the baseline in points (positive raises).</summary>
    public double BaselineShift { get; init; }

    /// <summary>All capitals.</summary>
    public bool Caps { get; init; }

    /// <summary>Small capitals.</summary>
    public bool SmallCaps { get; init; }

    /// <summary>Hidden text, which is not shown.</summary>
    public bool Hidden { get; init; }

    /// <summary>Extra space between characters, in points.</summary>
    public double LetterSpacing { get; init; }

    /// <summary>Right-to-left text.</summary>
    public bool RightToLeft { get; init; }

    /// <summary>The language (a BCP 47 tag), or <c>null</c>.</summary>
    public string? Language { get; init; }
}

/// <summary>Inline content of a paragraph.</summary>
internal abstract class WordInline
{
}

/// <summary>Text in one format.</summary>
internal sealed class TextRun(string text, RunFormat format) : WordInline
{
    /// <summary>The text (without line breaks or tabs, which are inlines of their own).</summary>
    public string Text { get; } = text;

    /// <summary>The format.</summary>
    public RunFormat Format { get; } = format;
}

/// <summary>A tab: space up to the next tab stop.</summary>
internal sealed class TabInline(RunFormat format) : WordInline
{
    /// <summary>The format of the text around it.</summary>
    public RunFormat Format { get; } = format;

    /// <summary>
    /// For a tab to a position relative to the margins rather than to a tab stop (a positional tab): where the text
    /// after it aligns, at the start margin, in the middle or at the end margin; otherwise <c>null</c>.
    /// </summary>
    public WordAlignment? PositionalAlignment { get; init; }
}

/// <summary>Kinds of breaks.</summary>
internal enum BreakKind
{
    /// <summary>A line break.</summary>
    Line,

    /// <summary>A page break.</summary>
    Page,

    /// <summary>A column break (a page break when the text is in one column).</summary>
    Column,
}

/// <summary>A break in a paragraph.</summary>
internal sealed class BreakInline(BreakKind kind, RunFormat format) : WordInline
{
    /// <summary>The kind.</summary>
    public BreakKind Kind { get; } = kind;

    /// <summary>The format of the text around it.</summary>
    public RunFormat Format { get; } = format;
}

/// <summary>How an image is placed.</summary>
internal enum ImagePlacement
{
    /// <summary>In the line of text, as a large character.</summary>
    Inline,

    /// <summary>At the start or end margin (by <see cref="ImageInline.Alignment"/>), text wrapping around it.</summary>
    Float,

    /// <summary>On a line of its own before its paragraph, aligned by <see cref="ImageInline.Alignment"/>.</summary>
    Block,
}

/// <summary>An image.</summary>
internal sealed class ImageInline(byte[] data, double width, double height) : WordInline
{
    /// <summary>The image file's bytes (PNG, JPEG, GIF, BMP, TIFF, WebP or SVG).</summary>
    public byte[] Data { get; } = data;

    /// <summary>The width in points.</summary>
    public double Width { get; } = width;

    /// <summary>The height in points.</summary>
    public double Height { get; } = height;

    /// <summary>How it is placed.</summary>
    public ImagePlacement Placement { get; init; }

    /// <summary>
    /// Where a floating or block image goes across: <see cref="WordAlignment.Start"/>, <see cref="WordAlignment.Center"/>
    /// or <see cref="WordAlignment.End"/>; <see cref="Offset"/> decides instead when it is set.
    /// </summary>
    public WordAlignment Alignment { get; init; }

    /// <summary>
    /// The distance of a floating or block image from the start margin, or <c>null</c> to place it by
    /// <see cref="Alignment"/>.
    /// </summary>
    public double? Offset { get; init; }

    /// <summary>Its alternative text, or <c>null</c>.</summary>
    public string? AlternativeText { get; init; }

    /// <summary>A name for diagnostics (its part name).</summary>
    public string? Name { get; init; }

    /// <summary>The chart drawn instead of image data, or <c>null</c> for a picture.</summary>
    public EmbeddedChart? Chart { get; init; }
}

/// <summary>A chart in a document: the chart, and the theme fonts its text may refer to.</summary>
/// <param name="Chart">The chart, its colors resolved through the document's theme.</param>
/// <param name="MinorFont">The theme's body font, for chart text that names no font or <c>+mn-lt</c>.</param>
/// <param name="MajorFont">The theme's heading font, for <c>+mj-lt</c>.</param>
internal sealed record EmbeddedChart(Charts.Chart Chart, string? MinorFont, string? MajorFont);

/// <summary>Fields whose value depends on the layout.</summary>
internal enum FieldKind
{
    /// <summary>The page number.</summary>
    Page,

    /// <summary>The number of pages of the document.</summary>
    NumPages,

    /// <summary>The number of pages of the section.</summary>
    SectionPages,

    /// <summary>The page number of a bookmark (<see cref="FieldInline.Argument"/>).</summary>
    PageRef,
}

/// <summary>A field whose value is computed when the document is laid out (other fields keep their last result).</summary>
internal sealed class FieldInline(FieldKind kind, RunFormat format, string? cachedResult) : WordInline
{
    /// <summary>The kind.</summary>
    public FieldKind Kind { get; } = kind;

    /// <summary>The format of the result.</summary>
    public RunFormat Format { get; } = format;

    /// <summary>The result the document was saved with, used where the value cannot be computed.</summary>
    public string? CachedResult { get; } = cachedResult;

    /// <summary>The bookmark of a <see cref="FieldKind.PageRef"/>.</summary>
    public string? Argument { get; init; }

    /// <summary>How the number is written, or <c>null</c> for the section's page number format.</summary>
    public NumberFormat? NumberFormat { get; init; }
}

/// <summary>A hyperlink around inline content.</summary>
internal sealed class HyperlinkInline : WordInline
{
    /// <summary>The web address or file the link opens, or <c>null</c>.</summary>
    public Uri? Uri { get; init; }

    /// <summary>The bookmark the link jumps to, or <c>null</c>.</summary>
    public string? Anchor { get; init; }

    /// <summary>The linked content.</summary>
    public List<WordInline> Inlines { get; } = [];
}

/// <summary>A bookmark: a place links and page references can point to.</summary>
internal sealed class BookmarkInline(string name) : WordInline
{
    /// <summary>The bookmark's name.</summary>
    public string Name { get; } = name;
}

/// <summary>Footnote or endnote.</summary>
internal enum NoteKind
{
    /// <summary>A footnote.</summary>
    Footnote,

    /// <summary>An endnote.</summary>
    Endnote,
}

/// <summary>A reference to a footnote or an endnote: its number, linked to the note.</summary>
internal sealed class NoteReference(NoteKind kind, string id, RunFormat format) : WordInline
{
    /// <summary>Footnote or endnote.</summary>
    public NoteKind Kind { get; } = kind;

    /// <summary>The note's id.</summary>
    public string Id { get; } = id;

    /// <summary>The format of the mark.</summary>
    public RunFormat Format { get; } = format;

    /// <summary>A custom mark (instead of the number), or <c>null</c>.</summary>
    public string? CustomMark { get; init; }
}

/// <summary>In a note's own text: the note's number.</summary>
internal sealed class NoteMark(RunFormat format) : WordInline
{
    /// <summary>The format of the mark.</summary>
    public RunFormat Format { get; } = format;
}

/// <summary>
/// Blocks anchored in a paragraph (the text of a text box), laid out after the paragraph since floating frames are not
/// supported.
/// </summary>
internal sealed class AnchoredBlocks : WordInline
{
    /// <summary>The blocks.</summary>
    public List<WordBlock> Blocks { get; } = [];
}

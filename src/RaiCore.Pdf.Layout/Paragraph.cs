using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Layout;

/// <summary>
/// A block placed in a line of text as one unbreakable item (CSS <c>inline-block</c>, <c>inline-flex</c>,
/// <c>inline-table</c>): a badge, a button, a small table. It is as wide as its
/// <see cref="BoxStyle.Width"/>, or shrinks to fit its content within the paragraph's width; its last line of text
/// sits on the line's baseline (or its bottom margin edge, when it has no text). Its margins add space around it.
/// </summary>
/// <param name="Content">The box and its content. Its children's margins stay inside it.</param>
/// <param name="Alignment">Its vertical position in the line (CSS <c>vertical-align</c>).</param>
public sealed record InlineBox(Block Content, InlineAlignment Alignment = default);

/// <summary>An image placed in a line of text, with its bottom on the baseline (like an HTML <c>img</c> in a <c>p</c>).</summary>
/// <param name="Image">The image.</param>
/// <param name="Width">Width in points.</param>
/// <param name="Height">Height in points.</param>
/// <param name="Alignment">Its vertical position in the line (CSS <c>vertical-align</c>).</param>
public sealed record InlineImage(PdfImage Image, double Width, double Height, InlineAlignment Alignment = default);

/// <summary>
/// The box of an inline element around part of a paragraph's text (CSS backgrounds, borders, padding and horizontal
/// margins on a <c>span</c>): drawn behind the text of each line it covers, sliced at line breaks.
/// </summary>
/// <param name="Start">Index in the paragraph text where the element starts.</param>
/// <param name="End">Index where it ends (exclusive).</param>
/// <param name="Box">Its padding, border, background and margins (vertical margins are ignored, as in CSS).</param>
/// <param name="Style">Its text style: the font's ascent and descent, raised by its vertical shift, make the content area.</param>
/// <param name="HasStartEdge">False when the element started in an earlier paragraph (its left edge is there).</param>
/// <param name="HasEndEdge">False when it continues in a later paragraph.</param>
public sealed record InlineDecoration(int Start, int End, BoxStyle Box, TextStyle Style, bool HasStartEdge = true, bool HasEndEdge = true);

/// <summary>A link around part of a paragraph's text (see <see cref="Paragraph.BeginLink"/>).</summary>
/// <param name="Start">Index in the paragraph text where the link starts.</param>
/// <param name="End">Index where it ends (exclusive).</param>
/// <param name="Target">Where it goes.</param>
public sealed record InlineLink(int Start, int End, LinkTarget Target);

/// <summary>A link destination inside a paragraph (see <see cref="Paragraph.AddAnchor"/>).</summary>
/// <param name="Index">Index in the paragraph text: the destination is the line containing it.</param>
/// <param name="Id">The id links refer to (<see cref="LinkTarget.ToDestination"/>).</param>
public sealed record InlineAnchor(int Index, string Id);

/// <summary>A run of text in one style, an inline image or an inline box.</summary>
/// <param name="Text">The text; U+FFFC for an image or a box.</param>
/// <param name="Style">The style (for an image or a box, the paragraph style).</param>
/// <param name="Image">The image, or <c>null</c>.</param>
/// <param name="Box">The inline box, or <c>null</c>.</param>
public sealed record ParagraphSpan(string Text, TextStyle Style, InlineImage? Image = null, InlineBox? Box = null);

/// <summary>
/// A paragraph of styled text, laid out into lines with <see cref="Layout(double)"/>.
/// The text is used as given: collapsing white space (as HTML does) is up to the caller.
/// Line feeds and other hard breaks start a new line.
/// </summary>
/// <example>
/// <code>
/// var body = new TextStyle(fonts.Find("serif")!, 11);
/// var paragraph = new Paragraph(body) { Alignment = TextAlignment.Justify }
///     .Add("The quick brown fox ")
///     .Add("jumps", body with { Color = PdfColor.FromRgb(200, 0, 0) })
///     .Add(" over the lazy dog.");
/// var layout = paragraph.Layout(width: 300);
/// layout.Draw(page.Canvas, x: 72, top: 700);
/// </code>
/// </example>
public sealed class Paragraph
{
    private readonly List<ParagraphSpan> _spans = [];
    private readonly List<InlineDecoration> _decorations = [];
    private readonly Stack<(int Start, BoxStyle Box, TextStyle Style, bool StartEdge)> _open = new();
    private readonly List<InlineLink> _links = [];
    private readonly Stack<(int Start, LinkTarget Target)> _openLinks = new();
    private readonly List<InlineAnchor> _anchors = [];
    private int _length;

    /// <summary>Creates a paragraph. <paramref name="style"/> is the default for spans and sets the minimum line height (the CSS "strut").</summary>
    public Paragraph(TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        Style = style;
    }

    /// <summary>The paragraph's base style.</summary>
    public TextStyle Style { get; }

    /// <summary>Horizontal alignment.</summary>
    public TextAlignment Alignment { get; set; } = TextAlignment.Left;

    /// <summary>Indent of the first line in points (CSS <c>text-indent</c>; may be negative for hanging indents).</summary>
    public double FirstLineIndent { get; set; }

    /// <summary>
    /// Break a word that is wider than the line at any character, instead of letting it overflow
    /// (CSS <c>overflow-wrap: break-word</c>). Off by default, as in CSS.
    /// </summary>
    public bool BreakLongWords { get; set; }

    /// <summary>The spans, in order.</summary>
    public IReadOnlyList<ParagraphSpan> Spans => _spans;

    /// <summary>The boxes of inline elements (see <see cref="BeginInline"/>), in the order they end.</summary>
    public IReadOnlyList<InlineDecoration> Decorations => _decorations;

    /// <summary>The links in the text (see <see cref="BeginLink"/>), in the order they end.</summary>
    public IReadOnlyList<InlineLink> Links => _links;

    /// <summary>The link destinations in the text (see <see cref="AddAnchor"/>), in order.</summary>
    public IReadOnlyList<InlineAnchor> Anchors => _anchors;

    /// <summary>
    /// Starts a link: the text, images and boxes added until the matching <see cref="EndLink"/> become clickable. Each
    /// line the link covers gets its own rectangle, as high as the line, on the page where the line is drawn.
    /// </summary>
    public Paragraph BeginLink(LinkTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _openLinks.Push((_length, target));
        return this;
    }

    /// <summary>Ends the innermost link started with <see cref="BeginLink"/>. An empty one is dropped.</summary>
    public Paragraph EndLink()
    {
        if (_openLinks.Count == 0)
        {
            throw new InvalidOperationException("EndLink without a matching BeginLink.");
        }

        var (start, target) = _openLinks.Pop();
        if (_length > start)
        {
            _links.Add(new InlineLink(start, _length, target));
        }

        return this;
    }

    /// <summary>Appends text that links to <paramref name="target"/>, in the paragraph's style or <paramref name="style"/>.</summary>
    public Paragraph AddLink(string text, LinkTarget target, TextStyle? style = null)
    {
        BeginLink(target);
        Add(text, style ?? Style);
        return EndLink();
    }

    /// <summary>
    /// Marks the current position (the end of the text added so far) as a destination that links can jump to with
    /// <see cref="LinkTarget.ToDestination"/>: the top of the line it falls on, once the paragraph is laid out.
    /// </summary>
    public Paragraph AddAnchor(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        _anchors.Add(new InlineAnchor(_length, id));
        return this;
    }

    /// <summary>
    /// Starts an inline element with a box: the text added until the matching <see cref="EndInline"/> gets its
    /// background, border and padding, and its left padding, border and margin take space before it.
    /// </summary>
    /// <param name="box">Padding, border, background and horizontal margins.</param>
    /// <param name="style">Its text style (for the height of its content area); defaults to the paragraph's.</param>
    /// <param name="startEdge">False if the element began in an earlier paragraph: no left edge here.</param>
    public Paragraph BeginInline(BoxStyle box, TextStyle? style = null, bool startEdge = true)
    {
        ArgumentNullException.ThrowIfNull(box);
        _open.Push((_length, box, style ?? Style, startEdge));
        return this;
    }

    /// <summary>Ends the innermost inline element started with <see cref="BeginInline"/>. An empty one is dropped.</summary>
    /// <param name="endEdge">False if the element continues in a later paragraph: no right edge here.</param>
    public Paragraph EndInline(bool endEdge = true)
    {
        if (_open.Count == 0)
        {
            throw new InvalidOperationException("EndInline without a matching BeginInline.");
        }

        var (start, box, style, startEdge) = _open.Pop();
        if (_length > start)
        {
            _decorations.Add(new InlineDecoration(start, _length, box, style, startEdge, endEdge));
        }

        return this;
    }

    /// <summary>Adds content inside an inline element's box (see <see cref="BeginInline"/>).</summary>
    public Paragraph AddInline(BoxStyle box, Action<Paragraph> content, TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        BeginInline(box, style);
        content(this);
        return EndInline();
    }

    /// <summary>Appends text in the paragraph's style.</summary>
    public Paragraph Add(string text) => Add(text, Style);

    /// <summary>Appends text in a style.</summary>
    public Paragraph Add(string text, TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);
        if (text.Length > 0)
        {
            _spans.Add(new ParagraphSpan(text, style));
            _length += text.Length;
        }

        return this;
    }

    /// <summary>
    /// Appends an image that flows with the text: it sits on the baseline, makes its line taller if needed,
    /// and lines may break before and after it.
    /// </summary>
    public Paragraph AddImage(PdfImage image, double width, double height, InlineAlignment alignment = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        _spans.Add(new ParagraphSpan("\uFFFC", Style, new InlineImage(image, width, height, alignment)));
        _length++;
        return this;
    }

    /// <summary>
    /// Appends a block that flows with the text as one unbreakable item (see <see cref="InlineBox"/>): lines may break
    /// before and after it, and it makes its line taller if needed.
    /// </summary>
    public Paragraph AddBox(Block content, InlineAlignment alignment = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        _spans.Add(new ParagraphSpan("\uFFFC", Style, Box: new InlineBox(content, alignment)));
        _length++;
        return this;
    }

    /// <summary>
    /// The paragraph's intrinsic widths in points (CSS min-content and max-content): the narrowest width without
    /// overflowing words, and the width it takes without wrapping. Used to size table columns.
    /// </summary>
    public (double Min, double Max) MeasureIntrinsicWidths() => ParagraphLayouter.MeasureIntrinsic(this);

    /// <summary>Breaks the paragraph into lines no wider than <paramref name="width"/> points.</summary>
    public ParagraphLayout Layout(double width)
    {
        if (width <= 0 || double.IsNaN(width))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be positive.");
        }

        return ParagraphLayouter.Layout(this, width);
    }
}

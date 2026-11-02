namespace RaiCore.Pdf.Layout;

/// <summary>An image placed in a line of text, with its bottom on the baseline (like an HTML <c>img</c> in a <c>p</c>).</summary>
/// <param name="Image">The image.</param>
/// <param name="Width">Width in points.</param>
/// <param name="Height">Height in points.</param>
public sealed record InlineImage(PdfImage Image, double Width, double Height);

/// <summary>A run of text in one style, or an inline image.</summary>
/// <param name="Text">The text; U+FFFC for an image.</param>
/// <param name="Style">The style (for an image, the paragraph style).</param>
/// <param name="Image">The image, or <c>null</c> for text.</param>
public sealed record ParagraphSpan(string Text, TextStyle Style, InlineImage? Image = null);

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
        }

        return this;
    }

    /// <summary>
    /// Appends an image that flows with the text: it sits on the baseline, makes its line taller if needed,
    /// and lines may break before and after it.
    /// </summary>
    public Paragraph AddImage(PdfImage image, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        _spans.Add(new ParagraphSpan("\uFFFC", Style, new InlineImage(image, width, height)));
        return this;
    }

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

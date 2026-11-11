using System.Globalization;
using System.Text;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Html.Boxes;

/// <summary>
/// Collects the inline content of one block (text, line breaks, inline images) into a <see cref="Paragraph"/>,
/// collapsing white space as CSS does for <c>white-space: normal</c> and its variants.
/// </summary>
internal sealed class InlineCollector(BoxBuilder builder, ComputedStyle blockStyle, double contentWidth)
{
    private Paragraph? _paragraph;
    private bool _hasVisibleContent;
    private bool _afterSpace = true; // At the start of a line, collapsible spaces are removed.
    private int _column; // For tab stops in preserved text.
    private string? _marker;

    /// <summary>Floats met in the inline content, placed before the paragraph they are in.</summary>
    private readonly List<Block> _floats = [];

    /// <summary>Inline elements with a box that are open, outer first (they continue in the next paragraph after a block).</summary>
    private readonly List<(BoxStyle Box, TextStyle Style, TextDirection Direction)> _openInlines = [];

    /// <summary>Bidi controls of open inline elements, outer first, as (opening, closing, style) (re-opened after a block).</summary>
    private readonly List<(string Open, string Close, TextStyle Style)> _openBidi = [];

    /// <summary>Links that are open, outer first (they continue in the next paragraph after a block).</summary>
    private readonly List<LinkTarget> _openLinks = [];

    /// <summary>The innermost open link: block-level content inside it is linked as a whole.</summary>
    public LinkTarget? CurrentLink => _openLinks.Count > 0 ? _openLinks[^1] : null;

    /// <summary>
    /// The anchors of the last paragraph flushed without visible content (e.g. <c>&lt;p&gt;&lt;a id="x"&gt;&lt;/a&gt;&lt;/p&gt;</c>),
    /// which no paragraph block keeps.
    /// </summary>
    public IReadOnlyList<string> DroppedAnchors { get; private set; } = [];

    /// <summary>
    /// Adds a leader (CSS <c>leader()</c>): its pattern, repeated when the line is laid out to fill the rest of the
    /// line. Its spaces are kept.
    /// </summary>
    public void AddLeader(string pattern, ComputedStyle style, InlineAlignment alignment = default)
    {
        var textStyle = builder.TextStyleFor(style);
        if (alignment != default)
        {
            textStyle = textStyle with { VerticalAlign = alignment };
        }

        if (builder.CurrentDecorations.Count > 0)
        {
            textStyle = textStyle with { Decorations = builder.CurrentDecorations };
        }

        Paragraph().AddLeader(pattern, textStyle);
        _afterSpace = pattern.EndsWith(' ');
        _hasVisibleContent = true;
    }

    /// <summary>
    /// Adds the blocks of a floated element met in the inline content. They do not end the paragraph: they go before it,
    /// so the float starts at the paragraph's first line.
    /// </summary>
    public void AddFloat(IEnumerable<Block> blocks) => _floats.AddRange(blocks);

    /// <summary>The floats met since the last call, which go before the paragraph <see cref="Flush"/> returns.</summary>
    public List<Block> TakeFloats()
    {
        var floats = new List<Block>(_floats);
        _floats.Clear();
        return floats;
    }

    /// <summary>A list marker to place before the first content (or alone, if the item is empty).</summary>
    public void SetMarker(string marker) => _marker = marker;

    public void AddText(string text, ComputedStyle style, InlineAlignment alignment = default)
    {
        var processed = ProcessWhiteSpace(Transform(text, style.TextTransform), style.WhiteSpace);
        if (processed.Length == 0)
        {
            return;
        }

        var textStyle = builder.TextStyleFor(style);
        if (alignment != default)
        {
            textStyle = textStyle with { VerticalAlign = alignment };
        }

        if (builder.CurrentDecorations.Count > 0)
        {
            textStyle = textStyle with { Decorations = builder.CurrentDecorations };
        }

        Paragraph().Add(processed, textStyle);
        if (processed.Any(c => !char.IsWhiteSpace(c)))
        {
            _hasVisibleContent = true;
        }
    }

    public void AddLineBreak(ComputedStyle style)
    {
        Paragraph().Add("\n", builder.TextStyleFor(style));
        _afterSpace = true;
        _column = 0;
        _hasVisibleContent = true;
    }

    public void AddImage(PdfImage image, double width, double height, InlineAlignment alignment = default, double opacity = 1, CornerRadii borderRadius = default)
    {
        Paragraph().AddImage(image, width, height, alignment, opacity, borderRadius);
        _afterSpace = false;
        _hasVisibleContent = true;
    }

    /// <summary>Starts an inline element with a background, border, padding or horizontal margins.</summary>
    public void BeginInline(BoxStyle box, TextStyle style, TextDirection direction)
    {
        Paragraph().BeginInline(box, style, direction: direction);
        _openInlines.Add((box, style, direction));
    }

    /// <summary>
    /// Starts an inline element's bidi embedding, override or isolate: adds its opening controls, which are not drawn
    /// and do not affect white-space collapsing.
    /// </summary>
    public void BeginBidi(string open, string close, ComputedStyle style)
    {
        var textStyle = builder.TextStyleFor(style);
        Paragraph().Add(open, textStyle);
        _openBidi.Add((open, close, textStyle));
    }

    /// <summary>Ends the innermost bidi embedding started with <see cref="BeginBidi"/>.</summary>
    public void EndBidi()
    {
        var (_, close, style) = _openBidi[^1];
        _openBidi.RemoveAt(_openBidi.Count - 1);
        _paragraph?.Add(close, style);
    }

    /// <summary>Ends the innermost inline element started with <see cref="BeginInline"/>.</summary>
    public void EndInline()
    {
        _openInlines.RemoveAt(_openInlines.Count - 1);
        _paragraph?.EndInline();
    }

    /// <summary>Starts a link around the inline content that follows.</summary>
    public void BeginLink(LinkTarget target)
    {
        Paragraph().BeginLink(target);
        _openLinks.Add(target);
    }

    /// <summary>Ends the innermost link started with <see cref="BeginLink"/>.</summary>
    public void EndLink()
    {
        _openLinks.RemoveAt(_openLinks.Count - 1);
        _paragraph?.EndLink();
    }

    /// <summary>Marks the current position as a link destination (an inline element's id).</summary>
    public void AddAnchor(string id) => Paragraph().AddAnchor(id);

    /// <summary>Adds an atomic inline box (inline-block, inline-flex, inline-table) as one item of the line.</summary>
    public void AddBox(Block block, InlineAlignment alignment = default)
    {
        Paragraph().AddBox(block, alignment);
        _afterSpace = false;
        _hasVisibleContent = true;
    }

    /// <summary>The finished paragraph as an anonymous block, or null if there was no visible content.</summary>
    public ParagraphBlock? Flush()
    {
        if (_marker is not null && _paragraph is null)
        {
            Paragraph(); // An empty list item still shows its marker.
            _hasVisibleContent = true;
        }

        // A block inside an inline element splits it: its box ends here without its right edge and continues in the
        // next paragraph without its left edge.
        for (var i = 0; i < _openInlines.Count && _paragraph is not null; i++)
        {
            _paragraph.EndInline(endEdge: false);
        }

        for (var i = 0; i < _openLinks.Count && _paragraph is not null; i++)
        {
            _paragraph.EndLink();
        }

        var result = _hasVisibleContent && _paragraph is not null
            ? new ParagraphBlock(_paragraph, new BoxStyle { Orphans = blockStyle.Orphans, Widows = blockStyle.Widows })
            : null;
        DroppedAnchors = result is null && _paragraph is not null ? [.. _paragraph.Anchors.Select(a => a.Id)] : [];
        _paragraph = null;
        _hasVisibleContent = false;
        _afterSpace = true;
        _column = 0;
        return result;
    }

    private Paragraph Paragraph()
    {
        if (_paragraph is not null)
        {
            return _paragraph;
        }

        var baseStyle = builder.TextStyleFor(blockStyle);
        var indent = blockStyle.TextIndent.Resolve(contentWidth);
        _paragraph = new Paragraph(baseStyle)
        {
            Alignment = blockStyle.TextAlign,
            Direction = blockStyle.DirectionAuto || blockStyle.UnicodeBidi == CssUnicodeBidi.Plaintext ? ParagraphDirection.Auto
                : blockStyle.Rtl ? ParagraphDirection.RightToLeft
                : ParagraphDirection.LeftToRight,
            FirstLineIndent = indent,
            BreakLongWords = false,
            Ellipsis = blockStyle.TextOverflowEllipsis && blockStyle.ClipOverflow,
        };

        // An override on the block applies to all of its inline content.
        if (blockStyle.UnicodeBidi is CssUnicodeBidi.BidiOverride or CssUnicodeBidi.IsolateOverride)
        {
            _paragraph.Add(blockStyle.Rtl ? "\u202E" : "\u202D", baseStyle);
        }

        if (_marker is { } marker)
        {
            // An outside marker hangs to the left of the text; an inside marker is just the first piece of text.
            var markerText = marker + "\u00A0";
            _paragraph.Add(markerText, baseStyle);
            if (!blockStyle.ListStyleInside)
            {
                _paragraph.FirstLineIndent = indent - TextShaper.Shape(markerText, baseStyle.Fonts, baseStyle.Shaping).GetWidth(baseStyle.FontSize);
            }

            _marker = null;
        }

        foreach (var (box, style, direction) in _openInlines)
        {
            _paragraph.BeginInline(box, style, startEdge: false, direction: direction);
        }

        foreach (var (open, _, style) in _openBidi)
        {
            _paragraph.Add(open, style);
        }

        foreach (var link in _openLinks)
        {
            _paragraph.BeginLink(link);
        }

        return _paragraph;
    }

    private string ProcessWhiteSpace(string text, CssWhiteSpace mode)
    {
        var output = new StringBuilder(text.Length);
        var preserveSpaces = mode is CssWhiteSpace.Pre or CssWhiteSpace.PreWrap;
        var preserveNewlines = mode is CssWhiteSpace.Pre or CssWhiteSpace.PreWrap or CssWhiteSpace.PreLine;
        var noWrap = mode is CssWhiteSpace.NoWrap or CssWhiteSpace.Pre;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                continue;
            }

            if (c == '\n' && preserveNewlines)
            {
                if (mode == CssWhiteSpace.PreLine)
                {
                    TrimTrailingSpace(output);
                }

                output.Append('\n');
                _afterSpace = true;
                _column = 0;
                continue;
            }

            if (c is ' ' or '\t' or '\n' or '\f')
            {
                if (preserveSpaces)
                {
                    var count = c == '\t' ? 8 - (_column % 8) : 1;
                    output.Append(noWrap ? '\u00A0' : ' ', count);
                    _column += count;
                    _afterSpace = false;
                }
                else if (!_afterSpace)
                {
                    output.Append(noWrap ? '\u00A0' : ' ');
                    _afterSpace = true;
                    _column++;
                }

                continue;
            }

            output.Append(c);
            _afterSpace = false;
            _column++;
        }

        return output.ToString();
    }

    private static void TrimTrailingSpace(StringBuilder output)
    {
        while (output.Length > 0 && output[^1] == ' ')
        {
            output.Length--;
        }
    }

    private static string Transform(string text, string transform) => transform switch
    {
        "uppercase" => text.ToUpperInvariant(),
        "lowercase" => text.ToLowerInvariant(),
        "capitalize" => Capitalize(text),
        _ => text,
    };

    private static string Capitalize(string text)
    {
        var chars = text.ToCharArray();
        var startOfWord = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetter(chars[i]))
            {
                if (startOfWord)
                {
                    chars[i] = char.ToUpper(chars[i], CultureInfo.InvariantCulture);
                }

                startOfWord = false;
            }
            else if (char.IsWhiteSpace(chars[i]) || chars[i] is '-' or '/')
            {
                startOfWord = true;
            }
        }

        return new string(chars);
    }
}

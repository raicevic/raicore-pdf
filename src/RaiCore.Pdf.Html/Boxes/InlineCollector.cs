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

    /// <summary>A list marker to place before the first content (or alone, if the item is empty).</summary>
    public void SetMarker(string marker) => _marker = marker;

    public void AddText(string text, ComputedStyle style)
    {
        var processed = ProcessWhiteSpace(Transform(text, style.TextTransform), style.WhiteSpace);
        if (processed.Length == 0)
        {
            return;
        }

        Paragraph().Add(processed, builder.TextStyleFor(style));
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

    public void AddImage(PdfImage image, double width, double height)
    {
        Paragraph().AddImage(image, width, height);
        _afterSpace = false;
        _hasVisibleContent = true;
    }

    /// <summary>Adds an atomic inline box (inline-block, inline-flex, inline-table) as one item of the line.</summary>
    public void AddBox(Block block)
    {
        Paragraph().AddBox(block);
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

        var result = _hasVisibleContent && _paragraph is not null
            ? new ParagraphBlock(_paragraph, new BoxStyle { Orphans = blockStyle.Orphans, Widows = blockStyle.Widows })
            : null;
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
            FirstLineIndent = indent,
            BreakLongWords = false,
        };

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

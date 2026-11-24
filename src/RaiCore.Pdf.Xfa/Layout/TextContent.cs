using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Xfa.Template;
using LayoutParagraph = RaiCore.Pdf.Layout.Paragraph;

namespace RaiCore.Pdf.Xfa.Layout;

/// <summary>A run of text with its font.</summary>
internal sealed record TextRunSpec(string Text, FontSpec Font);

/// <summary>A paragraph of text to lay out: its runs and formatting.</summary>
internal sealed class ParagraphSpec
{
    public List<TextRunSpec> Runs { get; } = [];

    /// <summary>
    /// The paragraph's formatting. A negative text indent hangs the first line out to the left margin, so the margin is at
    /// least the indent (the first line never starts left of the text area).
    /// </summary>
    public required ParaSpec Para
    {
        get;
        set => field = value.MarginLeft + value.TextIndent < 0 ? value with { MarginLeft = -value.TextIndent } : value;
    }

    /// <summary>The font of an empty paragraph's line (and of a list label).</summary>
    public required FontSpec Font { get; set; }

    /// <summary>Text drawn before the paragraph's first line in its indent (a list label), or <c>null</c>.</summary>
    public string? Label { get; set; }

    public bool IsEmpty => Runs.All(r => r.Text.Length == 0);
}

/// <summary>A laid out paragraph: its lines and place in the block.</summary>
internal sealed record LaidParagraph(ParagraphLayout? Layout, double Top, double X, double Height, ParagraphLayout? Label, double LabelX);

/// <summary>Paragraphs laid out at a width: what a draw's or field's text area shows.</summary>
internal sealed class TextBlock
{
    public List<LaidParagraph> Paragraphs { get; } = [];

    public double Height { get; set; }

    /// <summary>The widest line, from the block's left edge.</summary>
    public double ContentWidth { get; set; }

    /// <summary>The width the block was laid out at.</summary>
    public double Width { get; init; }

    /// <summary>The first line's baseline from the block's top (for comb fields), or 0.</summary>
    public double FirstBaseline { get; set; }
}

/// <summary>Lays out paragraphs with the layout engine.</summary>
internal sealed class TextLayouter(XfaFonts fonts)
{
    /// <summary>The layout engine's style of a font.</summary>
    public TextStyle Style(FontSpec font, LineHeight lineHeight)
    {
        var decorations = new List<TextDecoration>();
        if (font.Underline > 0)
        {
            decorations.Add(new TextDecoration(TextDecorationLines.Underline, font.Color)
            {
                Style = font.Underline == 2 ? TextDecorationStyle.Double : TextDecorationStyle.Solid,
            });
        }

        if (font.LineThrough)
        {
            decorations.Add(new TextDecoration(TextDecorationLines.LineThrough, font.Color));
        }

        return new TextStyle(fonts.Get(font.Typeface, font.Bold, font.Italic), Math.Max(font.Size, 0.1))
        {
            Color = font.Color,
            Decorations = decorations,
            LetterSpacing = font.LetterSpacing,
            LineHeight = lineHeight,
            VerticalAlign = font.BaselineShift != 0 ? InlineAlignment.Raised(font.BaselineShift) : InlineAlignment.Baseline,
        };
    }

    /// <summary>The widest line the paragraphs have unwrapped (for growable widths).</summary>
    public double NaturalWidth(IReadOnlyList<ParagraphSpec> paragraphs)
    {
        var width = 0.0;
        foreach (var spec in paragraphs)
        {
            if (!spec.IsEmpty)
            {
                width = Math.Max(width, Build(spec).MeasureIntrinsicWidths().Max + spec.Para.MarginLeft + spec.Para.MarginRight + Math.Max(spec.Para.TextIndent, 0));
            }
        }

        return width;
    }

    /// <summary>Lays out paragraphs at a width; <paramref name="wrap"/> false keeps each paragraph's lines unbroken.</summary>
    public TextBlock Layout(IReadOnlyList<ParagraphSpec> paragraphs, double width, bool wrap = true)
    {
        var block = new TextBlock { Width = width };
        var y = 0.0;
        for (var i = 0; i < paragraphs.Count; i++)
        {
            ResourceLimits.Checkpoint();
            var spec = paragraphs[i];
            y += spec.Para.SpaceAbove;
            var lineHeight = LineHeightOf(spec.Para);
            var available = Math.Max(width - spec.Para.MarginLeft - spec.Para.MarginRight, 1);
            ParagraphLayout? layout = null;
            double height;
            var x = spec.Para.MarginLeft;
            if (spec.IsEmpty)
            {
                var style = Style(spec.Font, lineHeight);
                height = lineHeight.Resolve(style.Fonts[0], style.FontSize);
            }
            else
            {
                var paragraph = Build(spec);
                var layoutWidth = available;
                if (!wrap)
                {
                    var natural = paragraph.MeasureIntrinsicWidths().Max + Math.Max(spec.Para.TextIndent, 0) + 0.01;
                    if (natural > available)
                    {
                        layoutWidth = natural;
                        x += spec.Para.HAlign switch
                        {
                            "center" => (available - natural) / 2,
                            "right" => available - natural,
                            _ => 0,
                        };
                    }
                }

                layout = paragraph.Layout(layoutWidth);
                height = layout.Height;
                block.ContentWidth = Math.Max(block.ContentWidth, x + layout.ContentWidth);
                if (i == 0 && layout.Lines.Count > 0)
                {
                    block.FirstBaseline = y + layout.Lines[0].Top + layout.Lines[0].Baseline;
                }
            }

            ParagraphLayout? label = null;
            var labelX = 0.0;
            if (spec.Label is { } text)
            {
                var labelParagraph = new LayoutParagraph(Style(spec.Font, lineHeight)).Add(text);
                var labelWidth = labelParagraph.MeasureIntrinsicWidths().Max;
                label = labelParagraph.Layout(labelWidth + 1);
                labelX = Math.Max(spec.Para.MarginLeft - labelWidth - (spec.Font.Size * 0.4), 0);
            }

            block.Paragraphs.Add(new LaidParagraph(layout, y, x, height, label, labelX));
            y += height + spec.Para.SpaceBelow;
        }

        block.Height = y;
        return block;
    }

    private static LineHeight LineHeightOf(ParaSpec para) => para.LineHeight > 0 ? LineHeight.Points(para.LineHeight) : LineHeight.Normal;

    private LayoutParagraph Build(ParagraphSpec spec)
    {
        var lineHeight = LineHeightOf(spec.Para);
        var paragraph = new LayoutParagraph(Style(spec.Font, lineHeight))
        {
            Alignment = spec.Para.HAlign switch
            {
                "center" => TextAlignment.Center,
                "right" or "radix" => TextAlignment.Right,
                "justify" or "justifyAll" => TextAlignment.Justify,
                _ => TextAlignment.Left,
            },
            FirstLineIndent = spec.Para.TextIndent,
            BreakLongWords = true,
        };
        foreach (var run in spec.Runs)
        {
            if (run.Text.Length > 0)
            {
                paragraph.Add(run.Text, Style(run.Font, lineHeight));
            }
        }

        return paragraph;
    }
}

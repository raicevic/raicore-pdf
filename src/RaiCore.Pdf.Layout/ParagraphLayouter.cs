using System.Globalization;
using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Layout;

/// <summary>
/// Breaks a paragraph into lines: shapes every span to measure it, finds break opportunities with UAX #14,
/// fills lines greedily, then reshapes each line so glyphs are exact, and computes line boxes with the
/// CSS inline model (each piece of text contributes its ascent, descent and half-leading).
/// </summary>
internal static class ParagraphLayouter
{
    private const double Epsilon = 1e-6;
    private const char SoftHyphen = '\u00AD';

    /// <summary>
    /// The CSS intrinsic widths: min-content is the widest piece that cannot be broken (between two break opportunities),
    /// max-content is the widest line when only hard breaks are used.
    /// </summary>
    public static (double Min, double Max) MeasureIntrinsic(Paragraph paragraph)
    {
        var (text, spanStarts) = Concatenate(paragraph.Spans);
        if (text.Length == 0)
        {
            return (0, 0);
        }

        // Inline boxes contribute their min-content width to unbreakable pieces and their max-content width to lines.
        var minPrefix = MeasurePrefix(text, paragraph.Spans, spanStarts, box => Paginator.InlineBoxIntrinsic(box.Content).Min);
        var prefix = MeasurePrefix(text, paragraph.Spans, spanStarts, box => Paginator.InlineBoxIntrinsic(box.Content).Max);
        var indent = paragraph.FirstLineIndent;
        double min = 0, max = 0;
        int segmentStart = 0, lineStart = 0;
        foreach (var opportunity in LineBreaker.GetBreakOpportunities(text))
        {
            var segment = minPrefix[TrimEnd(text, segmentStart, opportunity.Position)] - minPrefix[segmentStart] + (segmentStart == 0 ? Math.Max(0, indent) : 0);
            min = Math.Max(min, segment);
            segmentStart = opportunity.Position;
            if (opportunity.Required || opportunity.Position == text.Length)
            {
                var line = prefix[TrimEnd(text, lineStart, opportunity.Position)] - prefix[lineStart] + (lineStart == 0 ? indent : 0);
                max = Math.Max(max, line);
                lineStart = opportunity.Position;
            }
        }

        return (min, Math.Max(min, max));
    }

    private static (string Text, int[] SpanStarts) Concatenate(IReadOnlyList<ParagraphSpan> spans)
    {
        var builder = new StringBuilder();
        var spanStarts = new int[spans.Count + 1];
        for (var s = 0; s < spans.Count; s++)
        {
            spanStarts[s] = builder.Length;
            builder.Append(spans[s].Text);
        }

        spanStarts[spans.Count] = builder.Length;
        return (builder.ToString(), spanStarts);
    }

    public static ParagraphLayout Layout(Paragraph paragraph, double width)
    {
        var spans = paragraph.Spans;
        var (text, spanStarts) = Concatenate(spans);
        if (text.Length == 0)
        {
            return new ParagraphLayout(width, []);
        }

        // Inline boxes are laid out once at this width; their outer widths take part in line breaking.
        var boxes = new Dictionary<InlineBox, InlineBoxFragment>(ReferenceEqualityComparer.Instance);
        foreach (var box in spans.Where(s => s.Box is not null).Select(s => s.Box!))
        {
            var borderWidth = Paginator.InlineBoxWidth(box.Content, width);
            var (height, baseline, content) = Paginator.LayoutInlineBox(box.Content, borderWidth);
            boxes[box] = new InlineBoxFragment(box, 0, borderWidth, height, baseline, content);
        }

        var prefix = MeasurePrefix(text, spans, spanStarts, box => boxes[box].OuterWidth);
        double Measure(int from, int to) => prefix[to] - prefix[from];

        var lineRanges = BreakIntoLines(paragraph, text, width, Measure, spans, spanStarts);

        var lines = new List<LineLayout>(lineRanges.Count);
        var top = 0.0;
        for (var i = 0; i < lineRanges.Count; i++)
        {
            var (start, end, hard, hyphenate) = lineRanges[i];
            var available = width - (i == 0 ? paragraph.FirstLineIndent : 0);
            var isLast = i == lineRanges.Count - 1;
            var line = BuildLine(paragraph, text, spans, spanStarts, start, end, hyphenate, available, top, i == 0,
                justify: paragraph.Alignment == TextAlignment.Justify && !isLast && !hard, boxes);
            lines.Add(line);
            top += line.Height;
        }

        return new ParagraphLayout(width, lines);
    }

    /// <summary>prefix[i] = width in points of text[0..i), measured by shaping each span.</summary>
    private static double[] MeasurePrefix(string text, IReadOnlyList<ParagraphSpan> spans, int[] spanStarts, Func<InlineBox, double> boxWidth)
    {
        var advance = new double[text.Length];
        for (var s = 0; s < spans.Count; s++)
        {
            var (spanText, style, image, box) = spans[s];
            if (image is not null)
            {
                advance[spanStarts[s]] = image.Width;
                continue;
            }

            if (box is not null)
            {
                advance[spanStarts[s]] = boxWidth(box);
                continue;
            }

            foreach (var run in TextShaper.Shape(spanText, style.Fonts, style.Shaping).Runs)
            {
                for (var g = 0; g < run.Glyphs.Count; g++)
                {
                    advance[spanStarts[s] + run.Clusters[g]] += run.Glyphs[g].Advance * style.FontSize;
                }
            }
        }

        var prefix = new double[text.Length + 1];
        for (var i = 0; i < text.Length; i++)
        {
            prefix[i + 1] = prefix[i] + advance[i];
        }

        return prefix;
    }

    private static List<(int Start, int End, bool Hard, bool Hyphenate)> BreakIntoLines(
        Paragraph paragraph, string text, double width, Func<int, int, double> measure,
        IReadOnlyList<ParagraphSpan> spans, int[] spanStarts)
    {
        var breaks = LineBreaker.GetBreakOpportunities(text);
        int[]? graphemes = null;
        var lines = new List<(int, int, bool, bool)>();
        var lineStart = 0;
        var b = 0;
        while (lineStart < text.Length)
        {
            var available = width - (lines.Count == 0 ? paragraph.FirstLineIndent : 0);
            while (b < breaks.Count && breaks[b].Position <= lineStart)
            {
                b++;
            }

            LineBreakOpportunity? lastFit = null;
            (int End, bool Hard, bool Hyphenate)? chosen = null;
            for (; b < breaks.Count; b++)
            {
                var candidate = breaks[b];
                var hyphenate = !candidate.Required && candidate.Position < text.Length && text[candidate.Position - 1] == SoftHyphen;
                var lineWidth = measure(lineStart, TrimEnd(text, lineStart, candidate.Position))
                    + (hyphenate ? HyphenWidth(StyleAt(spans, spanStarts, candidate.Position - 1)) : 0);

                if (lineWidth <= available + Epsilon)
                {
                    lastFit = candidate;
                    if (candidate.Required)
                    {
                        chosen = (candidate.Position, true, false);
                        b++;
                        break;
                    }

                    continue;
                }

                if (lastFit is { } fit)
                {
                    chosen = (fit.Position, false, text[fit.Position - 1] == SoftHyphen && fit.Position < text.Length);
                }
                else if (paragraph.BreakLongWords)
                {
                    // A word wider than the line: split it at the last grapheme boundary that fits (at least one grapheme).
                    graphemes ??= StringInfo.ParseCombiningCharacters(text);
                    var split = -1;
                    foreach (var g in graphemes)
                    {
                        if (g <= lineStart)
                        {
                            continue;
                        }

                        if (g >= candidate.Position || measure(lineStart, g) > available + Epsilon)
                        {
                            break;
                        }

                        split = g;
                    }

                    if (split < 0)
                    {
                        split = graphemes.FirstOrDefault(g => g > lineStart, candidate.Position);
                    }

                    chosen = (Math.Min(split, candidate.Position), candidate.Required && split >= candidate.Position, false);
                }
                else
                {
                    chosen = (candidate.Position, candidate.Required, hyphenate); // Overflows, like CSS.
                    b++;
                }

                break;
            }

            chosen ??= lastFit is { } last ? (last.Position, last.Required, false) : (text.Length, false, false);
            lines.Add((lineStart, chosen.Value.End, chosen.Value.Hard, chosen.Value.Hyphenate));
            lineStart = chosen.Value.End;
        }

        return lines;
    }

    private static LineLayout BuildLine(
        Paragraph paragraph, string text, IReadOnlyList<ParagraphSpan> spans, int[] spanStarts,
        int start, int end, bool hyphenate, double available, double top, bool firstLine, bool justify,
        Dictionary<InlineBox, InlineBoxFragment> boxes)
    {
        var contentEnd = TrimEnd(text, start, end);

        // Shape each span's part of the line again, so kerning and contextual forms are exact at line edges.
        // Image and box spans become pieces without a run.
        var pieces = new List<(ShapedRun? Run, TextStyle Style, InlineImage? Image, InlineBoxFragment? Box)>();
        for (var s = 0; s < spans.Count; s++)
        {
            var from = Math.Max(start, spanStarts[s]);
            var to = Math.Min(contentEnd, spanStarts[s + 1]);
            if (from >= to)
            {
                continue;
            }

            // A soft-hyphen break keeps the U+00AD in the line, so the last piece is never empty.
            var isLastPiece = to == contentEnd;

            var style = spans[s].Style;
            if (spans[s].Image is not null || spans[s].Box is not null)
            {
                pieces.Add((null, style, spans[s].Image, spans[s].Box is { } box ? boxes[box] : null));
                if (isLastPiece)
                {
                    break;
                }

                continue;
            }

            var piece = text[from..to];
            if (hyphenate && isLastPiece)
            {
                piece += style.Fonts[0].HasGlyph(0x2010) ? "\u2010" : "-";
            }

            if (piece.Length == 0)
            {
                continue;
            }

            foreach (var run in TextShaper.Shape(piece, style.Fonts, style.Shaping).VisualRuns)
            {
                pieces.Add((run, style, null, null));
            }

            if (isLastPiece)
            {
                break;
            }
        }

        // Justification: widen spaces so the line fills the available width.
        var naturalWidth = pieces.Sum(p => p.Run is { } run ? run.Advance * p.Style.FontSize : p.Image?.Width ?? p.Box!.OuterWidth);
        var glyphLists = pieces.Select(p => p.Run?.Glyphs ?? []).ToList();
        if (justify && naturalWidth < available)
        {
            var spaces = glyphLists.Sum(g => g.Count(IsSpace));
            if (spaces > 0)
            {
                var extra = (available - naturalWidth) / spaces;
                for (var p = 0; p < pieces.Count; p++)
                {
                    var size = pieces[p].Style.FontSize;
                    glyphLists[p] = [.. glyphLists[p].Select(g => IsSpace(g) ? g with { Advance = g.Advance + (extra / size) } : g)];
                }
            }
        }

        // Each piece's extent above and below its own baseline, its alignment, and its x position.
        var items = new (double Above, double Below, InlineAlignment Align, double X)[pieces.Count];
        var x = 0.0;
        for (var p = 0; p < pieces.Count; p++)
        {
            var (run, style, image, box) = pieces[p];
            if (image is not null)
            {
                items[p] = (image.Height, 0, image.Alignment, x); // An image's bottom is its baseline.
                x += image.Width;
            }
            else if (box is not null)
            {
                var margin = box.Box.Content.Style.Margin;
                items[p] = (margin.Top + box.Baseline, box.Height - box.Baseline + margin.Bottom, box.Box.Alignment, x);
                x += box.OuterWidth;
            }
            else
            {
                var (a, d) = InlineBox(run!.Font, style);
                items[p] = (a, d, style.VerticalAlign, x);
                x += glyphLists[p].Sum(g => g.Advance) * style.FontSize;
            }
        }

        // Line box (CSS 2.1 section 10.8): the strut and every item contribute their extent above and below the
        // baseline, after their vertical-align shift. Items aligned to the line's top or bottom come last, once the
        // line box is known, and only make it taller if they do not fit.
        var strut = paragraph.Style;
        var strutFont = strut.Fonts[0];
        var (above, below) = InlineBox(strutFont, strut);
        var shifts = new double[pieces.Count];
        for (var p = 0; p < pieces.Count; p++)
        {
            var (a, d, align, _) = items[p];
            shifts[p] = align.Shift + align.Kind switch
            {
                InlineAlignmentKind.Middle => (strutFont.XHeight * strut.FontSize / 2) - ((a - d) / 2),
                InlineAlignmentKind.TextTop => (strutFont.Ascent * strut.FontSize) - a,
                InlineAlignmentKind.TextBottom => d + (strutFont.Descent * strut.FontSize),
                _ => 0,
            };
            if (align.Kind is not (InlineAlignmentKind.Top or InlineAlignmentKind.Bottom))
            {
                above = Math.Max(above, a + shifts[p]);
                below = Math.Max(below, d - shifts[p]);
            }
        }

        for (var p = 0; p < pieces.Count; p++)
        {
            var (a, d, align, _) = items[p];
            if (align.Kind == InlineAlignmentKind.Top)
            {
                shifts[p] = above - a;
                below = Math.Max(below, a + d - above);
            }
            else if (align.Kind == InlineAlignmentKind.Bottom)
            {
                shifts[p] = d - below;
                above = Math.Max(above, a + d - below);
            }
        }

        var fragments = new List<LineFragment>(pieces.Count);
        var images = new List<InlineImageFragment>();
        var lineBoxes = new List<InlineBoxFragment>();
        for (var p = 0; p < pieces.Count; p++)
        {
            var (run, style, image, box) = pieces[p];
            var itemX = items[p].X;
            if (image is not null)
            {
                images.Add(new InlineImageFragment(image, itemX, shifts[p]));
            }
            else if (box is not null)
            {
                lineBoxes.Add(new InlineBoxFragment(box.Box, itemX, box.Width, box.Height, box.Baseline, box.Content, shifts[p]));
            }
            else
            {
                fragments.Add(new LineFragment(run!, style, glyphLists[p], itemX, shifts[p]));
            }
        }

        var indent = firstLine ? paragraph.FirstLineIndent : 0;
        var offset = paragraph.Alignment switch
        {
            TextAlignment.Right => Math.Max(0, available - x),
            TextAlignment.Center => Math.Max(0, (available - x) / 2),
            _ => 0,
        };

        return new LineLayout(start, end, top, above + below, above, indent + offset, x, fragments, images, lineBoxes);
    }

    private static (double Above, double Below) InlineBox(PdfFont font, TextStyle style)
    {
        var ascent = font.Ascent * style.FontSize;
        var descent = -font.Descent * style.FontSize;
        var halfLeading = (style.LineHeight.Resolve(font, style.FontSize) - (ascent + descent)) / 2;
        return (ascent + halfLeading, descent + halfLeading);
    }

    /// <summary>End of the visible content: trailing spaces hang and hard-break characters are not drawn.</summary>
    private static int TrimEnd(string text, int start, int end)
    {
        while (end > start && text[end - 1] is ' ' or '\t' or '\u3000' or '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029')
        {
            end--;
        }

        return end;
    }

    private static bool IsSpace(PositionedGlyph glyph) => glyph.Text is " " or "\u00A0";

    private static TextStyle StyleAt(IReadOnlyList<ParagraphSpan> spans, int[] spanStarts, int index)
    {
        for (var s = 0; s < spans.Count; s++)
        {
            if (index < spanStarts[s + 1])
            {
                return spans[s].Style;
            }
        }

        return spans[^1].Style;
    }

    private static double HyphenWidth(TextStyle style) =>
        TextShaper.Shape(style.Fonts[0].HasGlyph(0x2010) ? "\u2010" : "-", style.Fonts, style.Shaping).GetWidth(style.FontSize);
}

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

    private static readonly string[] NoOptionalLigatures = ["-liga", "-clig", "-dlig", "-hlig"];

    /// <summary>Scripts whose letters join, which letter spacing would break apart.</summary>
    private static readonly HashSet<string> CursiveScripts = ["Arab", "Syrc", "Mong", "Nkoo", "Phag", "Mand", "Mani", "Adlm", "Rohg", "Sogd", "Ougr"];

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
        var minPrefix = MeasurePrefix(text, paragraph, spanStarts, box => Paginator.InlineBoxIntrinsic(box.Content).Min);
        var prefix = MeasurePrefix(text, paragraph, spanStarts, box => Paginator.InlineBoxIntrinsic(box.Content).Max);
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

        var prefix = MeasurePrefix(text, paragraph, spanStarts, box => boxes[box].OuterWidth);
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
    private static double[] MeasurePrefix(string text, Paragraph paragraph, int[] spanStarts, Func<InlineBox, double> boxWidth)
    {
        var spans = paragraph.Spans;
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

            foreach (var run in TextShaper.Shape(spanText, style.Fonts, ShapingFor(style)).Runs)
            {
                var glyphs = Spaced(run, style);
                for (var g = 0; g < glyphs.Count; g++)
                {
                    advance[spanStarts[s] + run.Clusters[g]] += glyphs[g].Advance * style.FontSize;
                }
            }
        }

        // Inline elements' horizontal margins, borders and padding take space where they start and end.
        foreach (var decoration in paragraph.Decorations)
        {
            if (decoration.HasStartEdge)
            {
                advance[decoration.Start] += StartEdge(decoration.Box);
            }

            if (decoration.HasEndEdge)
            {
                advance[decoration.End - 1] += EndEdge(decoration.Box);
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
        // From and To are the piece's text range where it starts or ends its span's part of the line, else -1.
        var pieces = new List<(ShapedRun? Run, IReadOnlyList<PositionedGlyph> Glyphs, TextStyle Style, InlineImage? Image, InlineBoxFragment? Box, int From, int To)>();
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
                pieces.Add((null, [], style, spans[s].Image, spans[s].Box is { } box ? boxes[box] : null, from, to));
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

            var runs = TextShaper.Shape(piece, style.Fonts, ShapingFor(style)).VisualRuns.ToList();
            for (var r = 0; r < runs.Count; r++)
            {
                pieces.Add((runs[r], Spaced(runs[r], style), style, null, null, r == 0 ? from : -1, r == runs.Count - 1 ? to : -1));
            }

            if (isLastPiece)
            {
                break;
            }
        }

        // Justification: widen spaces so the line fills the available width.
        // Inline elements that start or end on this line add their edges.
        var decorations = paragraph.Decorations.Where(d => d.Start < contentEnd && d.End > start).ToList();
        var edges = decorations.Sum(d =>
            (d.HasStartEdge && d.Start >= start ? StartEdge(d.Box) : 0) + (d.HasEndEdge && d.End <= contentEnd ? EndEdge(d.Box) : 0));
        var naturalWidth = edges + pieces.Sum(p => p.Run is not null ? p.Glyphs.Sum(g => g.Advance) * p.Style.FontSize : p.Image?.Width ?? p.Box!.OuterWidth);
        var glyphLists = pieces.Select(p => p.Glyphs).ToList();
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
        var openDecorations = new Dictionary<InlineDecoration, (double X0, bool StartEdge)>(ReferenceEqualityComparer.Instance);
        var decorationPieces = new List<(InlineDecoration Decoration, double X0, double X1, bool StartEdge, bool EndEdge)>();
        foreach (var decoration in decorations.Where(d => d.Start < start))
        {
            openDecorations[decoration] = (0, false); // Continued from the previous line.
        }

        var x = 0.0;
        for (var p = 0; p < pieces.Count; p++)
        {
            var (run, _, style, image, box, from, to) = pieces[p];

            // Elements starting here, outer ones (ending later) first: margin, then the border box starts.
            foreach (var decoration in decorations.Where(d => d.Start == from).OrderByDescending(d => d.End))
            {
                var edge = decoration.HasStartEdge;
                x += edge ? decoration.Box.Margin.Left : 0;
                openDecorations[decoration] = (x, edge);
                x += edge ? decoration.Box.Border.Left.Width + decoration.Box.Padding.Left : 0;
            }

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

            // Elements ending here, inner ones (starting later) first: the border box ends, then the margin.
            foreach (var decoration in decorations.Where(d => d.End == to).OrderByDescending(d => d.Start))
            {
                var edge = decoration.HasEndEdge;
                x += edge ? decoration.Box.Padding.Right + decoration.Box.Border.Right.Width : 0;
                if (openDecorations.Remove(decoration, out var open))
                {
                    decorationPieces.Add((decoration, open.X0, x, open.StartEdge, edge));
                }

                x += edge ? decoration.Box.Margin.Right : 0;
            }
        }

        // Elements continuing on the next line end at the line's end, without their right edge.
        foreach (var (decoration, open) in openDecorations)
        {
            decorationPieces.Add((decoration, open.X0, x, open.StartEdge, false));
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
            var (run, _, style, image, box, _, _) = pieces[p];
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

        // Each element's piece covers its font's content area (raised with its text) plus its padding and border.
        var decorationFragments = decorationPieces
            .OrderBy(d => d.Decoration.Start).ThenByDescending(d => d.Decoration.End)
            .Select(d =>
            {
                var (style, box) = (d.Decoration.Style, d.Decoration.Box);
                var font = style.Fonts[0];
                var shift = style.VerticalAlign.Kind == InlineAlignmentKind.Baseline ? style.VerticalAlign.Shift : 0;
                var above = (font.Ascent * style.FontSize) + box.Padding.Top + box.Border.Top.Width + shift;
                var below = (-font.Descent * style.FontSize) + box.Padding.Bottom + box.Border.Bottom.Width - shift;
                return new InlineDecorationFragment(d.Decoration, d.X0, d.X1, above, below, d.StartEdge, d.EndEdge);
            })
            .ToList();

        return new LineLayout(start, end, top, above + below, above, indent + offset, x, fragments, images, lineBoxes, decorationFragments);
    }

    private static double StartEdge(BoxStyle box) => box.Margin.Left + box.Border.Left.Width + box.Padding.Left;

    private static double EndEdge(BoxStyle box) => box.Padding.Right + box.Border.Right.Width + box.Margin.Right;

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
        TextShaper.Shape(style.Fonts[0].HasGlyph(0x2010) ? "\u2010" : "-", style.Fonts, ShapingFor(style)).Runs
            .Sum(run => Spaced(run, style).Sum(g => g.Advance)) * style.FontSize;

    /// <summary>
    /// The style's shaping options; with letter spacing, optional ligatures are off as in browsers (CSS Text 3
    /// section 8.2.1), unless the style's own features turn them on again.
    /// </summary>
    internal static ShapingOptions ShapingFor(TextStyle style) => style.LetterSpacing == 0
        ? style.Shaping
        : new ShapingOptions { Features = [.. NoOptionalLigatures, .. style.Shaping.Features], Language = style.Shaping.Language };

    /// <summary>
    /// The run's glyphs with letter spacing after every cluster (a character with its marks, or a ligature) and word
    /// spacing after every word separator, as advances in em (CSS Text 3 section 8). Like browsers, the space follows
    /// the last character of a line too.
    /// </summary>
    private static IReadOnlyList<PositionedGlyph> Spaced(ShapedRun run, TextStyle style)
    {
        var letter = CursiveScripts.Contains(run.Script) ? 0 : style.LetterSpacing;
        if ((letter == 0 && style.WordSpacing == 0) || style.FontSize <= 0)
        {
            return run.Glyphs;
        }

        var text = run.Text;
        var glyphs = run.Glyphs.ToArray();
        for (var i = 0; i < glyphs.Length; i++)
        {
            if (i + 1 < glyphs.Length && run.Clusters[i + 1] == run.Clusters[i])
            {
                continue; // Not the cluster's last glyph.
            }

            var extra = letter + (IsWordSeparator(text, run.Clusters[i] - run.Start) ? style.WordSpacing : 0);
            glyphs[i] = glyphs[i] with { Advance = glyphs[i].Advance + (extra / style.FontSize) };
        }

        return glyphs;
    }

    /// <summary>The word-separator characters of CSS Text 3 section 8.1.</summary>
    private static bool IsWordSeparator(string text, int index) =>
        Rune.TryGetRuneAt(text, index, out var rune) && rune.Value is 0x20 or 0xA0 or 0x1361 or 0x10100 or 0x10101 or 0x1039F or 0x1091F;
}

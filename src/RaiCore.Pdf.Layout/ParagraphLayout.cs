using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Layout;

/// <summary>A paragraph broken into lines, ready to draw or to split across pages.</summary>
public sealed partial class ParagraphLayout
{
    internal ParagraphLayout(double availableWidth, IReadOnlyList<LineLayout> lines)
    {
        AvailableWidth = availableWidth;
        Lines = lines;
        Height = lines.Count == 0 ? 0 : lines[^1].Top + lines[^1].Height;
        ContentWidth = lines.Count == 0 ? 0 : lines.Max(l => l.Width);
    }

    /// <summary>The width the paragraph was laid out in.</summary>
    public double AvailableWidth { get; }

    /// <summary>The lines, top to bottom.</summary>
    public IReadOnlyList<LineLayout> Lines { get; }

    /// <summary>Total height in points.</summary>
    public double Height { get; }

    /// <summary>Width of the widest line (it can exceed <see cref="AvailableWidth"/> when a word does not fit).</summary>
    public double ContentWidth { get; }

    /// <summary>Draws all lines with the paragraph's top-left corner at (<paramref name="x"/>, <paramref name="top"/>).</summary>
    public void Draw(PdfCanvas canvas, double x, double top) => Draw(canvas, x, top, 0, Lines.Count);

    /// <summary>
    /// Draws lines <paramref name="firstLine"/> up to (not including) <paramref name="endLine"/>, with the first of them
    /// at <paramref name="top"/>. Used when a paragraph is split across pages.
    /// </summary>
    public void Draw(PdfCanvas canvas, double x, double top, int firstLine, int endLine)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentOutOfRangeException.ThrowIfNegative(firstLine);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(endLine, Lines.Count);
        if (firstLine >= endLine)
        {
            return;
        }

        var offset = Lines[firstLine].Top;
        PdfColor? current = null;
        for (var i = firstLine; i < endLine; i++)
        {
            var line = Lines[i];
            var baseline = top - (line.Top - offset) - line.Baseline;

            // Inline elements' backgrounds and borders go under the line's content.
            foreach (var decoration in line.Decorations)
            {
                DrawDecoration(canvas, decoration, x + line.X, baseline);
                current = null;
            }

            foreach (var fragment in line.Fragments)
            {
                if (current != fragment.Style.Color)
                {
                    canvas.SetFillColor(fragment.Style.Color);
                    current = fragment.Style.Color;
                }

                canvas.DrawGlyphs(
                    fragment.Glyphs, fragment.Run.Font, fragment.Style.FontSize, x + line.X + fragment.X, baseline + fragment.BaselineShift,
                    fragment.Run.NeedsActualText ? fragment.Run.Text : null);
            }

            if (DrawDecorations(canvas, line, x + line.X, baseline))
            {
                current = null;
            }

            foreach (var image in line.Images)
            {
                var (left, bottom, width, height) = (x + line.X + image.X, baseline + image.BaselineShift, image.Image.Width, image.Image.Height);
                var translucent = image.Image.Opacity < 1;
                var rounded = !image.Image.BorderRadius.IsSquare;
                if (translucent)
                {
                    canvas.BeginGroup(image.Image.Opacity);
                }

                if (rounded)
                {
                    canvas.SaveState();
                    image.Image.BorderRadius.Resolve(width, height).Clip(canvas, left, bottom, width, height);
                }

                canvas.DrawImage(image.Image.Image, left, bottom, width, height);
                if (rounded)
                {
                    canvas.RestoreState();
                }

                if (translucent)
                {
                    canvas.EndGroup();
                }
            }

            foreach (var box in line.Boxes)
            {
                // The box's content was laid out at x = 0; move it to its place on the line.
                var left = x + line.X + box.X + box.Box.Content.Style.Margin.Left;
                canvas.SaveState().Transform(1, 0, 0, 1, left, 0);
                foreach (var draw in box.Content)
                {
                    draw(canvas, baseline + box.BaselineShift + box.Baseline);
                }

                canvas.RestoreState();
                current = null; // The box may have changed the fill color.
            }

            // Links cover the whole height of the line, so the rectangles of a wrapped link touch without overlapping.
            foreach (var link in line.Links)
            {
                link.Link.Target.AddTo(canvas, x + line.X + link.X0, baseline - (line.Height - line.Baseline), link.X1 - link.X0, line.Height);
            }
        }
    }
}

/// <summary>Drawing helpers for <see cref="ParagraphLayout"/>.</summary>
public sealed partial class ParagraphLayout
{
    /// <summary>
    /// Draws a line's text decorations: each decoration as one segment per run of adjacent fragments that share it.
    /// Returns whether anything was drawn (the fill color changed).
    /// </summary>
    private static bool DrawDecorations(PdfCanvas canvas, LineLayout line, double lineX, double baseline)
    {
        var drawn = false;
        var open = new Dictionary<TextDecoration, (double Start, double End, LineFragment First)>(ReferenceEqualityComparer.Instance);
        void Close(TextDecoration decoration)
        {
            var (start, end, first) = open[decoration];
            open.Remove(decoration);
            DrawDecorationLines(canvas, decoration, first, lineX + start, lineX + end, baseline);
            drawn = true;
        }

        foreach (var fragment in line.Fragments)
        {
            foreach (var decoration in open.Keys.Where(d => !fragment.Style.Decorations.Contains(d) || Math.Abs(open[d].End - fragment.X) > 0.01).ToList())
            {
                Close(decoration);
            }

            foreach (var decoration in fragment.Style.Decorations)
            {
                open[decoration] = open.TryGetValue(decoration, out var segment)
                    ? (segment.Start, fragment.X + fragment.Width, segment.First)
                    : (fragment.X, fragment.X + fragment.Width, fragment);
            }
        }

        foreach (var decoration in open.Keys.ToList())
        {
            Close(decoration);
        }

        return drawn;
    }

    private static void DrawDecorationLines(PdfCanvas canvas, TextDecoration decoration, LineFragment first, double left, double right, double baseline)
    {
        var font = decoration.Font ?? first.Run.Font;
        var size = decoration.FontSize > 0 ? decoration.FontSize : first.Style.FontSize;
        var y0 = baseline + decoration.BaselineShift;
        canvas.SetFillColor(decoration.Color).SetStrokeColor(decoration.Color);

        void Line(double top, double thickness, int direction)
        {
            // direction: where a second (double) line goes, -1 below, +1 above.
            var bottom = top - thickness;
            switch (decoration.Style)
            {
                case TextDecorationStyle.Double:
                    canvas.Rectangle(left, bottom, right - left, thickness).Fill();
                    canvas.Rectangle(left, bottom + (2 * thickness * direction), right - left, thickness).Fill();
                    break;
                case TextDecorationStyle.Dotted or TextDecorationStyle.Dashed:
                    var middle = top - (thickness / 2);
                    var dots = decoration.Style == TextDecorationStyle.Dotted;
                    canvas.SaveState().SetLineWidth(thickness)
                        .SetLineCap(dots ? LineCap.Round : LineCap.Butt)
                        .SetDash(dots ? [0, 2 * thickness] : [3 * thickness, 2 * thickness])
                        .MoveTo(left + (dots ? thickness / 2 : 0), middle).LineTo(right, middle).Stroke().RestoreState();
                    break;
                case TextDecorationStyle.Wavy:
                    var amplitude = thickness * 1.5;
                    var wave = Math.Max(4 * thickness, 2);
                    var center = top - (thickness / 2);
                    canvas.SaveState().SetLineWidth(thickness).SetLineCap(LineCap.Round).MoveTo(left, center);
                    for (var x = left; x < right; x += wave)
                    {
                        var end = Math.Min(right, x + wave);
                        var half = (end - x) / 2;
                        canvas.CurveTo(x + (half / 2), center + amplitude, x + half - (half / 2), center + amplitude, x + half, center)
                            .CurveTo(x + half + (half / 2), center - amplitude, end - (half / 2), center - amplitude, end, center);
                    }

                    canvas.Stroke().RestoreState();
                    break;
                default:
                    canvas.Rectangle(left, bottom, right - left, thickness).Fill();
                    break;
            }
        }

        if (decoration.Lines.HasFlag(TextDecorationLines.Underline))
        {
            var thickness = decoration.Thickness ?? Math.Max(font.UnderlineThickness * size, 0.5);
            var top = decoration.UnderlineOffset is { } offset ? y0 - offset : y0 + (font.UnderlinePosition * size);
            Line(top, thickness, -1);
        }

        if (decoration.Lines.HasFlag(TextDecorationLines.Overline))
        {
            var thickness = decoration.Thickness ?? Math.Max(font.UnderlineThickness * size, 0.5);
            Line(y0 + (font.Ascent * size), thickness, +1);
        }

        if (decoration.Lines.HasFlag(TextDecorationLines.LineThrough))
        {
            var thickness = decoration.Thickness ?? Math.Max(font.StrikeoutThickness * size, 0.5);
            Line(y0 + (font.StrikeoutPosition * size), thickness, +1);
        }
    }

    /// <summary>
    /// An inline element's piece with rounded corners: only the corners at the edges it has (the left ones on the
    /// element's first line, the right ones on its last), with the background and borders following them.
    /// </summary>
    private static void DrawRoundedDecoration(PdfCanvas canvas, InlineDecorationFragment piece, double x, double y, double width, double height)
    {
        var box = piece.Decoration.Box;
        var corners = box.BorderRadius.Resolve(width, height).Fragment(true, true, width, height, left: piece.StartEdge, right: piece.EndEdge);
        if (box.Background is { } background)
        {
            canvas.SetFillColor(background);
            corners.AddPath(canvas, x, y, width, height);
            canvas.Fill();
        }

        var border = box.Border with
        {
            Left = piece.StartEdge ? box.Border.Left : Blocks.BorderSide.None,
            Right = piece.EndEdge ? box.Border.Right : Blocks.BorderSide.None,
        };
        Blocks.BorderPainter.PaintBox(canvas, border, x, y, width, height, corners: corners);
    }

    private static void DrawDecoration(PdfCanvas canvas, InlineDecorationFragment piece, double lineX, double baseline)
    {
        var box = piece.Decoration.Box;
        var (left, right) = (lineX + piece.X0, lineX + piece.X1);
        var (bottom, top) = (baseline - piece.Below, baseline + piece.Above);
        if (!box.BorderRadius.IsSquare)
        {
            DrawRoundedDecoration(canvas, piece, left, bottom, right - left, top - bottom);
            return;
        }

        if (box.Background is { } background)
        {
            canvas.SetFillColor(background).Rectangle(left, bottom, right - left, top - bottom).Fill();
        }

        void Side(Blocks.BorderSide side, double sx, double sy, double sw, double sh)
        {
            if (side.Width > 0)
            {
                canvas.SetFillColor(side.Color).Rectangle(sx, sy, sw, sh).Fill();
            }
        }

        var border = box.Border;
        Side(border.Top, left, top - border.Top.Width, right - left, border.Top.Width);
        Side(border.Bottom, left, bottom, right - left, border.Bottom.Width);
        if (piece.StartEdge)
        {
            Side(border.Left, left, bottom, border.Left.Width, top - bottom);
        }

        if (piece.EndEdge)
        {
            Side(border.Right, right - border.Right.Width, bottom, border.Right.Width, top - bottom);
        }
    }
}

/// <summary>One line of a <see cref="ParagraphLayout"/>.</summary>
public sealed class LineLayout
{
    internal LineLayout(
        int start, int end, double top, double height, double baseline, double x, double width,
        IReadOnlyList<LineFragment> fragments, IReadOnlyList<InlineImageFragment> images, IReadOnlyList<InlineBoxFragment>? boxes = null,
        IReadOnlyList<InlineDecorationFragment>? decorations = null)
    {
        Decorations = decorations ?? [];
        Images = images;
        Boxes = boxes ?? [];
        Start = start;
        End = end;
        Top = top;
        Height = height;
        Baseline = baseline;
        X = x;
        Width = width;
        Fragments = fragments;
    }

    /// <summary>Index in the paragraph text where the line starts.</summary>
    public int Start { get; }

    /// <summary>Index in the paragraph text where the next line starts.</summary>
    public int End { get; }

    /// <summary>Distance from the top of the paragraph to the top of this line.</summary>
    public double Top { get; }

    /// <summary>Height of the line box.</summary>
    public double Height { get; }

    /// <summary>Distance from the top of the line box to the baseline.</summary>
    public double Baseline { get; }

    /// <summary>Horizontal offset of the line's content from the paragraph's left edge (indent and alignment).</summary>
    public double X { get; }

    /// <summary>Width of the content, without trailing spaces (and after justification).</summary>
    public double Width { get; }

    /// <summary>The pieces of text on this line, left to right.</summary>
    public IReadOnlyList<LineFragment> Fragments { get; }

    /// <summary>Inline images on this line.</summary>
    public IReadOnlyList<InlineImageFragment> Images { get; }

    /// <summary>Inline boxes on this line.</summary>
    public IReadOnlyList<InlineBoxFragment> Boxes { get; }

    /// <summary>The pieces of inline elements' boxes on this line, outer ones first.</summary>
    public IReadOnlyList<InlineDecorationFragment> Decorations { get; }

    /// <summary>The pieces of links (<see cref="Paragraph.BeginLink"/>) on this line.</summary>
    public IReadOnlyList<InlineLinkFragment> Links { get; internal init; } = [];
}

/// <summary>The piece of a link (<see cref="InlineLink"/>) on one line: it is clickable over the whole height of the line.</summary>
/// <param name="Link">The link.</param>
/// <param name="X0">Left edge, from the start of the line's content.</param>
/// <param name="X1">Right edge.</param>
public sealed record InlineLinkFragment(InlineLink Link, double X0, double X1);

/// <summary>The piece of an inline element's box (<see cref="InlineDecoration"/>) on one line.</summary>
/// <param name="Decoration">The element's box.</param>
/// <param name="X0">Left edge of the border box, from the start of the line's content.</param>
/// <param name="X1">Right edge of the border box.</param>
/// <param name="Above">Top of the border box above the line's baseline.</param>
/// <param name="Below">Bottom of the border box below the line's baseline.</param>
/// <param name="StartEdge">Its left border is drawn: a left-to-right element starts (a right-to-left one ends) here.</param>
/// <param name="EndEdge">Its right border is drawn: a left-to-right element ends (a right-to-left one starts) here.</param>
public sealed record InlineDecorationFragment(InlineDecoration Decoration, double X0, double X1, double Above, double Below, bool StartEdge, bool EndEdge);

/// <summary>An inline box on a line.</summary>
public sealed class InlineBoxFragment
{
    internal InlineBoxFragment(InlineBox box, double x, double width, double height, double baseline, IReadOnlyList<Blocks.DrawOperation> content, double baselineShift = 0)
    {
        BaselineShift = baselineShift;
        Box = box;
        X = x;
        Width = width;
        Height = height;
        Baseline = baseline;
        Content = content;
    }

    /// <summary>The inline box.</summary>
    public InlineBox Box { get; }

    /// <summary>Offset of the box's margin edge from the start of the line's content.</summary>
    public double X { get; }

    /// <summary>Width of the border box.</summary>
    public double Width { get; }

    /// <summary>Height of the border box.</summary>
    public double Height { get; }

    /// <summary>Distance from the top of the border box to the baseline it is aligned on.</summary>
    public double Baseline { get; }

    /// <summary>How far the box's baseline is above the line's baseline (CSS <c>vertical-align</c>).</summary>
    public double BaselineShift { get; }

    /// <summary>Width including the horizontal margins: the space it takes on the line.</summary>
    public double OuterWidth => Box.Content.Style.Margin.Horizontal + Width;

    /// <summary>Drawing operations with the border box's top-left corner at x = 0 and y = the top passed in.</summary>
    internal IReadOnlyList<Blocks.DrawOperation> Content { get; }
}

/// <summary>An inline image on a line.</summary>
/// <param name="Image">The image and its size.</param>
/// <param name="X">Offset from the start of the line's content.</param>
/// <param name="BaselineShift">How far the image's bottom is above the line's baseline (CSS <c>vertical-align</c>).</param>
public sealed record InlineImageFragment(InlineImage Image, double X, double BaselineShift = 0);

/// <summary>A run of glyphs on a line, in one style and font.</summary>
public sealed class LineFragment
{
    internal LineFragment(ShapedRun run, TextStyle style, IReadOnlyList<PositionedGlyph> glyphs, double x, double baselineShift = 0)
    {
        BaselineShift = baselineShift;
        Run = run;
        Style = style;
        Glyphs = glyphs;
        X = x;
        Width = glyphs.Sum(g => g.Advance) * style.FontSize;
    }

    /// <summary>The shaped run (font, script, direction, source text).</summary>
    public ShapedRun Run { get; }

    /// <summary>How far this text's baseline is above the line's baseline (CSS <c>vertical-align</c>).</summary>
    public double BaselineShift { get; }

    /// <summary>The style of the text.</summary>
    public TextStyle Style { get; }

    /// <summary>Glyphs to draw; justification may have widened spaces compared to <see cref="ShapedRun.Glyphs"/>.</summary>
    public IReadOnlyList<PositionedGlyph> Glyphs { get; }

    /// <summary>Offset from the start of the line's content.</summary>
    public double X { get; }

    /// <summary>Width in points.</summary>
    public double Width { get; }
}

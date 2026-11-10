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

            foreach (var image in line.Images)
            {
                canvas.DrawImage(image.Image.Image, x + line.X + image.X, baseline + image.BaselineShift, image.Image.Width, image.Image.Height);
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
        }
    }
}

/// <summary>Drawing helpers for <see cref="ParagraphLayout"/>.</summary>
public sealed partial class ParagraphLayout
{
    private static void DrawDecoration(PdfCanvas canvas, InlineDecorationFragment piece, double lineX, double baseline)
    {
        var box = piece.Decoration.Box;
        var (left, right) = (lineX + piece.X0, lineX + piece.X1);
        var (bottom, top) = (baseline - piece.Below, baseline + piece.Above);
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
}

/// <summary>The piece of an inline element's box (<see cref="InlineDecoration"/>) on one line.</summary>
/// <param name="Decoration">The element's box.</param>
/// <param name="X0">Left edge of the border box, from the start of the line's content.</param>
/// <param name="X1">Right edge of the border box.</param>
/// <param name="Above">Top of the border box above the line's baseline.</param>
/// <param name="Below">Bottom of the border box below the line's baseline.</param>
/// <param name="StartEdge">The element starts on this line: its left border is drawn.</param>
/// <param name="EndEdge">The element ends on this line: its right border is drawn.</param>
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

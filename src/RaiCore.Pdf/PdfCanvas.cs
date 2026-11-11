using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf;

/// <summary>How the ends of open stroked lines and dashes look (PDF line cap style).</summary>
public enum LineCap
{
    /// <summary>Square, ending exactly at the endpoint.</summary>
    Butt,

    /// <summary>A semicircle around the endpoint (round dots for zero-length dashes).</summary>
    Round,

    /// <summary>Square, extending half the line width past the endpoint.</summary>
    Square,
}

/// <summary>How corners of stroked paths look (PDF line join style).</summary>
public enum LineJoin
{
    /// <summary>Sharp corners, cut off by the miter limit.</summary>
    Miter,

    /// <summary>Rounded corners.</summary>
    Round,

    /// <summary>Flattened corners.</summary>
    Bevel,
}

/// <summary>
/// Draws on a page by building its content stream. Coordinates are in PDF points (1/72 inch)
/// with the origin in the bottom-left corner of the page.
/// </summary>
/// <remarks>Partial: feature areas may add drawing operations in their own files (<c>PdfCanvas.*.cs</c>).</remarks>
public sealed partial class PdfCanvas
{
    private readonly PdfPage _page;

    /// <summary>The content being drawn: the page's content stream, or the innermost open transparency group's.</summary>
    private StringBuilder _ops = new();

    internal PdfCanvas(PdfPage page)
    {
        _page = page;
        _resources = _pageResources;
        PageMarks = _marks;
    }

    /// <summary>Saves the graphics state (<c>q</c>), including the transformation, clip, colors and alpha. Pair with <see cref="RestoreState"/>.</summary>
    public PdfCanvas SaveState()
    {
        PushState();
        return StateOp("q");
    }

    /// <summary>Restores the graphics state (<c>Q</c>).</summary>
    public PdfCanvas RestoreState()
    {
        PopState();
        return StateOp("Q");
    }

    /// <summary>
    /// Sets the color used by fill operations and text. Its <see cref="PdfColor.Alpha"/> becomes the fill alpha
    /// (an extended graphics state with <c>/ca</c>, written only when the alpha changes), so translucent colors let
    /// what is underneath show through.
    /// </summary>
    public PdfCanvas SetFillColor(PdfColor color)
    {
        StateOp($"{N(color.Red)} {N(color.Green)} {N(color.Blue)} rg");
        return SetAlpha(color.Alpha, _strokeAlpha);
    }

    /// <summary>
    /// Sets the color used by stroke operations. Its <see cref="PdfColor.Alpha"/> becomes the stroke alpha (an
    /// extended graphics state with <c>/CA</c>, written only when the alpha changes).
    /// </summary>
    public PdfCanvas SetStrokeColor(PdfColor color)
    {
        StateOp($"{N(color.Red)} {N(color.Green)} {N(color.Blue)} RG");
        return SetAlpha(_fillAlpha, color.Alpha);
    }

    /// <summary>Sets the stroke line width in points.</summary>
    public PdfCanvas SetLineWidth(double width) => StateOp($"{N(width)} w");

    /// <summary>
    /// Sets the dash pattern for strokes: lengths of alternating dashes and gaps in points, starting
    /// <paramref name="phase"/> points into the pattern. An empty pattern draws solid lines.
    /// </summary>
    public PdfCanvas SetDash(IReadOnlyList<double> pattern, double phase = 0)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Any(length => length < 0) || (pattern.Count > 0 && pattern.All(length => length == 0)))
        {
            throw new ArgumentException("Dash lengths must be non-negative and not all zero.", nameof(pattern));
        }

        return StateOp($"[{string.Join(' ', pattern.Select(N))}] {N(phase)} d");
    }

    /// <summary>Sets how the ends of open lines and dashes are drawn.</summary>
    public PdfCanvas SetLineCap(LineCap cap) => StateOp($"{(int)cap} J");

    /// <summary>Sets how corners of stroked paths are drawn.</summary>
    public PdfCanvas SetLineJoin(LineJoin join) => StateOp($"{(int)join} j");

    /// <summary>Sets the miter limit: the longest miter, as a multiple of the line width, before a corner is beveled.</summary>
    public PdfCanvas SetMiterLimit(double limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return StateOp($"{N(limit)} M");
    }

    /// <summary>
    /// Concatenates a transformation matrix with the current one (<c>cm</c>): later coordinates map as
    /// x' = a·x + c·y + e and y' = b·x + d·y + f. Use <see cref="SaveState"/>/<see cref="RestoreState"/> to undo it.
    /// </summary>
    public PdfCanvas Transform(double a, double b, double c, double d, double e, double f)
    {
        ConcatTransform(a, b, c, d, e, f);
        return StateOp($"{N(a)} {N(b)} {N(c)} {N(d)} {N(e)} {N(f)} cm");
    }

    /// <summary>Starts a new subpath at the given point.</summary>
    public PdfCanvas MoveTo(double x, double y) => Op($"{N(x)} {N(y)} m");

    /// <summary>Adds a straight line to the current subpath.</summary>
    public PdfCanvas LineTo(double x, double y) => Op($"{N(x)} {N(y)} l");

    /// <summary>Adds a cubic Bézier curve to the current subpath.</summary>
    public PdfCanvas CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) =>
        Op($"{N(x1)} {N(y1)} {N(x2)} {N(y2)} {N(x3)} {N(y3)} c");

    /// <summary>Closes the current subpath.</summary>
    public PdfCanvas ClosePath() => Op("h");

    /// <summary>Adds a rectangle with its bottom-left corner at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public PdfCanvas Rectangle(double x, double y, double width, double height) =>
        Op($"{N(x)} {N(y)} {N(width)} {N(height)} re");

    /// <summary>Fills the current path (nonzero winding rule).</summary>
    public PdfCanvas Fill() => Op("f");

    /// <summary>Strokes the current path.</summary>
    public PdfCanvas Stroke() => Op("S");

    /// <summary>Fills and then strokes the current path.</summary>
    public PdfCanvas FillAndStroke() => Op("B");

    /// <summary>Fills the current path with the even-odd rule (holes where subpaths overlap an even number of times).</summary>
    public PdfCanvas FillEvenOdd() => Op("f*");

    /// <summary>Fills the current path with the even-odd rule and then strokes it.</summary>
    public PdfCanvas FillAndStrokeEvenOdd() => Op("B*");

    /// <summary>
    /// Intersects the clipping region with the current path and ends the path without painting it. Everything drawn
    /// afterwards is clipped until <see cref="RestoreState"/>.
    /// </summary>
    public PdfCanvas Clip(bool evenOdd = false) => Op(evenOdd ? "W* n" : "W n");

    /// <summary>Ends the current path without painting it.</summary>
    public PdfCanvas EndPath() => Op("n");

    /// <summary>
    /// Draws a single line of text with its baseline starting at (<paramref name="x"/>, <paramref name="y"/>),
    /// using the current fill color (and its alpha). Characters the font lacks are drawn as its "missing glyph" box.
    /// </summary>
    public PdfCanvas DrawText(string text, PdfFont font, double size, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        var (resourceName, usage) = _page.UseFont(font);
        UseResource(resourceName);
        var operand = font.EncodeText(text, usage).ToString();
        return Op($"BT /{resourceName} {N(size)} Tf {N(x)} {N(y)} Td {operand} Tj ET");
    }

    /// <summary>Draws text in one of the 14 standard fonts. See <see cref="DrawText(string, PdfFont, double, double, double)"/>.</summary>
    public PdfCanvas DrawText(string text, StandardFont font, double size, double x, double y) =>
        DrawText(text, PdfFont.Standard(font), size, x, y);

    /// <summary>
    /// Draws an image scaled to fill the rectangle with its bottom-left corner at (<paramref name="x"/>, <paramref name="y"/>).
    /// The EXIF orientation of JPEG photos is applied, so the image appears upright. The image is drawn opaque (with
    /// its own transparency, if any), whatever the alpha of the fill color; draw it inside
    /// <see cref="BeginGroup"/>/<see cref="EndGroup"/> to make it translucent.
    /// </summary>
    public PdfCanvas DrawImage(PdfImage image, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(image);
        var name = UseResource(_page.UseImage(image));
        var (a, b, c, d, e, f) = image.GetMatrix(x, y, width, height);
        return Op($"q {OpaqueImagePrefix()}{N(a)} {N(b)} {N(c)} {N(d)} {N(e)} {N(f)} cm /{name} Do Q");
    }

    /// <summary>
    /// Draws shaped glyphs in visual (left-to-right) order, starting with the pen at
    /// (<paramref name="x"/>, <paramref name="y"/>) on the baseline. Requires a font loaded with <see cref="PdfFont.Load(string, int)"/>.
    /// </summary>
    /// <param name="glyphs">The glyphs; positions are fractions of <paramref name="size"/>.</param>
    /// <param name="font">An embedded (TrueType/OpenType) font.</param>
    /// <param name="size">Font size in points.</param>
    /// <param name="x">Pen start x.</param>
    /// <param name="y">Baseline y.</param>
    /// <param name="actualText">
    /// The logical text of the whole run, for scripts where glyphs do not map one-to-one to characters in order
    /// (Arabic, Indic). Text extraction then returns this instead of the per-glyph mapping.
    /// </param>
    public PdfCanvas DrawGlyphs(IReadOnlyList<PositionedGlyph> glyphs, PdfFont font, double size, double x, double y, string? actualText = null)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        if (font is not Fonts.OpenTypePdfFont openType)
        {
            throw new NotSupportedException("Drawing glyphs by ID requires an embedded font loaded with PdfFont.Load; the standard fonts are addressed by character code.");
        }

        openType.EnsureEmbeddable();
        var (resourceName, usage) = _page.UseFont(font);
        UseResource(resourceName);
        var op = new StringBuilder();
        if (actualText is not null)
        {
            op.Append("/Span <</ActualText ").Append(PdfString.FromText(actualText).ToString()).Append(">> BDC ");
        }

        op.Append("BT /").Append(resourceName).Append(' ').Append(N(size)).Append(" Tf");

        // Glyphs are grouped into TJ arrays. Kerning becomes a TJ adjustment; a glyph with an offset (e.g. a mark)
        // starts a new segment at its exact position, and the glyph after it starts another one.
        var penX = x;
        var segmentOpen = false;
        var restart = true;
        foreach (var glyph in glyphs)
        {
            var hasOffset = glyph.OffsetX != 0 || glyph.OffsetY != 0;
            if (restart || hasOffset)
            {
                if (segmentOpen)
                {
                    op.Append("] TJ");
                }

                op.Append(" 1 0 0 1 ").Append(N(penX + (glyph.OffsetX * size))).Append(' ').Append(N(y + (glyph.OffsetY * size))).Append(" Tm [");
                segmentOpen = true;
            }

            op.Append('<').Append(((ushort)glyph.GlyphId).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)).Append('>');
            usage.Add(glyph.GlyphId, glyph.Text);

            restart = hasOffset;
            var adjustment = glyph.Advance - openType.GetGlyphAdvance(glyph.GlyphId);
            if (!restart && Math.Abs(adjustment) > 1e-6)
            {
                // TJ numbers are thousandths of the font size, subtracted from the pen position.
                op.Append(' ').Append(N(-adjustment * 1000)).Append(' ');
            }

            penX += glyph.Advance * size;
        }

        if (segmentOpen)
        {
            op.Append("] TJ");
        }

        op.Append(" ET");
        if (actualText is not null)
        {
            op.Append(" EMC");
        }

        return Op(op.ToString());
    }

    /// <summary>The page's content stream.</summary>
    /// <exception cref="InvalidOperationException">A transparency group is still open.</exception>
    internal byte[] ToBytes()
    {
        EnsureGroupsClosed();
        CloseMarkedContent();
        return Encoding.Latin1.GetBytes(_ops.ToString());
    }

    /// <summary>
    /// Appends a graphics state operator (<c>q</c>, <c>cm</c>, colors, line styles): not content itself, so in a tagged
    /// document it does not open a marked-content sequence and may fall in any.
    /// </summary>
    private PdfCanvas StateOp(string op)
    {
        _ops.Append(op).Append('\n');
        return this;
    }

    /// <summary>Appends a path or painting operator: in a tagged document, it is marked with the current tag.</summary>
    private PdfCanvas Op(string op)
    {
        if (Tagging)
        {
            MarkContent();
        }

        _ops.Append(op).Append('\n');
        return this;
    }

    private static string N(double value) => PdfNumberFormat.Format(value);
}

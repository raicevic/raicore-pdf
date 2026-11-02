using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf;

/// <summary>
/// Draws on a page by building its content stream. Coordinates are in PDF points (1/72 inch)
/// with the origin in the bottom-left corner of the page.
/// </summary>
public sealed class PdfCanvas
{
    private readonly StringBuilder _ops = new();
    private readonly PdfPage _page;

    internal PdfCanvas(PdfPage page) => _page = page;

    /// <summary>Saves the graphics state (<c>q</c>). Pair with <see cref="RestoreState"/>.</summary>
    public PdfCanvas SaveState() => Op("q");

    /// <summary>Restores the graphics state (<c>Q</c>).</summary>
    public PdfCanvas RestoreState() => Op("Q");

    /// <summary>Sets the color used by fill operations and text.</summary>
    public PdfCanvas SetFillColor(PdfColor color) => Op($"{N(color.Red)} {N(color.Green)} {N(color.Blue)} rg");

    /// <summary>Sets the color used by stroke operations.</summary>
    public PdfCanvas SetStrokeColor(PdfColor color) => Op($"{N(color.Red)} {N(color.Green)} {N(color.Blue)} RG");

    /// <summary>Sets the stroke line width in points.</summary>
    public PdfCanvas SetLineWidth(double width) => Op($"{N(width)} w");

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

    /// <summary>
    /// Draws a single line of text with its baseline starting at (<paramref name="x"/>, <paramref name="y"/>),
    /// using the current fill color. Characters the font lacks are drawn as its "missing glyph" box.
    /// </summary>
    public PdfCanvas DrawText(string text, PdfFont font, double size, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        var (resourceName, usage) = _page.UseFont(font);
        var operand = font.EncodeText(text, usage).ToString();
        return Op($"BT /{resourceName} {N(size)} Tf {N(x)} {N(y)} Td {operand} Tj ET");
    }

    /// <summary>Draws text in one of the 14 standard fonts. See <see cref="DrawText(string, PdfFont, double, double, double)"/>.</summary>
    public PdfCanvas DrawText(string text, StandardFont font, double size, double x, double y) =>
        DrawText(text, PdfFont.Standard(font), size, x, y);

    /// <summary>
    /// Draws an image scaled to fill the rectangle with its bottom-left corner at (<paramref name="x"/>, <paramref name="y"/>).
    /// The EXIF orientation of JPEG photos is applied, so the image appears upright.
    /// </summary>
    public PdfCanvas DrawImage(PdfImage image, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(image);
        var name = _page.UseImage(image);
        var (a, b, c, d, e, f) = image.GetMatrix(x, y, width, height);
        return Op($"q {N(a)} {N(b)} {N(c)} {N(d)} {N(e)} {N(f)} cm /{name} Do Q");
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

    internal byte[] ToBytes() => Encoding.Latin1.GetBytes(_ops.ToString());

    private PdfCanvas Op(string op)
    {
        _ops.Append(op).Append('\n');
        return this;
    }

    private static string N(double value) => PdfNumberFormat.Format(value);
}

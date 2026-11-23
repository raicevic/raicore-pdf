using System.Text;
using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf;

/// <summary>Operations used to draw metafiles (WMF, EMF) into a form XObject.</summary>
public sealed partial class PdfCanvas
{
    /// <summary>
    /// Draws text whose characters have given advances (a metafile's character spacing): each cluster (a character,
    /// or a surrogate pair) is moved to its place with a <c>TJ</c> adjustment, so the text stays one extractable run.
    /// </summary>
    /// <param name="clusters">The characters and how far the pen moves after each, in text space units (as the size).</param>
    /// <param name="font">The font.</param>
    /// <param name="size">The font size.</param>
    /// <param name="x">Pen start x.</param>
    /// <param name="y">Baseline y.</param>
    internal PdfCanvas DrawTextRun(IReadOnlyList<(string Text, double Advance)> clusters, PdfFont font, double size, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(font);
        if (clusters.Count == 0 || size == 0)
        {
            return this;
        }

        var (resourceName, usage) = _page.UseFont(font);
        UseResource(resourceName);
        var width = 0.0;
        var op = new StringBuilder();
        op.Append("BT /").Append(resourceName).Append(' ').Append(N(size)).Append(" Tf ").Append(N(x)).Append(' ').Append(N(y)).Append(" Td [");
        foreach (var (text, advance) in clusters)
        {
            if (font is Fonts.OpenTypePdfFont)
            {
                NotePrivateUse(text);
            }

            op.Append(font.EncodeText(text, usage).ToString());
            var natural = font.CanMeasure ? font.MeasureText(text, size) : size;
            var adjustment = (natural - advance) / size * 1000;
            if (Math.Abs(adjustment) > 0.01 && double.IsFinite(adjustment))
            {
                // TJ numbers are thousandths of the font size, subtracted from the pen position.
                op.Append(' ').Append(N(adjustment)).Append(' ');
            }

            width += advance;
        }

        op.Append("] TJ ET");
        Painting("text", TracksBounds ? TextBounds(font, size, x, y, Math.Min(0, width), Math.Max(0, width)) : null);
        return Op(op.ToString());
    }
}

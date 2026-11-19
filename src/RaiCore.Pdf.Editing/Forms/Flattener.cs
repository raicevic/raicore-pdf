using System.Text;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Editing;

/// <summary>
/// Draws annotations into their pages' content and removes them (ISO 32000-2, 12.5.5): each annotation's appearance
/// stream becomes a form XObject of the page, drawn at the annotation rectangle.
/// </summary>
internal sealed class Flattener(ObjectTable table, IReadOnlyList<PdfReference> pages)
{
    // Annotation flags (ISO 32000-2, table 167).
    private const int Hidden = 1 << 1;
    private const int NoView = 1 << 5;

    /// <summary>
    /// Flattens the annotations <paramref name="select"/> chooses: hidden ones are removed without being drawn, the others
    /// drawn with the appearance <paramref name="appearance"/> gives (and whether it was made rather than found) and removed;
    /// one without an appearance is removed.
    /// </summary>
    public PdfFlattenResult Flatten(Func<PdfDictionary, bool> select, Func<PdfDictionary, (PdfReference? Form, bool Generated)> appearance)
    {
        int drawn = 0, generated = 0, removed = 0;
        foreach (var page in pages.Select(table.Dictionary).OfType<PdfDictionary>())
        {
            if (table.Array(page["Annots"]) is not { } annotations)
            {
                continue;
            }

            var kept = new PdfArray();
            var drawing = new StringBuilder();
            foreach (var item in annotations.Items)
            {
                if (table.Dictionary(item) is not { } annotation || !select(annotation))
                {
                    kept.Items.Add(item);
                    continue;
                }

                var flags = table.Resolve(annotation["F"]) is PdfInteger f ? f.Value : 0;
                var (form, made) = (flags & (Hidden | NoView)) != 0 ? (null, false) : appearance(annotation);
                if (form is null || !Draw(page, annotation, form, drawing))
                {
                    removed++;
                    continue;
                }

                drawn++;
                generated += made ? 1 : 0;
            }

            page["Annots"] = kept.Items.Count > 0 ? kept : null;
            if (drawing.Length > 0)
            {
                AppendContent(page, drawing.ToString());
            }
        }

        return new PdfFlattenResult(drawn, generated, removed);
    }

    /// <summary>
    /// Adds the drawing of an appearance at an annotation's rectangle (the appearance's box, after its matrix, scaled and
    /// moved onto the rectangle). Returns false when the rectangle or box is empty.
    /// </summary>
    private bool Draw(PdfDictionary page, PdfDictionary annotation, PdfReference reference, StringBuilder drawing)
    {
        if (Rectangle(annotation["Rect"]) is not { Width: > 0, Height: > 0 } rect || table.Resolve(reference) is not PdfStreamObject form)
        {
            return false;
        }

        var box = Rectangle(form.Dictionary["BBox"]) ?? new PdfRectangle(0, 0, rect.Width, rect.Height);
        var m = table.Array(form.Dictionary["Matrix"]) is { Items.Count: 6 } matrix && matrix.Items.Select(i => Number(table.Resolve(i))).ToArray() is [{ } a, { } b, { } c, { } d, { } e, { } f0]
            ? (a, b, c, d, e, f0)
            : (1.0, 0.0, 0.0, 1.0, 0.0, 0.0);
        (double X, double Y) Map(double x, double y) => ((m.Item1 * x) + (m.Item3 * y) + m.Item5, (m.Item2 * x) + (m.Item4 * y) + m.Item6);
        var corners = new[] { Map(box.X, box.Y), Map(box.Right, box.Y), Map(box.Right, box.Top), Map(box.X, box.Top) };
        var (left, bottom) = (corners.Min(p => p.X), corners.Min(p => p.Y));
        var (width, height) = (corners.Max(p => p.X) - left, corners.Max(p => p.Y) - bottom);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        // The appearance must be a form XObject with a box (most are; some producers leave out /Type or /Subtype).
        form.Dictionary["Type"] = new PdfName("XObject");
        form.Dictionary["Subtype"] = new PdfName("Form");
        form.Dictionary["BBox"] ??= PdfArray.OfNumbers(box.X, box.Y, box.Right, box.Top);

        var resources = table.Dictionary(page["Resources"]);
        if (resources is null)
        {
            resources = new PdfDictionary();
            page["Resources"] = resources;
        }

        var forms = table.Dictionary(resources["XObject"]);
        if (forms is null)
        {
            forms = new PdfDictionary();
            resources["XObject"] = forms;
        }

        var name = Enumerable.Range(1, int.MaxValue).Select(i => $"Flat{i}").First(n => forms[n] is null);
        forms[name] = reference;

        var sx = rect.Width / width;
        var sy = rect.Height / height;
        drawing.Append(CultureInvariant($"q {N(sx)} 0 0 {N(sy)} {N(rect.X - (sx * left))} {N(rect.Y - (sy * bottom))} cm /{name} Do Q\n"));
        return true;
    }

    /// <summary>
    /// Adds drawing after a page's content, which is wrapped in <c>q</c> ... <c>Q</c> first so that a graphics state it
    /// leaves changed does not move the drawing.
    /// </summary>
    private void AppendContent(PdfDictionary page, string drawing)
    {
        var contents = new PdfArray();
        switch (table.Resolve(page["Contents"]))
        {
            case PdfStreamObject:
                contents.Items.Add(page["Contents"]!);
                break;
            case PdfArray existing:
                foreach (var item in existing.Items)
                {
                    contents.Items.Add(item);
                }

                break;
        }

        if (contents.Items.Count > 0)
        {
            contents.Items.Insert(0, table.Add(new PdfStreamObject("q\n"u8.ToArray())));
            drawing = "Q\n" + drawing;
        }

        contents.Items.Add(table.Add(PdfStreamObject.CreateCompressed(Encoding.ASCII.GetBytes(drawing))));
        page["Contents"] = contents;
    }

    private PdfRectangle? Rectangle(PdfObject? value) =>
        table.Array(value) is { Items.Count: 4 } array && array.Items.Select(i => Number(table.Resolve(i))).ToArray() is [{ } x1, { } y1, { } x2, { } y2]
            ? new PdfRectangle(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1))
            : null;

    private static double? Number(PdfObject? obj) => obj switch
    {
        PdfInteger i => i.Value,
        PdfReal r when double.IsFinite(r.Value) => r.Value,
        _ => null,
    };

    private static string N(double value) => PdfNumberFormat.Format(value);

    private static string CultureInvariant(FormattableString text) => FormattableString.Invariant(text);
}

/// <summary>What flattening did: how many annotations were drawn into their pages and removed.</summary>
/// <param name="Drawn">How many were drawn into their page's content (and removed).</param>
/// <param name="Generated">How many of those were drawn with an appearance made for them, because they had none (or the form asked for new ones).</param>
/// <param name="Removed">How many were removed without being drawn: hidden ones, and ones without an appearance that could be made.</param>
public sealed record PdfFlattenResult(int Drawn, int Generated, int Removed);

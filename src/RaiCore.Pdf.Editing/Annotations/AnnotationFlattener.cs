using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Editing;

/// <summary>
/// Flattens annotations other than form widgets (see <see cref="PdfEditor.FlattenAnnotations"/>): draws the normal
/// appearance of each one that is shown and printed into its page and removes it, with the pop-up window that belongs to it.
/// </summary>
internal sealed class AnnotationFlattener(ObjectTable table, IReadOnlyList<PdfReference> pages, PdfAnnotationFlattenOptions options)
{
    public PdfFlattenResult Flatten()
    {
        var flattener = new Flattener(table, pages);
        return flattener.Flatten(Selected, annotation => (flattener.NormalAppearance(annotation), false), printedOnly: true);
    }

    /// <summary>Whether an annotation is flattened: by its type and the options; a pop-up goes with its parent.</summary>
    private bool Selected(PdfDictionary annotation)
    {
        var subtype = (table.Resolve(annotation["Subtype"]) as PdfName)?.Value;
        return subtype switch
        {
            null or "Widget" => false,
            "Popup" => table.Dictionary(annotation["Parent"]) is { } parent
                && table.Resolve(parent["Subtype"]) is PdfName { Value: not "Popup" }
                && Selected(parent),
            "Link" when options.KeepLinks => false,
            _ => options.Types is not { } types || types.Contains(subtype),
        };
    }
}

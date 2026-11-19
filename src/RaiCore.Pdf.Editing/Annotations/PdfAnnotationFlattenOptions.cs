namespace RaiCore.Pdf.Editing;

/// <summary>Which annotations <see cref="PdfEditor.FlattenAnnotations"/> flattens. By default all but links and form fields.</summary>
public sealed record PdfAnnotationFlattenOptions
{
    /// <summary>All annotations but links (and form fields, which <see cref="PdfEditor.FlattenForm"/> flattens).</summary>
    public static PdfAnnotationFlattenOptions Default { get; } = new();

    /// <summary>
    /// The annotation types to flatten, by their PDF names (<c>Highlight</c>, <c>Ink</c>, <c>FreeText</c>, <c>Stamp</c>,
    /// <c>Square</c>, <c>Text</c> and so on; ISO 32000-2, table 171), or <see langword="null"/> (the default) for all. A
    /// pop-up window goes with the annotation it belongs to; <c>Widget</c> never counts.
    /// </summary>
    public IReadOnlySet<string>? Types { get; init; }

    /// <summary>
    /// Keep links (the default) so that they still work; turn off to flatten them too (most links have no appearance and
    /// are then just removed).
    /// </summary>
    public bool KeepLinks { get; init; } = true;
}

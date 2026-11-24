namespace RaiCore.Pdf.Editing;

/// <summary>What <see cref="PdfEditor.FlattenForm(PdfFormFlattenOptions)"/> does with a dynamic XFA form.</summary>
public enum PdfDynamicXfaHandling
{
    /// <summary>Throw <see cref="NotSupportedException"/>: the pages of a dynamic XFA form are laid out by the viewer.</summary>
    Throw,

    /// <summary>
    /// Remove the XFA (as <see cref="PdfEditor.RemoveXfa"/> does) and flatten the AcroForm behind it, so the result shows
    /// what viewers without XFA show. A form whose pages are only a placeholder (<see cref="PdfForm.IsXfaPlaceholder"/>)
    /// still throws, since nothing of the form would be left.
    /// </summary>
    RemoveXfa,

    /// <summary>
    /// Lay the XFA form out with <see cref="PdfFormFlattenOptions.XfaRenderer"/> (such as <c>XfaToPdf.Convert</c> from
    /// the <c>RaiCore.Pdf.Xfa</c> package) and replace the document's pages, often only a placeholder, with the result;
    /// the XFA and the form go. This also works where <see cref="PdfForm.IsXfaPlaceholder"/> is set.
    /// </summary>
    Render,
}

/// <summary>How <see cref="PdfEditor.FlattenForm(PdfFormFlattenOptions)"/> flattens a form.</summary>
public sealed record PdfFormFlattenOptions
{
    /// <summary>The defaults: a dynamic XFA form throws.</summary>
    public static PdfFormFlattenOptions Default { get; } = new();

    /// <summary>
    /// What to do with a dynamic XFA form (<see cref="PdfXfaKind.Dynamic"/>): throw (the default) or remove the XFA and
    /// flatten the AcroForm. A static XFA form is always removed with the form.
    /// </summary>
    public PdfDynamicXfaHandling DynamicXfa { get; init; }

    /// <summary>
    /// Lays out a dynamic XFA form for <see cref="PdfDynamicXfaHandling.Render"/>: it gets the document as saved now
    /// (unencrypted) and returns the rendered pages, for example <c>bytes =&gt; XfaToPdf.Convert(bytes)</c> from the
    /// <c>RaiCore.Pdf.Xfa</c> package (which this package does not depend on).
    /// </summary>
    public Func<byte[], PdfDocument>? XfaRenderer { get; init; }
}

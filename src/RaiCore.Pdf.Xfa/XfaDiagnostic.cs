namespace RaiCore.Pdf.Xfa;

/// <summary>
/// Something in an XFA form that the converter did not render as a viewer with XFA support would (reported through
/// <see cref="XfaConverterOptions.Diagnostics"/>).
/// </summary>
/// <param name="Kind">What kind of issue it is.</param>
/// <param name="Message">A description, naming the form object where there is one (its SOM-like path, such as <c>form1.Page1.Name</c>).</param>
public sealed record XfaDiagnostic(XfaDiagnosticKind Kind, string Message);

/// <summary>The kinds of <see cref="XfaDiagnostic"/>.</summary>
public enum XfaDiagnosticKind
{
    /// <summary>
    /// Scripts (FormCalc or JavaScript: calculations, validations and events such as <c>initialize</c>) are never run.
    /// What they would have done (computed values, shown or hidden objects, added instances) is missing from the output.
    /// </summary>
    ScriptNotRun,

    /// <summary>
    /// Content the converter does not support, left out or drawn simplified: barcodes (drawn as their text), signatures
    /// (drawn as their box), prototypes it cannot resolve, and the like.
    /// </summary>
    UnsupportedContent,

    /// <summary>An image that cannot be drawn (a linked image, an unknown format or damaged data); it is left out.</summary>
    ImageNotLoaded,

    /// <summary>
    /// Content that does not fit where it is laid out: an object taller than a whole content area (drawn on its own page
    /// and cut off), or a form with no page set (drawn on Letter pages).
    /// </summary>
    Layout,

    /// <summary>Data the form's template does not take: data values no field is bound to, and bindings that find no data.</summary>
    Data,

    /// <summary>A typeface the fonts do not have, drawn with a substitute (once per typeface).</summary>
    FontSubstituted,
}

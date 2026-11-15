namespace RaiCore.Pdf.Html;

/// <summary>
/// Something in an HTML document that the converter could not render as a browser would (see
/// <see cref="HtmlConverterOptions.Diagnostics"/>): a resource that did not load, CSS it does not support, content that
/// does not fit. Each distinct issue is reported once per conversion.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Severity">Whether the output differs (<see cref="HtmlDiagnosticSeverity.Warning"/>) or not.</param>
/// <param name="Subject">What it is about: a URL, a CSS property or value, a font family.</param>
/// <param name="Message">A sentence describing the issue.</param>
/// <param name="ElementPath">
/// Where it first occurred, as a CSS-like path such as <c>html &gt; body &gt; div.card:nth-of-type(2) &gt; img#logo</c>;
/// <c>null</c> when it concerns no element (a font, a style sheet's rule).
/// </param>
/// <param name="Count">How many elements it concerns (at least 1).</param>
public sealed record HtmlDiagnostic(
    HtmlDiagnosticKind Kind, HtmlDiagnosticSeverity Severity, string Subject, string Message, string? ElementPath, int Count);

/// <summary>The kinds of <see cref="HtmlDiagnostic"/>.</summary>
public enum HtmlDiagnosticKind
{
    /// <summary>A resource (image, style sheet, font) could not be loaded: the resource loader returned nothing.</summary>
    ResourceNotLoaded,

    /// <summary>A resource was loaded but could not be read: not an image or font the converter understands, or damaged.</summary>
    ResourceInvalid,

    /// <summary>A resource exceeded a per-resource limit (<see cref="ResourceLimits"/>), such as an image too large to decode.</summary>
    ResourceTooLarge,

    /// <summary>A web font that may not be embedded (its license forbids it), so a fallback font was used.</summary>
    FontNotEmbeddable,
}

/// <summary>How much an <see cref="HtmlDiagnostic"/> matters.</summary>
public enum HtmlDiagnosticSeverity
{
    /// <summary>It does not change the output (e.g. CSS that has no effect in print).</summary>
    Info,

    /// <summary>The output differs from what a browser would print.</summary>
    Warning,
}

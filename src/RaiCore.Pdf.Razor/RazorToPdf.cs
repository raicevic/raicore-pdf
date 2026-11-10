using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using RaiCore.Pdf.Html;

namespace RaiCore.Pdf.Razor;

/// <summary>
/// Converts Razor components to PDF without dependency injection. Use <see cref="RazorPdfRenderer"/> when components
/// need to <c>@inject</c> services.
/// </summary>
/// <example>
/// <code>
/// var pdf = RazorToPdf.Convert&lt;InvoiceDocument&gt;(new Dictionary&lt;string, object?&gt;
/// {
///     [nameof(InvoiceDocument.Invoice)] = invoice,
/// });
/// pdf.Save("invoice.pdf");
/// </code>
/// </example>
public static class RazorToPdf
{
    private static readonly RazorPdfRenderer Renderer = new();

    /// <summary>Renders a component and converts the HTML to a PDF document.</summary>
    /// <typeparam name="TComponent">The component, typically a <c>.razor</c> file that renders a whole HTML document.</typeparam>
    /// <param name="parameters">Values for the component's <c>[Parameter]</c> properties, keyed by property name.</param>
    /// <param name="options">HTML conversion options: fonts, default page setup, base URI for images and stylesheets.</param>
    public static PdfDocument Convert<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(
        IReadOnlyDictionary<string, object?>? parameters = null,
        HtmlConverterOptions? options = null)
        where TComponent : IComponent =>
        Task.Run(() => Renderer.ConvertAsync<TComponent>(parameters, options)).GetAwaiter().GetResult();

    /// <summary>Renders a component and converts the HTML to PDF bytes.</summary>
    /// <typeparam name="TComponent">The component, typically a <c>.razor</c> file that renders a whole HTML document.</typeparam>
    /// <param name="parameters">Values for the component's <c>[Parameter]</c> properties, keyed by property name.</param>
    /// <param name="options">HTML conversion options: fonts, default page setup, base URI for images and stylesheets.</param>
    public static byte[] ConvertToBytes<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(
        IReadOnlyDictionary<string, object?>? parameters = null,
        HtmlConverterOptions? options = null)
        where TComponent : IComponent =>
        Convert<TComponent>(parameters, options).ToArray();

    /// <summary>Renders a component and converts the HTML to a PDF document.</summary>
    /// <typeparam name="TComponent">The component, typically a <c>.razor</c> file that renders a whole HTML document.</typeparam>
    /// <param name="parameters">Values for the component's <c>[Parameter]</c> properties, keyed by property name.</param>
    /// <param name="options">HTML conversion options: fonts, default page setup, base URI for images and stylesheets.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static Task<PdfDocument> ConvertAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(
        IReadOnlyDictionary<string, object?>? parameters = null,
        HtmlConverterOptions? options = null,
        CancellationToken cancellationToken = default)
        where TComponent : IComponent =>
        Renderer.ConvertAsync<TComponent>(parameters, options, cancellationToken);

    /// <summary>Renders a component to an HTML string, e.g. to inspect what will be converted.</summary>
    /// <typeparam name="TComponent">The component to render.</typeparam>
    /// <param name="parameters">Values for the component's <c>[Parameter]</c> properties, keyed by property name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static Task<string> RenderHtmlAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
        where TComponent : IComponent =>
        Renderer.RenderHtmlAsync<TComponent>(parameters, cancellationToken);
}

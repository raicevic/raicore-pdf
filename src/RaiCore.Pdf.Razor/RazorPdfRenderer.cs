using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Razor;

/// <summary>
/// Renders Razor components to HTML with Blazor's static <see cref="HtmlRenderer"/> and converts the HTML to PDF with
/// <see cref="HtmlToPdf"/>. No browser, web server or HTTP request is involved.
/// </summary>
/// <remarks>
/// <para>
/// Components can <c>@inject</c> anything registered in the <see cref="IServiceProvider"/> passed to the constructor.
/// In an ASP.NET Core or generic-host app, register the renderer with
/// <see cref="RazorPdfServiceCollectionExtensions.AddRazorPdf"/> and inject it; it is scoped, so components see the
/// same scoped services (e.g. a <c>DbContext</c>) as the code that requests the PDF.
/// </para>
/// <para>
/// Rendering waits until every component has finished its asynchronous initialization (<c>OnInitializedAsync</c>),
/// so components may load their own data. JavaScript interop and event handlers do nothing in a static render, and
/// interactive render modes are not supported.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var renderer = new RazorPdfRenderer();
/// var pdf = await renderer.ConvertAsync&lt;InvoiceDocument&gt;(new Dictionary&lt;string, object?&gt;
/// {
///     [nameof(InvoiceDocument.Invoice)] = invoice,
/// });
/// pdf.Save("invoice.pdf");
/// </code>
/// </example>
public sealed class RazorPdfRenderer
{
    private readonly IServiceProvider services;
    private readonly ILoggerFactory loggerFactory;

    /// <summary>Creates a renderer.</summary>
    /// <param name="services">
    /// Services that components can inject. Defaults to an empty container, so components that inject services fail
    /// to render.
    /// </param>
    /// <param name="loggerFactory">Receives the renderer's diagnostics. Defaults to <see cref="NullLoggerFactory"/>.</param>
    public RazorPdfRenderer(IServiceProvider? services = null, ILoggerFactory? loggerFactory = null)
    {
        this.services = services ?? EmptyServices.Instance;
        this.loggerFactory = loggerFactory ?? services?.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
    }

    /// <summary>Renders a component to an HTML string.</summary>
    /// <typeparam name="TComponent">The component, typically a <c>.razor</c> file that renders a whole HTML document.</typeparam>
    /// <param name="parameters">Values for the component's <c>[Parameter]</c> properties, keyed by property name.</param>
    /// <param name="cancellationToken">Cancels the operation before or after rendering (a render in progress runs to completion).</param>
    public Task<string> RenderHtmlAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
        where TComponent : IComponent =>
        RenderHtmlAsync(typeof(TComponent), parameters, cancellationToken);

    /// <summary>Renders a component to an HTML string.</summary>
    /// <param name="componentType">A type that implements <see cref="IComponent"/>.</param>
    /// <param name="parameters">Values for the component's <c>[Parameter]</c> properties, keyed by property name.</param>
    /// <param name="cancellationToken">Cancels the operation before or after rendering (a render in progress runs to completion).</param>
    public async Task<string> RenderHtmlAsync(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type componentType,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        if (!typeof(IComponent).IsAssignableFrom(componentType))
        {
            throw new ArgumentException($"{componentType.FullName} does not implement {nameof(IComponent)}.", nameof(componentType));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var parameterView = parameters is null or { Count: 0 }
            ? ParameterView.Empty
            : ParameterView.FromDictionary(new Dictionary<string, object?>(parameters, StringComparer.Ordinal));

        await using var renderer = new HtmlRenderer(services, loggerFactory);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            // No ConfigureAwait(false): the HTML must be read on the renderer's dispatcher, which this lambda runs on.
            var root = await renderer.RenderComponentAsync(componentType, parameterView);
            return root.ToHtmlString();
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return html;
    }

    /// <summary>Renders a component and converts the HTML to a PDF document.</summary>
    /// <typeparam name="TComponent">The component, typically a <c>.razor</c> file that renders a whole HTML document.</typeparam>
    /// <param name="parameters">Values for the component's <c>[Parameter]</c> properties, keyed by property name.</param>
    /// <param name="options">HTML conversion options: fonts, default page setup, base URI for images and stylesheets.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task<PdfDocument> ConvertAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(
        IReadOnlyDictionary<string, object?>? parameters = null,
        HtmlConverterOptions? options = null,
        CancellationToken cancellationToken = default)
        where TComponent : IComponent =>
        ConvertAsync(typeof(TComponent), parameters, options, cancellationToken);

    /// <summary>Renders a component and converts the HTML to a PDF document.</summary>
    /// <param name="componentType">A type that implements <see cref="IComponent"/>.</param>
    /// <param name="parameters">Values for the component's <c>[Parameter]</c> properties, keyed by property name.</param>
    /// <param name="options">HTML conversion options: fonts, default page setup, base URI for images and stylesheets.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<PdfDocument> ConvertAsync(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type componentType,
        IReadOnlyDictionary<string, object?>? parameters = null,
        HtmlConverterOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var html = await RenderHtmlAsync(componentType, parameters, cancellationToken).ConfigureAwait(false);
        var document = await HtmlToPdf.ConvertAsync(html, options, cancellationToken).ConfigureAwait(false);
        document.Info.Creator = "RaiCore.Pdf.Razor";
        return document;
    }

    /// <summary>
    /// Renders a component and lays out the HTML without rendering the PDF, so you can add a
    /// <see cref="FlowDocument.PageDecorator"/> or render it into an existing <see cref="PdfDocument"/>.
    /// </summary>
    /// <typeparam name="TComponent">The component, typically a <c>.razor</c> file that renders a whole HTML document.</typeparam>
    /// <param name="parameters">Values for the component's <c>[Parameter]</c> properties, keyed by property name.</param>
    /// <param name="options">HTML conversion options: fonts, default page setup, base URI for images and stylesheets.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<FlowDocument> CreateFlowDocumentAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(
        IReadOnlyDictionary<string, object?>? parameters = null,
        HtmlConverterOptions? options = null,
        CancellationToken cancellationToken = default)
        where TComponent : IComponent
    {
        var html = await RenderHtmlAsync<TComponent>(parameters, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return HtmlToPdf.CreateFlowDocument(html, options);
    }

    /// <summary>A service provider with no services, for components that inject nothing.</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}

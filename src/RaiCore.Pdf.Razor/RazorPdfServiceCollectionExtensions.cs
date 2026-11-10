using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace RaiCore.Pdf.Razor;

/// <summary>Registers <see cref="RazorPdfRenderer"/> with dependency injection.</summary>
public static class RazorPdfServiceCollectionExtensions
{
    /// <summary>
    /// Registers a scoped <see cref="RazorPdfRenderer"/> whose components resolve services from the current scope
    /// (in ASP.NET Core, the request).
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddRazorPdf();
    ///
    /// app.MapGet("/invoices/{id}.pdf", async (int id, RazorPdfRenderer pdf) =&gt;
    ///     Results.File((await pdf.ConvertAsync&lt;InvoicePage&gt;(new Dictionary&lt;string, object?&gt; { ["Id"] = id })).ToArray(),
    ///                  "application/pdf"));
    /// </code>
    /// </example>
    public static IServiceCollection AddRazorPdf(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped(provider => new RazorPdfRenderer(provider, provider.GetService<ILoggerFactory>()));
        return services;
    }
}

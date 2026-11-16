#pragma warning disable CA1849, CA2007
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RaiCore.Pdf.Razor;

namespace Snippets;

internal static class Razor
{
    public static async Task RunAsync()
    {
        var invoice = Data.Invoice;

        #region razor-to-pdf
        var pdf = await RazorToPdf.ConvertAsync<InvoiceDocument>(new Dictionary<string, object?>
        {
            [nameof(InvoiceDocument.Invoice)] = invoice,
        });
        pdf.Save("razor-invoice.pdf");
        #endregion

        #region services
        var services = new ServiceCollection().AddRazorPdf().BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            var renderer = scope.ServiceProvider.GetRequiredService<RazorPdfRenderer>();
            var html = await renderer.RenderHtmlAsync<InvoiceDocument>(new Dictionary<string, object?> { ["Invoice"] = invoice });
            Console.WriteLine($"{html.Length} characters of HTML");     // the HTML that is converted, for debugging
        }
        #endregion
    }

    /// <summary>Compiled, not run: it needs a web host.</summary>
    public static void MapEndpoints(WebApplicationBuilder builder)
    {
        #region aspnetcore
        builder.Services.AddRazorPdf();               // a RazorPdfRenderer per request scope
        var app = builder.Build();

        // Render the PDF, then send its bytes.
        app.MapGet("/invoices/{number}.pdf", async (
            string number, RazorPdfRenderer renderer, CancellationToken cancel) =>
        {
            var invoice = Data.Invoice with { Number = number };
            var pdf = await renderer.ConvertAsync<InvoiceDocument>(
                new Dictionary<string, object?> { ["Invoice"] = invoice }, cancellationToken: cancel);
            return Results.File(pdf.ToArray(), "application/pdf", $"invoice-{number}.pdf");
        });

        // Or stream it into the response page by page
        // (asynchronous writes only, as ASP.NET Core requires).
        app.MapGet("/statement.pdf", async (
            HttpResponse response, RazorPdfRenderer renderer, CancellationToken cancel) =>
        {
            response.ContentType = "application/pdf";
            var parameters = new Dictionary<string, object?> { ["Invoice"] = Data.Invoice };
            await renderer.ConvertToStreamAsync<InvoiceDocument>(
                response.Body, parameters, cancellationToken: cancel);
        });
        #endregion

        app.Run();
    }
}

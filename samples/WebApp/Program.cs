// PDFs from Razor components in an ASP.NET Core app: components inject services like any Blazor component, and the PDF
// is returned as a file or streamed straight into the response.
// Usage: dotnet run --project samples/WebApp, then open http://localhost:5000/invoices/42.pdf or /statement.pdf
using RaiCore.Pdf.Razor;
using WebApp;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPdf(); // A RazorPdfRenderer per request scope, so components get the request's services.
builder.Services.AddSingleton<InvoiceStore>();
var app = builder.Build();

app.MapGet("/", () => Results.Content(
    """<p><a href="/invoices/42.pdf">Invoice 42</a> &middot; <a href="/statement.pdf">Statement</a></p>""", "text/html"));

// A short document: render it, then send the bytes.
app.MapGet("/invoices/{id:int}.pdf", async (int id, InvoiceStore store, RazorPdfRenderer renderer, CancellationToken cancel) =>
{
    if (store.Find(id) is not { } invoice)
    {
        return Results.NotFound();
    }

    var parameters = new Dictionary<string, object?> { [nameof(InvoicePage.Invoice)] = invoice };
    var pdf = await renderer.ConvertAsync<InvoicePage>(parameters, cancellationToken: cancel);
    return Results.File(pdf.ToArray(), "application/pdf", $"invoice-{id}.pdf");
});

// A long document: stream it into the response page by page (asynchronous writes only, as ASP.NET Core requires).
app.MapGet("/statement.pdf", async (HttpResponse response, RazorPdfRenderer renderer, CancellationToken cancel) =>
{
    response.ContentType = "application/pdf";
    response.Headers.ContentDisposition = "inline; filename=statement.pdf";
    await renderer.ConvertToStreamAsync<StatementPage>(response.Body, cancellationToken: cancel);
});

app.Run();

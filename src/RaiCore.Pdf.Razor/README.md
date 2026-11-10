# RaiCore.Pdf.Razor

Render Razor components (`.razor` files) to PDF. Components are rendered to HTML with Blazor's static
`HtmlRenderer` and converted with [RaiCore.Pdf.Html](https://www.nuget.org/packages/RaiCore.Pdf.Html). No web
server, browser or HTTP request is needed, so it works in console apps, background jobs and ASP.NET Core alike.

```sh
dotnet add package RaiCore.Pdf.Razor
```

```razor
@* InvoiceDocument.razor *@
<html>
<head>
    <title>Invoice @Invoice.Number</title>
    <style>
        @@page { size: A4; margin: 20mm; }
        body { font-family: Arial, sans-serif; }
    </style>
</head>
<body>
    <h1>Invoice @Invoice.Number</h1>
    <ul>
        @foreach (var line in Invoice.Lines) { <li>@line.Description</li> }
    </ul>
</body>
</html>

@code {
    [Parameter] public Invoice Invoice { get; set; } = default!;
}
```

```csharp
using RaiCore.Pdf.Razor;

var pdf = await RazorToPdf.ConvertAsync<InvoiceDocument>(new Dictionary<string, object?>
{
    [nameof(InvoiceDocument.Invoice)] = invoice,
});
pdf.Save("invoice.pdf");
```

Components that `@inject` services: register `builder.Services.AddRazorPdf()` and inject `RazorPdfRenderer`. It is
scoped, so components share the request's scoped services. Outside a host, use `new RazorPdfRenderer(services)`.

- Rendering waits for `OnInitializedAsync`, so components can load their own data.
- In `.razor` files, write CSS at-rules with a double `@`: `@@page`, `@@media print`.
- Static rendering only: no event handlers, JavaScript interop or interactive render modes. `.razor.css` isolation
  files and `<PageTitle>` are not applied (use `<style>` and `<title>`).

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)

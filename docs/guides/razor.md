# Razor and ASP.NET Core

`RaiCore.Pdf.Razor` renders Razor components (`.razor` files) to HTML with Blazor's static `HtmlRenderer` and converts
the HTML. It needs no web server, browser or HTTP request, so it works in console apps, background jobs and Azure
Functions as well as in ASP.NET Core.

## A component as a document

The component renders a whole HTML document, styles included. In `.razor` files, CSS at-rules are written with a
double `@`: `@@page`, `@@media print`.

[!code-xml[](../snippets/InvoiceDocument.razor)]

[!code-csharp[](../snippets/Razor.cs#razor-to-pdf)]

## Services and ASP.NET Core

Components that `@inject` services need the renderer from dependency injection: `AddRazorPdf()` registers a
`RazorPdfRenderer` per scope, so components share the request's scoped services (a `DbContext`, for example). Long
documents can be streamed into the response page by page; the stream is written asynchronously only, as ASP.NET Core
requires.

[!code-csharp[](../snippets/Razor.cs#aspnetcore)]

Outside a host, build a service provider yourself (or pass any `IServiceProvider` to `new RazorPdfRenderer(services)`).
`RenderHtmlAsync` returns the HTML that is converted, which helps when the output is not what you expect:

[!code-csharp[](../snippets/Razor.cs#services)]

## What works

- Parameters, child components, layout components with `ChildContent`, `@foreach`/`@if` and HTML encoding work as in
  Blazor. Rendering waits for `OnInitializedAsync`, so components can load their own data.
- `HtmlConverterOptions` apply as for HTML (`RazorToPdf.ConvertAsync<T>(parameters, options)`); the document's creator
  is `RaiCore.Pdf.Razor`.
- Static rendering only: event handlers and JavaScript interop do nothing, and `<PageTitle>`/`<HeadContent>` have no
  effect (use a `<title>` element). CSS isolation files (`.razor.css`) are not applied. MVC views and Razor Pages
  (`.cshtml`) are not supported.

The [WebApp sample](https://github.com/raicevic/raicore-pdf/tree/main/samples/WebApp) is a complete app.

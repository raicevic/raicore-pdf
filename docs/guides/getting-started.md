# Getting started

RaiCore.Pdf creates PDF documents in .NET 10: from HTML and CSS, from Razor components, or by drawing and laying out
content in code. It has its own layout engine, so there is no browser to install and the output is the same on every
machine.

## Install

| Package | For |
|---|---|
| `RaiCore.Pdf.Html` | HTML and CSS to PDF (brings `RaiCore.Pdf`, `RaiCore.Pdf.Layout` and `RaiCore.Pdf.Fonts`) |
| `RaiCore.Pdf.Razor` | Razor components to PDF (brings `RaiCore.Pdf.Html`) |
| `RaiCore.Pdf.Layout` | Paragraphs, tables and multi-page documents from code |
| `RaiCore.Pdf` | Drawing PDF pages directly, reading and merging PDFs |
| `RaiCore.Pdf.Fonts` | The bundled Liberation fonts |

```sh
dotnet add package RaiCore.Pdf.Html
```

## HTML to PDF

[!code-csharp[](../snippets/GettingStarted.cs#html-to-pdf)]

The page size and margins come from the CSS `@page` rule (A4 with 2 cm margins without one). Headers, footers and
page numbers come from CSS too: see [Paged media](paged-media.md).

## Options

`HtmlConverterOptions` holds everything a conversion needs besides the HTML: where relative URLs point, the document
metadata, fonts, limits for untrusted input and more. Options are immutable and can be shared by conversions running
at the same time.

[!code-csharp[](../snippets/GettingStarted.cs#options)]

## Writing to a file or a response

`ConvertToStreamAsync` writes each page as soon as it is laid out, so long documents need little memory. It writes the
stream asynchronously only, so the stream can be an ASP.NET Core response body
(see [Razor and ASP.NET Core](razor.md)).

[!code-csharp[](../snippets/GettingStarted.cs#stream)]

## Drawing directly

Coordinates are PDF points (1/72 inch) with the origin at the bottom left of the page.

[!code-csharp[](../snippets/GettingStarted.cs#draw)]

## Next steps

- [CSS support](css-support.md): what the converter supports, and how to find out why output differs from a browser's.
- [Paged media](paged-media.md): page sizes, headers, footers, page numbers and tables of contents.
- [Samples](https://github.com/raicevic/raicore-pdf/tree/main/samples): runnable apps for typical jobs.
- [Migration guides](../migration/README.md) for wkhtmltopdf, Puppeteer/Playwright, IronPDF, Aspose and SelectPdf.

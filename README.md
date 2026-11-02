# RaiCore.Pdf

An open-source .NET library for creating PDF documents and converting HTML, Razor and office documents to PDF, built on its own layout engine with no headless browser.

> **Status:** early development (0.1.0-alpha). The PDF writer works; HTML/Razor conversion is in progress. APIs will change.

## Packages

| Package | Purpose | Status |
|---|---|---|
| `RaiCore.Pdf` | PDF object model, writer, fonts and drawing API | 🟡 In progress |
| `RaiCore.Pdf.Layout` | Format-neutral layout engine: text shaping, line breaking, pagination, tables | ⚪ Planned |
| `RaiCore.Pdf.Html` | HTML + CSS (including CSS Paged Media) → PDF | ⚪ Planned |
| `RaiCore.Pdf.Razor` | Razor components → PDF | ⚪ Planned |
| `RaiCore.Pdf.Docx`, `.Xlsx`, `.Pptx`, `.XslFo` | Office and XML formats → PDF | ⚪ Later |

## Quick start

```csharp
using RaiCore.Pdf;
using RaiCore.Pdf.Fonts;

var doc = new PdfDocument();
doc.Info.Title = "Hello";

var page = doc.AddPage(PdfPageSize.A4);
page.Canvas
    .SetFillColor(PdfColor.FromRgb(220, 30, 30))
    .Rectangle(72, 600, 200, 100)
    .Fill()
    .SetFillColor(PdfColor.Black)
    .DrawText("Hello from RaiCore.Pdf", StandardFont.Helvetica, 24, 72, page.Height - 80);

doc.Save("hello.pdf");
```

Coordinates are in PDF points (1/72 inch) with the origin at the bottom-left of the page.

## Architecture

```
 DOCX / XLSX / PPTX ─┐
 HTML + CSS / Razor ─┼─► Document model ─► Layout engine ─► PDF writer
 XML (XSL-FO)       ─┘
```

Every input format is mapped to one shared document model and laid out by one engine, so improvements to fonts, tables and pagination benefit all formats.

## Roadmap

1. **PDF writer:** objects, compression, standard fonts ✅, then embedded TrueType/OpenType with subsetting, images, links and bookmarks.
2. **HTML/CSS core:** parsing (AngleSharp), the cascade, block/inline layout, HarfBuzz text shaping, line breaking and pagination.
3. **0.1 release:** tables, `@page`, page breaks, page numbers and the Razor adapter.
4. **0.2:** flexbox, inline SVG, margin boxes, running headers, tagged PDF and PDF/A.
5. **Later:** DOCX, PDF editing, XSL-FO, XLSX and PPTX.

JavaScript is not executed, by design: the target is print documents such as invoices and reports.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet build
dotnet test
```

Install [qpdf](https://github.com/qpdf/qpdf) to run the PDF structure validation tests locally; otherwise they are skipped.

## License

[Apache License 2.0](LICENSE). All shipped dependencies must use permissive licenses (MIT, BSD or Apache-2.0).

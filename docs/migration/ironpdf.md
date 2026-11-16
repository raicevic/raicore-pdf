# From IronPDF

IronPDF renders HTML with an embedded Chromium and is licensed commercially per developer and deployment.
RaiCore.Pdf is open source (Apache-2.0) and renders with its own layout engine, so there is no browser engine to
deploy and no license key to set.

## Side by side

IronPDF:

```csharp
License.LicenseKey = "...";
var renderer = new ChromePdfRenderer();
renderer.RenderingOptions.PaperSize = PdfPaperSize.A4;
renderer.RenderingOptions.PaperOrientation = PdfPaperOrientation.Portrait;
renderer.RenderingOptions.MarginTop = 25;      // millimeters
renderer.RenderingOptions.MarginBottom = 20;
renderer.RenderingOptions.CssMediaType = PdfCssMediaType.Print;
renderer.RenderingOptions.PrintHtmlBackgrounds = true;
renderer.RenderingOptions.TextFooter = new TextHeaderFooter
{
    CenterText = "Page {page} of {total-pages}",
    FontSize = 8,
};
using var pdf = renderer.RenderHtmlAsPdf(html, @"C:\app\templates\");
pdf.SaveAs("invoice.pdf");
```

RaiCore.Pdf:

```csharp
using RaiCore.Pdf.Html;

var options = new HtmlConverterOptions
{
    BaseUri = new Uri(@"C:\app\templates\"),
    UserStyleSheet = """
        @page {
          size: A4;
          margin: 25mm 15mm 20mm;
          @bottom-center { content: "Page " counter(page) " of " counter(pages); font-size: 8pt; }
        }
        """,
};
HtmlToPdf.Convert(html, options).Save("invoice.pdf");
```

## Options

| IronPDF `RenderingOptions` | RaiCore.Pdf |
|---|---|
| `PaperSize = PdfPaperSize.A4` | `@page { size: A4 }` |
| `SetCustomPaperSizeInMillimeters(210, 297)` | `@page { size: 210mm 297mm }` |
| `PaperOrientation = Landscape` | `@page { size: A4 landscape }` |
| `MarginTop`, `MarginBottom`, ... (mm) | `@page { margin: ... }` |
| `CssMediaType = Print` | always `print` |
| `PrintHtmlBackgrounds` | always: backgrounds are drawn |
| `TextHeader`, `TextFooter` | margin boxes: `@top-center { content: "..." }` |
| `HtmlHeader`, `HtmlFooter` (`HtmlHeaderFooter`) | running elements: `position: running(name)` and `content: element(name)` |
| `FirstPageNumber` | `@page :first { counter-set: page 5 }` (`counter(pages)` still counts the pages) |
| `CreatePdfFormsFromHtml` | not supported: form fields are not made fillable |
| `EnableJavaScript`, `WaitFor`, `RenderDelay` | not needed: no JavaScript runs |
| `ViewPortWidth`, `FitToPaperMode`, `Zoom` | no equivalent: layout uses the page's content width |
| `Timeout` | `HtmlConverterOptions.Limits` (`ResourceLimits.Timeout`) |
| `Title` (and `PdfDocument.MetaData`) | `HtmlConverterOptions.Title`, `Author`, `Subject`, `Keywords` |
| `PdfDocument.SecuritySettings` | `HtmlConverterOptions.Encryption` |
| `PdfDocument.SaveAsPdfA` | `HtmlConverterOptions.PdfAConformance` |
| `RenderUrlAsPdf(url)` | load the page yourself; set `BaseUri` and a `ResourceLoader` for its resources |
| `PdfDocument.Merge`, `AppendPdf` | `PdfDocument.ImportPages` |

## Headers, footers and page numbers

| IronPDF placeholder | RaiCore.Pdf |
|---|---|
| `{page}` | `counter(page)` |
| `{total-pages}` | `counter(pages)` |
| `{html-title}`, `{pdf-title}` | the title as a string, or `string(heading)` with `string-set` on a heading |
| `{date}`, `{time}`, `{url}` | write them into the stylesheet when you build it |

`TextHeaderFooter.DrawDividerLine` becomes a running element with a `border-bottom`. An `HtmlHeaderFooter` fragment
becomes a running element in the document:

```html
<style>
  @page { margin-top: 30mm; @top-left { content: element(letterhead); } }
  .letterhead { position: running(letterhead); border-bottom: 0.5pt solid #999; }
</style>
<div class="letterhead"><img src="logo.svg" alt="Example" height="40"> Example Consulting</div>
```

## Fonts

IronPDF's Chromium uses installed fonts and web fonts. RaiCore.Pdf uses `@font-face` and its bundled Liberation fonts
(for Arial, Helvetica, Times New Roman, Courier New and the generic families) by default, so the output is the same
in every environment; add installed fonts with `Fonts = new FontCollection().AddSystemFonts().AddBundledFonts()`.

## Differences to expect

- **No JavaScript**, and layout at the page's width rather than a window's: see the
  [known differences](README.md#known-differences).
- **Editing existing PDFs** (stamping, filling forms, redaction) is not part of 1.0; RaiCore.Pdf can import pages
  from existing PDFs and draw on them.
- **Razor:** IronPDF's MVC/Razor extensions become RaiCore.Pdf.Razor, which renders Razor components without a web
  host (`RazorToPdf.ConvertAsync<TComponent>`), or with your services (`AddRazorPdf()` and `RazorPdfRenderer`).

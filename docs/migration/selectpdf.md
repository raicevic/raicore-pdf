# From SelectPdf

SelectPdf renders HTML with a browser engine and is licensed commercially (the free Community Edition has a page
limit). RaiCore.Pdf is open source (Apache-2.0) with no page limit.

Both libraries have a class named `HtmlToPdf`; while code still uses both, qualify them
(`RaiCore.Pdf.Html.HtmlToPdf`) or use an alias.

## Side by side

SelectPdf:

```csharp
var converter = new SelectPdf.HtmlToPdf();
converter.Options.PdfPageSize = PdfPageSize.A4;
converter.Options.PdfPageOrientation = PdfPageOrientation.Portrait;
converter.Options.MarginTop = 20;
converter.Options.MarginBottom = 20;
converter.Options.DisplayFooter = true;
converter.Footer.Height = 30;
converter.Footer.Add(new PdfTextSection(0, 10, "Page {page_number} of {total_pages}", new Font("Arial", 8))
{
    HorizontalAlign = PdfTextHorizontalAlign.Center,
});
PdfDocument document = converter.ConvertHtmlString(html, "file:///C:/app/templates/");
document.Save("invoice.pdf");
document.Close();
```

RaiCore.Pdf:

```csharp
using RaiCore.Pdf.Html;

var options = new HtmlConverterOptions
{
    BaseUri = new Uri("file:///C:/app/templates/"),
    UserStyleSheet = """
        @page {
          size: A4;
          margin: 20mm 15mm;
          @bottom-center { content: "Page " counter(page) " of " counter(pages); font-family: Arial; font-size: 8pt; }
        }
        """,
};
RaiCore.Pdf.Html.HtmlToPdf.Convert(html, options).Save("invoice.pdf");
```

## Options

| SelectPdf `Options` | RaiCore.Pdf |
|---|---|
| `PdfPageSize`, `PdfPageCustomSize` | `@page { size: A4 }`, `size: 210mm 297mm` |
| `PdfPageOrientation` | `@page { size: A4 landscape }` |
| `MarginTop`, `MarginBottom`, ... (points) | `@page { margin: ... }` |
| `DisplayHeader`, `DisplayFooter`, `Header.Add(...)` | margin boxes, or running elements for HTML (below) |
| `Header.DisplayOnFirstPage = false` | `@page :first { @top-center { content: none } }` |
| `WebPageWidth`, `WebPageHeight`, `AutoFitWidth` | no equivalent: layout uses the page's content width |
| `CssMediaType = HtmlToPdfCssMediaType.Print` | always `print` |
| `DrawBackground` | always: backgrounds are drawn |
| `JavaScriptEnabled`, `MinPageLoadTime`, `MaxPageLoadTime` | not needed: no JavaScript runs; `Limits.Timeout` bounds the conversion |
| `InternalLinksEnabled`, `ExternalLinksEnabled` | links work by default; remove `href` to drop one |
| `PdfDocumentInformation` | `HtmlConverterOptions.Title`, `Author`, `Subject`, `Keywords` |
| `SecurityOptions` | `HtmlConverterOptions.Encryption` |
| `PdfStandard = PdfStandard.PdfA` | `HtmlConverterOptions.PdfAConformance` |
| `ConvertUrl(url)` | load the page yourself; set `BaseUri` and a `ResourceLoader` for its resources |

## Headers, footers and page numbers

| SelectPdf placeholder | RaiCore.Pdf |
|---|---|
| `{page_number}` | `counter(page)` |
| `{total_pages}` | `counter(pages)` |

A header from HTML (`PdfHtmlSection`) becomes a running element in the document:

```html
<style>
  @page { margin-top: 30mm; @top-left { content: element(letterhead); } }
  .letterhead { position: running(letterhead); }
</style>
<div class="letterhead"><img src="logo.svg" alt="Example" height="40"> Example Consulting</div>
```

## Fonts

SelectPdf uses installed fonts. RaiCore.Pdf uses `@font-face` and its bundled Liberation fonts (for Arial, Helvetica,
Times New Roman, Courier New and the generic families) by default; add installed fonts with
`Fonts = new FontCollection().AddSystemFonts().AddBundledFonts()`.

## Differences to expect

See the [known differences](README.md#known-differences): no JavaScript, layout at the page's width rather than a
1024-pixel window, the same fonts everywhere and no network access unless you allow it.

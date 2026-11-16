# Migrating to RaiCore.Pdf

Guides for moving HTML-to-PDF code from other tools, with side-by-side examples:

- [wkhtmltopdf and DinkToPdf](wkhtmltopdf.md)
- [Puppeteer and Playwright (`page.pdf()`), also PuppeteerSharp and Playwright for .NET](chromium.md)
- [IronPDF](ironpdf.md)
- [Aspose.HTML and Aspose.PDF](aspose.md)
- [SelectPdf](selectpdf.md)

The examples of the other tools follow their documentation; option names can differ between their versions.

## The idea in one paragraph

Most tools take page size, margins, headers and footers as options of the converter. RaiCore.Pdf takes them from the
CSS, as browsers do when they print: `@page` sets size and margins, its margin boxes hold headers and footers, and
`counter(page)` and `counter(pages)` number the pages. So migrating mostly means moving options into a stylesheet. If
you cannot change the HTML, put that stylesheet in `HtmlConverterOptions.UserStyleSheet`; it applies after the
document's own styles.

```csharp
using RaiCore.Pdf.Html;

var options = new HtmlConverterOptions
{
    BaseUri = new Uri("file:///var/app/templates/"),   // where relative image and stylesheet URLs point
    UserStyleSheet = """
        @page {
          size: A4;
          margin: 20mm 15mm;
          @bottom-right { content: "Page " counter(page) " of " counter(pages); font-size: 8pt; }
        }
        """,
};
byte[] pdf = HtmlToPdf.ConvertToBytes(html, options);
```

## Common options

| What | RaiCore.Pdf |
|---|---|
| Page size and orientation | `@page { size: A4 }`, `size: A4 landscape`, `size: letter`, `size: 210mm 297mm` |
| Margins | `@page { margin: 20mm 15mm }`; per side and per page: `@page :first { margin-top: 50mm }` |
| Different pages for part of the document | named pages: `.wide { page: wide }` with `@page wide { size: A4 landscape }` |
| Print media type | always: the CSS is applied with `print` media |
| Print backgrounds | always: backgrounds and colors are drawn as specified |
| Headers and footers (text) | margin boxes: `@top-center { content: "Report" }` |
| Headers and footers (HTML) | running elements: `.header { position: running(header) }` and `@top-left { content: element(header) }` |
| Page numbers | `counter(page)` and `counter(pages)` in margin boxes, in any counter style |
| Chapter title in the header | `h1 { string-set: chapter content() }` and `@top-right { content: string(chapter) }` |
| Table of contents | links with `target-counter(attr(href), page)` and `leader(".")` (see [samples/Report](../../samples/Report/Program.cs)) |
| Bookmarks (outline) | from `h1`-`h6` by default (`CreateBookmarks`); `bookmark-level`, `bookmark-label`, `bookmark-state` |
| Base URL for relative links, images, stylesheets | `HtmlConverterOptions.BaseUri` |
| Loading resources over the network | off by default; pass a `ResourceLoader` (see the [README](../../README.md)) |
| Fonts | bundled Liberation fonts by default; `@font-face`; installed fonts with `Fonts = new FontCollection().AddSystemFonts().AddBundledFonts()` |
| Title, author, subject, keywords | from `<title>` and `<meta>`, or `HtmlConverterOptions.Title`, `Author`, ... |
| Password and permissions | `HtmlConverterOptions.Encryption` |
| PDF/A, tagged PDF, PDF/UA | `PdfAConformance`, `Tagged`, `PdfUA` |
| Timeouts and size limits | `HtmlConverterOptions.Limits` (`ResourceLimits`: time, pages, elements, image and resource sizes) |
| Why something looks different | `HtmlConverterOptions.Diagnostics`: resources that did not load and CSS that is not supported |
| Writing to a stream (ASP.NET Core) | `HtmlToPdf.ConvertToStreamAsync(html, Response.Body, options)` |
| Razor views or components | RaiCore.Pdf.Razor: `RazorToPdf.ConvertAsync<TComponent>(...)` or `RazorPdfRenderer` |

## Known differences

- **No JavaScript.** Scripts do not run, so content that a script builds (charts, client-side frameworks, data loaded
  by `fetch`) is missing. Render it on the server instead, for example with Razor components, or draw charts as SVG.
  There is also nothing to wait for: no render delays, `networkidle` or `window.status`.
- **Print layout, not a browser window.** Layout uses the page's content area as the viewport: media queries,
  `vw` and percentages see the width of the page, not a 1024 or 1280 pixel window, and nothing is shrunk to fit. A
  fixed-width layout wider than the page (say, `width: 1000px`) overflows it; use relative widths, a smaller layout
  in `@media print`, or a landscape page. Lengths are CSS units: `1px` is 1/96 inch and `1pt` is 1/72 inch.
- **The same fonts everywhere.** By default only the bundled Liberation fonts are used (metric-compatible with Arial,
  Helvetica, Times New Roman and Courier New), so the output is the same on every machine and in containers. Tools
  that use installed fonts may pick other fonts, so line breaks can differ. Use `@font-face` for your own fonts, or
  add the installed ones (`AddSystemFonts()`).
- **No network by default.** `http:` and `https:` URLs load only through a `ResourceLoader` you pass; the default one
  reads `data:` and `file:` URIs.
- **Not supported yet:** CSS grid (laid out as blocks), multi-column layout, `box-shadow`, and HTML form fields as
  fillable PDF fields. `HtmlConverterOptions.Diagnostics` reports what the converter skips.
- **Text stays text**, with the fonts embedded and subset, so the PDF is searchable and its text can be copied.

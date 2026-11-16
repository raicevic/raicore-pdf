# From Puppeteer and Playwright (`page.pdf()`)

Puppeteer, Playwright and their .NET ports (PuppeteerSharp, Microsoft.Playwright) print a page with headless Chromium.
RaiCore.Pdf needs no browser: no Chromium download, no process to start and manage, no sandbox flags in containers,
and the conversion runs in your process with a fixed memory and time budget (`ResourceLimits`).

## Side by side

Playwright for .NET:

```csharp
await using var browser = await playwright.Chromium.LaunchAsync();
var page = await browser.NewPageAsync();
await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.NetworkIdle });
byte[] pdf = await page.PdfAsync(new PagePdfOptions
{
    Format = "A4",
    Margin = new Margin { Top = "25mm", Bottom = "20mm", Left = "15mm", Right = "15mm" },
    PrintBackground = true,
    DisplayHeaderFooter = true,
    HeaderTemplate = "<div style='font-size: 8px; width: 100%; text-align: right; margin-right: 15mm'><span class='title'></span></div>",
    FooterTemplate = "<div style='font-size: 8px; width: 100%; text-align: center'>Page <span class='pageNumber'></span> of <span class='totalPages'></span></div>",
});
```

Puppeteer (JavaScript) has the same options: `page.pdf({ format: 'A4', margin: {...}, printBackground: true,
displayHeaderFooter: true, headerTemplate, footerTemplate })`.

RaiCore.Pdf:

```csharp
using RaiCore.Pdf.Html;

var options = new HtmlConverterOptions
{
    UserStyleSheet = """
        @page {
          size: A4;
          margin: 25mm 15mm 20mm;
          @top-right { content: string(heading); font-size: 8pt; }
          @bottom-center { content: "Page " counter(page) " of " counter(pages); font-size: 8pt; }
        }
        h1 { string-set: heading content(); }
        """,
};
byte[] pdf = await HtmlToPdf.ConvertToBytesAsync(html, options, cancellationToken);
```

## Options

| `page.pdf()` | RaiCore.Pdf |
|---|---|
| `format: 'A4'`, `width`, `height` | `@page { size: A4 }`, `size: 210mm 297mm` |
| `landscape: true` | `@page { size: A4 landscape }` |
| `margin` | `@page { margin: ... }` |
| `preferCSSPageSize: true` | always: `@page` decides |
| `printBackground: true` | always: backgrounds are drawn |
| `displayHeaderFooter`, `headerTemplate`, `footerTemplate` | `@page` margin boxes, or running elements for HTML headers (below) |
| `pageRanges: '1-3'` | not needed for output from your own HTML; to pick pages from a PDF, use `PdfDocument.ImportPages` |
| `scale` | no equivalent: change the CSS sizes |
| `outline: true` (Chrome's tagged outline) | on by default (`CreateBookmarks`) |
| `tagged: true` | `HtmlConverterOptions.Tagged` (and `PdfUA` for PDF/UA-1) |
| `page.emulateMedia({ media: 'print' })` | always `print` |
| `waitUntil: 'networkidle'`, `waitForSelector` | not needed: no JavaScript, resources load during conversion |
| `page.goto(url)` | load the HTML yourself, set `BaseUri` to its address and pass a `ResourceLoader` for its resources |
| `setViewport` | no viewport: layout uses the page's content area (see below) |

## Headers, footers and page numbers

Chromium's header and footer templates are small separate documents with classes it fills in. In RaiCore.Pdf they
are part of the document's CSS:

| Template class | RaiCore.Pdf |
|---|---|
| `pageNumber` | `counter(page)` |
| `totalPages` | `counter(pages)` |
| `title` | the title as a string, or `string(heading)` with `string-set` on a heading |
| `date` | write the date into the stylesheet when you build it |
| `url` | write it into the stylesheet |

Chromium's templates do not see the page's stylesheets and need explicit font sizes and margins; margin boxes are
styled with the page's CSS and placed in the page margin. For an HTML header (a logo, several lines), use a running
element:

```html
<style>
  @page { margin-top: 30mm; @top-left { content: element(letterhead); } }
  .letterhead { position: running(letterhead); }
</style>
<div class="letterhead"><img src="logo.svg" alt="Example" height="40"> Example Consulting</div>
```

Headers can differ per page: `@page :first`, `:left`, `:right`, `:blank` and named pages, which Chromium cannot do.

## Fonts

Chromium uses the fonts installed in the container or on the machine, which is why images often install extra font
packages. RaiCore.Pdf bundles the Liberation fonts and uses them for Arial, Helvetica, Times New Roman, Courier New and
the generic families, so nothing needs installing; other fonts come from `@font-face` (also `data:` URIs) or from
`Fonts = new FontCollection().AddSystemFonts().AddBundledFonts()`.

## Differences to expect

- **No JavaScript.** Client-side rendering (React, Vue, chart libraries) does not run. Render the HTML on the server,
  for example with RaiCore.Pdf.Razor, and draw charts as SVG.
- **Viewport.** Chromium lays out the page at the window width and then fits it to the paper; media queries see that
  window width. RaiCore.Pdf lays out at the page's content width (about 643 CSS pixels for A4 with 2 cm margins), so
  breakpoints for wide screens do not match and a fixed-width layout wider than the page overflows. Write the print
  layout in `@media print` or with relative widths.
- **Same output everywhere**: no dependence on the Chromium version or installed fonts. Set
  `HtmlConverterOptions.TimeProvider` to a fixed clock and the bytes are identical on every run.
- See also the [known differences](README.md#known-differences).

# From wkhtmltopdf and DinkToPdf

wkhtmltopdf (and DinkToPdf, its .NET wrapper) renders with an old Qt WebKit and is no longer maintained. RaiCore.Pdf
is a managed library: no native executable to ship, and modern CSS such as flexbox, CSS variables and `calc()` works.

## Side by side

DinkToPdf:

```csharp
var converter = new SynchronizedConverter(new PdfTools());
var document = new HtmlToPdfDocument
{
    GlobalSettings =
    {
        PaperSize = PaperKind.A4,
        Orientation = Orientation.Portrait,
        Margins = new MarginSettings { Top = 20, Bottom = 20, Left = 15, Right = 15 },
        DocumentTitle = "Invoice 2026-0042",
    },
    Objects =
    {
        new ObjectSettings
        {
            HtmlContent = html,
            WebSettings = { DefaultEncoding = "utf-8", UserStyleSheet = "print.css" },
            HeaderSettings = { FontSize = 8, Right = "[title]", Line = true },
            FooterSettings = { FontSize = 8, Center = "Page [page] of [toPage]" },
        },
    },
};
byte[] pdf = converter.Convert(document);
```

The same with the wkhtmltopdf command line:

```sh
wkhtmltopdf --page-size A4 --margin-top 20mm --margin-bottom 20mm --margin-left 15mm --margin-right 15mm \
  --header-right "[title]" --header-line --header-font-size 8 \
  --footer-center "Page [page] of [toPage]" --footer-font-size 8 invoice.html invoice.pdf
```

RaiCore.Pdf:

```csharp
using RaiCore.Pdf.Html;

var title = "Invoice 2026-0042";
var options = new HtmlConverterOptions
{
    Title = title,
    UserStyleSheet = $$"""
        @page {
          size: A4;
          margin: 20mm 15mm;
          @top-right { content: "{{title}}"; font-size: 8pt; }
          @bottom-center { content: "Page " counter(page) " of " counter(pages); font-size: 8pt; }
        }
        """,
};
byte[] pdf = HtmlToPdf.ConvertToBytes(html, options);
```

## Options

| wkhtmltopdf / DinkToPdf | RaiCore.Pdf |
|---|---|
| `--page-size A4` / `PaperSize = PaperKind.A4` | `@page { size: A4 }` |
| `--page-width`, `--page-height` | `@page { size: 210mm 297mm }` |
| `--orientation Landscape` / `Orientation.Landscape` | `@page { size: A4 landscape }` |
| `--margin-top 20mm` / `Margins` | `@page { margin-top: 20mm }` |
| `--title` / `DocumentTitle` | `HtmlConverterOptions.Title`, or the document's `<title>` |
| `--print-media-type` | always: the CSS is applied with `print` media |
| `--no-background` | no option: backgrounds are drawn as the CSS says (remove them in `@media print`) |
| `--outline` / `Outline` | on by default (`CreateBookmarks`), from `h1`-`h6` |
| `--outline-depth 2` | `h3, h4, h5, h6 { bookmark-level: none }` |
| `toc` / `--xsl-style-sheet` | a contents list of links with `target-counter()` and `leader()` |
| `--user-style-sheet` / `WebSettings.UserStyleSheet` | `HtmlConverterOptions.UserStyleSheet` (the CSS text, not a path) |
| `--encoding` / `DefaultEncoding` | not needed: the HTML is a .NET string |
| `--enable-local-file-access`, `--allow` | the default `ResourceLoader` reads `file:` URIs; pass your own to restrict or widen it |
| `--javascript-delay`, `--window-status`, `--no-stop-slow-scripts` | not needed: no JavaScript runs |
| `--zoom`, `--dpi`, `--disable-smart-shrinking` | no equivalent: CSS units are exact (see below) |
| `--grayscale` | no equivalent |
| `--cover` | the first element of the document, with `@page :first` for its own margins (or a named page) |
| `--page-offset 4` | `@page :first { counter-set: page 5 }` (`counter(pages)` still counts the pages) |

## Headers, footers and page numbers

wkhtmltopdf replaces placeholders in header and footer text; RaiCore.Pdf uses CSS content values in margin boxes:

| wkhtmltopdf | RaiCore.Pdf |
|---|---|
| `[page]` | `counter(page)` |
| `[topage]`, `[toPage]` | `counter(pages)` |
| `[title]` | the title as a string, or `string(heading)` with `h1 { string-set: heading content() }` |
| `[section]`, `[subsection]` | `string(chapter)` with `h1 { string-set: chapter content() }` (`h2` for subsections) |
| `[date]` | write the date into the stylesheet when you build it |
| `--header-line` | an HTML header (below) with `border-bottom` |
| `--header-spacing` | the page margin and the margin box's `vertical-align` |

An HTML header (`--header-html header.html`, `HeaderSettings.HtmUrl`) becomes a running element in the document
itself, with no separate page and no JavaScript to fill in the numbers:

```html
<style>
  @page { margin-top: 30mm; @top-left { content: element(letterhead); } }
  .letterhead { position: running(letterhead); }
</style>
<div class="letterhead"><img src="logo.svg" alt="Example" height="40"> Example Consulting</div>
```

## Fonts

wkhtmltopdf uses the fonts installed on the machine (through fontconfig on Linux). RaiCore.Pdf uses its bundled
Liberation fonts unless told otherwise, so `font-family: Arial` gets the metric-compatible Liberation Sans on every
machine. To use the installed fonts as before:

```csharp
var options = new HtmlConverterOptions { Fonts = new FontCollection().AddSystemFonts().AddBundledFonts() };
```

## Differences to expect

- **Sizes.** wkhtmltopdf shrinks pages ("smart shrinking") and has a DPI quirk, so the same CSS comes out smaller
  there than in a browser. RaiCore.Pdf renders CSS units exactly, like a browser's print preview, so text and boxes
  may look larger than they did; adjust the CSS rather than looking for a zoom factor.
- **Modern CSS works.** Flexbox, `calc()`, variables, `break-before`/`break-inside` and `@page` margin boxes behave as
  in current browsers, so old workarounds (`display: -webkit-box`, `page-break-*` hacks, tables for layout) can go,
  though they still work.
- **No JavaScript.** Scripts that filled in headers (`subst()` on `window.location.search`) or built content are not
  needed or not run; see the [known differences](README.md#known-differences).

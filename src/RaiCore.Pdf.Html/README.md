# RaiCore.Pdf.Html

Convert HTML and CSS to PDF in .NET with RaiCore.Pdf's own layout engine. There is no headless browser, no native
Chromium and no JavaScript, so it is fast, deterministic and runs anywhere .NET runs.

```sh
dotnet add package RaiCore.Pdf.Html
```

```csharp
using RaiCore.Pdf.Html;

var pdf = HtmlToPdf.Convert("""
    <style>
      @page { size: A4; margin: 20mm; @bottom-right { content: "Page " counter(page) " of " counter(pages); } }
      body { font-family: Arial, sans-serif; }
    </style>
    <h1>Invoice 2026-0042</h1>
    <table border="1"><tr><th>Item</th><th>Price</th></tr><tr><td>Consulting</td><td>1.234,50 kr.</td></tr></table>
    """);
pdf.Save("invoice.pdf");
```

Supported: the CSS cascade and print media queries, the box model, fonts and text styling, translucent colors and
`opacity` (as PDF transparency), lists, images
(JPEG, PNG, GIF, BMP, WebP and SVG from `data:` and `file:` URLs), inline `<svg>` drawn as vectors, linked stylesheets, tables (repeated headers and footers, `colspan`/`rowspan`, collapsed
borders), flexbox and CSS Paged Media: `@page` size and margins, headers and footers in margin boxes, page
counters, `string-set` and running elements. `::before`/`::after` generated content (strings, `attr()`, quotes and
images, styled inline or as blocks). Headings become PDF bookmarks, customized with `bookmark-level`,
`bookmark-label` and `bookmark-state`. Links (`<a href>`) are clickable, to URIs and to elements by `#id`.

Not yet: floats, positioning, grid (rendered as blocks) and `text-decoration`.

Fonts default to the bundled Liberation fonts (metric-compatible with Arial, Times New Roman and Courier New), so
output is identical on every machine. Brand fonts come from `@font-face` rules (TTF, OTF, WOFF, WOFF2, `data:` URIs,
`unicode-range`) or from your own `FontCollection`. Network access is off unless you supply a `ResourceLoader`, so
fonts from a CDN such as Google Fonts need a loader that downloads them.

For Razor components, see [RaiCore.Pdf.Razor](https://www.nuget.org/packages/RaiCore.Pdf.Razor).

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)

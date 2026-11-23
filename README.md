# RaiCore.Pdf

[![CI](https://github.com/raicevic/raicore-pdf/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/raicevic/raicore-pdf/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/RaiCore.Pdf.Html)](https://www.nuget.org/packages/RaiCore.Pdf.Html)

An open-source .NET library for creating PDF documents and converting HTML, Razor and office documents to PDF, built on its own layout engine with no headless browser.

> **Status:** 1.0, the first release: HTML, CSS and Razor to PDF with a stable API. See the
> [documentation](https://raicevic.github.io/raicore-pdf/), the [changelog](CHANGELOG.md) and the [roadmap](#roadmap).

## Packages

| Package | Purpose | Status |
|---|---|---|
| `RaiCore.Pdf.Html` | HTML + CSS (including CSS Paged Media) → PDF | ✅ 1.0 |
| `RaiCore.Pdf.Razor` | Razor components → PDF | ✅ 1.0 |
| `RaiCore.Pdf.Layout` | Format-neutral layout engine: text shaping, line breaking, pagination, tables, flexbox | ✅ 1.0 |
| `RaiCore.Pdf.Fonts` | Bundled Liberation fonts (metric-compatible with Arial, Times New Roman, Courier New) | ✅ 1.0 |
| `RaiCore.Pdf` | PDF object model, writer, fonts, images and drawing API | ✅ 1.0 |
| `RaiCore.Pdf.Extraction` | Read existing PDFs: text by page in reading order, text search with positions, images with their placement and data | 🚧 Unreleased |
| `RaiCore.Pdf.Editing` | Edit existing PDFs: delete, insert, move and rotate pages, save repeatedly | 🚧 Unreleased |
| `RaiCore.Pdf.Email` | Email (.eml, Outlook .msg) and web archives (.mht) → PDF: header block, inline images, attachments embedded or appended | 🚧 Unreleased |
| `RaiCore.Pdf.Sheets` | CSV and Excel (.xlsx) → PDF: paginated tables with repeated header rows, number formats, styles, merged cells, print areas | 🚧 Unreleased |
| `RaiCore.Pdf.Slides` | PowerPoint (.pptx) → PDF: one page per slide with masters, layouts, themes, shapes, text, pictures, tables | 🚧 Unreleased |
| `RaiCore.Pdf.Documents` | Word (.docx), OpenDocument text (.odt) and RTF → PDF: styles, lists, tables, sections, headers and footers with page numbers, images, notes | 🚧 Unreleased |
| `RaiCore.Pdf.XslFo` | XSL-FO → PDF | ⚪ After 1.0 |

All packages target .NET 10 and run on Windows, Linux (glibc and musl, so Alpine too) and macOS, x64 and Arm64, with
no native dependencies beyond HarfBuzz, which ships in the packages. Apps using them can be trimmed and published with
Native AOT, and run in containers (see [below](#trimming-and-native-aot)).

## Installation

Install the package for your input format from NuGet; the others come as dependencies:

```sh
dotnet add package RaiCore.Pdf.Html     # HTML and CSS
dotnet add package RaiCore.Pdf.Razor    # Razor components (includes RaiCore.Pdf.Html)
```

Symbols and SourceLink are published too, so you can step into the library code while debugging.

### Trimming and Native AOT

The libraries are trim- and AOT-compatible (`IsAotCompatible`). CI publishes an app that uses every package with
`PublishTrimmed` and with `PublishAot` on Windows, Linux and macOS, runs it and fails on unexpected trim warnings.
Drawing, layout, HTML conversion (AngleSharp and AngleSharp.Css are AOT-annotated too), reading and importing PDFs
and encryption publish without warnings, and HarfBuzz's native library is copied next to the executable.

Razor components work as well (parameters, child components, cascading values, `@inject` and
`OnInitializedAsync`), but Microsoft does not support Razor components with Native AOT, and
`Microsoft.AspNetCore.Components` itself reports four trim warnings (IL2072, in `ComponentProperties.SetProperties`,
`ComponentFactory.PerformPropertyInjection` and `CascadingParameterState.FindCascadingParameters`): it reflects over
the component's runtime type. The component types are kept, because `RazorToPdf` and `RazorPdfRenderer` take the root
component as an annotated (`[DynamicallyAccessedMembers]`) type and child components compile to annotated
`OpenComponent<T>` calls; a component that is only found by name at run time (with `Type.GetType`) can be trimmed
away. Test your components in the published app. If you build with `TreatWarningsAsErrors`, set
`ILLinkTreatWarningsAsErrors` (trimming) or `IlcTreatWarningsAsErrors` (Native AOT) to `false` to keep these
warnings from failing the publish.

### Docker and Linux containers

CI runs the package smoke test (also trimmed and Native AOT) in Alpine and Debian containers on x64 and Arm64.
[samples/Docker/Dockerfile](samples/Docker/Dockerfile) builds and runs a small HTML-to-PDF app on Alpine. What an image
needs:
- No fonts and no fontconfig: `RaiCore.Pdf.Fonts` bundles the Liberation fonts (for Arial, Helvetica, Times New
  Roman, Courier New and the generic families); other fonts come from `@font-face` or files you load.
- HarfBuzz ships for `linux-x64`, `linux-arm64`, `linux-musl-x64` and `linux-musl-arm64` and needs `libstdc++`,
  which the .NET images have (on a bare `alpine` base for a self-contained or Native AOT app: `apk add libstdc++`).
- ICU is optional. In globalization-invariant mode (the default in the Alpine runtime images) the PDFs come out the
  same, except that AES-256 passwords are not NFKC-normalized, so use ASCII passwords or ICU there. The `-extra`
  images include ICU, or `apk add icu-libs` and set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false`.
- Encryption uses OpenSSL on Linux (`libssl`, also in the .NET images).

## Quick start

[samples/](samples/README.md) has runnable apps for typical jobs: an invoice, a report with a table of contents, a
letter for a window envelope, an ASP.NET Core app with Razor components, a background worker and a Docker image.

Coming from wkhtmltopdf, Puppeteer/Playwright, IronPDF, Aspose or SelectPdf? See the
[migration guides](docs/migration/README.md).

### HTML to PDF

```csharp
using RaiCore.Pdf.Html;

var pdf = HtmlToPdf.Convert("""
    <style>
      @page { size: A4; margin: 20mm; }
      body { font-family: Arial, sans-serif; }
      .total { max-width: 200px; margin: 1em auto; border: 1px solid #333; text-align: center; }
    </style>
    <h1>Invoice 2026-0042</h1>
    <p>Thank you for your <b>order</b>.</p>
    <ul><li>Consulting</li><li>Support</li></ul>
    <div class="total">Total: 1.234,50 kr.</div>
    """);
pdf.Save("invoice.pdf");
```

HTML is parsed and styled with [AngleSharp](https://anglesharp.github.io/) (print media) and laid out by RaiCore.Pdf's
own engine. Supported today:
- The cascade, inheritance, `!important`, CSS variables and print media queries.
- Browser default styles for headings, paragraphs and lists.
- Block and inline formatting with white-space handling; `inline-block`, `inline-flex` and `inline-table` boxes flow
  in the text as one item (shrink-to-fit, aligned on their last line of text, or first row for tables).
- Backgrounds, borders, padding and horizontal margins on inline elements (highlighted text, tags), sliced where they
  break across lines or around a block inside them.
- `vertical-align` on inline text, images and inline boxes: `sub`, `super` (also for `<sub>`/`<sup>`), lengths,
  percentages, `middle`, `text-top`, `text-bottom`, `top` and `bottom`; nested shifts add up.
- `text-decoration`: underline, overline and line-through in every style (solid, double, dotted, dashed, wavy), with
  color, thickness and `text-underline-offset`, propagated to descendants and placed with the font's own metrics.
- `letter-spacing` and `word-spacing`, also with justified text.
- Hyphenation: `hyphens` (`manual`, the default, breaks only at soft hyphens `&shy;`; `none` not even there; `auto`
  adds the hyphenation points of the text's `lang`), `hyphenate-character` and `hyphenate-limit-chars`. Patterns for
  English (US and UK), German, Danish, Dutch, French, Spanish, Italian, Portuguese, Polish and Norwegian (Bokmål and
  Nynorsk) are bundled, from [hyph-utf8](https://github.com/hyphenation/tex-hyphen) under their own permissive
  licences (MIT, BSD or similar, see
  [the list](src/RaiCore.Pdf.Layout/Text/Hyphenation/Patterns/LICENSES.md)).
- Overflow: `overflow: hidden`/`clip`/`auto`/`scroll` clip the content to the padding box (with rounded corners), also
  floats and positioned children and on every page of a box split across pages; a clipped box of a fixed height does
  not make pages for what it hides. `text-overflow: ellipsis`. A fixed height taller than a page continues on the next.
- Backgrounds: `background-image` with `url()` (raster and SVG) and `linear-gradient`/`radial-gradient` (also
  repeating ones, translucent stops, toward corners), several layers, `background-repeat` (also `space` and `round`),
  `-position`, `-size` (`cover`, `contain`, lengths), `-origin` and `-clip`, and the `background` shorthand. Boxes split
  across pages show the slice of their background each fragment covers.
- Positioning: `position: relative` (drawn moved, keeping its place), `absolute` (in the padding box of the nearest
  positioned ancestor, or the page area of its page: insets, sizes from insets, shrink-to-fit, static position for
  auto insets) and `fixed` (repeated on every page, as browsers print it), painted over the flow by `z-index`; e.g. an
  address block in an envelope window.
- Floats and `clear` (CSS 2.1 section 9.5): `float: left | right` with shrink-to-fit widths, text that flows around
  floats (lines beside them are shortened, and move down when a word does not fit), `clear`, and the classic
  clearfix. Tables, flex containers, images and `display: flow-root`/`overflow` boxes are placed beside floats or
  below them, and new formatting contexts (those, table cells, flex items and floats) contain their floats. A float
  that does not fit on the rest of a page moves to the next one; one taller than a page continues on the next, and
  text keeps flowing around it there.
- Bidirectional text (the Unicode bidi algorithm, UAX #9): `direction`, `unicode-bidi`, the `dir` attribute (also
  `auto`), `<bdi>` and `<bdo>`; right-to-left paragraphs start on the right, and `text-align: start`/`end` follow the
  direction. Right-to-left tables put their first column on the right, flex rows and the cross axis of flex columns
  start on the right, lists and `<dd>` are indented on the right with the markers there, and a box narrower than its
  containing block goes to the containing block's start side. The logical margins and padding (`margin-inline-start`,
  `padding-inline`, ...) follow each element's direction.
- Generated content: `::before` and `::after` (also `:before`/`:after`) with their own styles, inline, `inline-block`
  or block-level, in any element, list item, table cell or flex container. `content` takes strings with CSS escapes
  (`"\201C"`, `"\A"` with `white-space: pre`), `attr()`, `open-quote`/`close-quote` (following `quotes`, nested
  quotes alternate between double and single; `<q>` gets them by default), `no-open-quote`/`no-close-quote` and
  `url()` images; `none` and `normal` generate nothing.
- CSS counters: `counter-reset`, `counter-increment` and `counter-set` with nested scopes (CSS Lists 3), shown with
  `counter()` and `counters()` (e.g. "1.2.3" section numbers) in any list-style counter style, in generated content,
  `string-set` and `bookmark-label`. List items count with the implicit `list-item` counter (honoring `start`,
  `reversed` and `value`), which their markers show.
- The box model: margin, border (every `border-style`: solid, dashed, dotted, double, groove, ridge, inset and
  outset), `border-radius` (elliptical and percentage radii, every border style, rounded images), padding, background
  and `background-clip`, width, `min-width`, `max-width`, `height`, `min-height`, `max-height`, auto margins,
  `box-sizing` and percentages, which resolve against the width the containing block gets in layout (a flex item after
  flexing, a table column, a shrink-to-fit float or `inline-block`). `min-width`/`max-width` also limit shrink-to-fit boxes and scale images (with
  `max-width: 100%; height: auto` keeping the aspect ratio). Percentage heights resolve against definite ancestor heights, starting with the page area (so
  `html, body { height: 100% }` and `100vh` fill the page).
- `calc()`, `min()`, `max()` and `clamp()`, nested and with CSS variables, in every length: widths, heights, margins,
  padding, `flex-basis`, gaps, `font-size`, `line-height`, `text-indent`, `vertical-align`, `letter-spacing`,
  `word-spacing`, `text-decoration-thickness`, `text-underline-offset`, table and `<col>` widths, and `@page` size and
  margins. Percentages inside them resolve like plain percentages (e.g. a sidebar layout with
  `width: calc(100% - 200px)`); `vw`, `vh`, `vmin` and `vmax` are the page content area.
- Fonts (`font-family` fallback lists, weight, style), color, `text-align`, `text-indent`, `line-height` and `text-transform`.
- Transparency: translucent colors (`rgba()`, `hsla()`, `#rrggbbaa`, `transparent`) for text, backgrounds and
  borders, and `opacity`, which composites an element with everything inside it as one PDF transparency group (one
  per page when the element is split across pages).
- Web fonts: `@font-face` with `url()` (TTF, OTF, TTC, WOFF, WOFF2, also as `data:` URIs) and `local()` sources,
  `format()` hints, `font-weight` ranges for variable fonts, `font-style` and `unicode-range` (so per-script files as
  Google Fonts serves them work as one family). Fonts load through the `ResourceLoader` when text first needs them;
  URLs in linked stylesheets resolve against the stylesheet.
- List markers: bullets, decimal, alpha and roman numerals, `start`, `reversed` and `value` (the `list-item` counter).
- Images from `data:` and `file:` URLs (JPEG, PNG, GIF, BMP, WebP, TIFF and SVG), inline `<svg>` elements drawn as vectors (sized
  like images; `currentColor` follows the CSS `color`), linked stylesheets, `@page` size and margins, and page break
  properties.
- Tables: automatic and fixed column widths, `colspan`/`rowspan`, `thead` and `tfoot` rows repeated on every page,
  `border-collapse`/`border-spacing`, `vertical-align`, `<col>` widths, captions, and the legacy
  `border`/`cellpadding`/`cellspacing`/`width` attributes. A row that does not fit is split like browsers do: each
  cell continues on the next page below the repeated header (paragraph orphans and widows still apply, so rows of a
  few lines move whole); `tr { break-inside: avoid }` keeps rows whole.
- Flexbox: `flex-direction`, `flex-wrap`, `justify-content`, `align-items`/`align-self` (including `stretch` and
  `baseline`), `align-content`, `gap`, `order`, `flex`/`flex-grow`/`flex-shrink`/`flex-basis`, min/max sizes and
  auto margins. Containers with a height flex their items vertically too, e.g. a full-page cover whose footer sits
  at the bottom (`margin-top: auto` or `flex: 1` on the content).
  A row too tall for the page is split like browsers do: each item continues on the next page in its own column,
  and stretched items (e.g. a sidebar background) run down to the end of the row.

Headers and footers come from CSS Paged Media:

```css
@page {
  size: A4; margin: 25mm 20mm;
  @top-left     { content: element(letterhead); }           /* any HTML, e.g. a logo */
  @top-right    { content: string(chapter); font-style: italic; }
  @bottom-right { content: "Page " counter(page) " of " counter(pages); font-size: 8pt; }
}
@page :first { @top-left { content: none; } }
.letterhead { position: running(letterhead); }
h1 { string-set: chapter content(); }
```

- Page sizes: lengths (`size: 210mm 297mm`), `landscape`/`portrait`, and the keywords `A0`-`A10`, `B0`-`B10`,
  `JIS-B4`, `JIS-B5`, `letter`, `legal` and `ledger` (in any case).
- Margin boxes: `top-`/`bottom-` `left`, `center`, `right` and corners, with font, color, alignment and
  vertical-align.
- Page selectors: `:first`, `:left`, `:right`, `:blank` and page names, combined (`@page chapter:first`) and in
  lists, cascaded by specificity. Each page gets its own `size` and margins from them, e.g. mirrored margins on left
  and right pages, and margin boxes are placed in each page's own margins.
- Named pages: `page: wide` with `@page wide { size: A4 landscape }` puts an element (say, a wide table) on pages of
  its own; a change of page name forces a page break, and percentages inside resolve against that page's width.
- `break-before`/`break-after: left`, `right`, `recto` and `verso` insert a blank page when needed (styled with
  `@page :blank`).
- Content values: `counter(page)` and `counter(pages)` in any counter style, `string()` with
  `first`/`start`/`last`/`first-except`, `element()` for running elements, and strings with CSS escapes.
- Page counters: `counter-reset`, `counter-increment` and `counter-set` in `@page` rules (also named pages and
  `:first`/`:left`/`:right`/`:blank`) change the `page` counter (e.g. `@page :first { counter-set: page 5 }`) and
  named counters of the page context, which margin boxes show with `counter()`. Blank pages count. Document counters reach margin boxes through `string-set` (e.g. `string-set: chapter counter(chapter)`).

Cross-references and tables of contents come from CSS GCPM: `target-counter()` gives the page (or any counter) of
the element a link points to, `target-text()` its text, and `leader()` fills the line up to the page number:

```css
nav a::before { content: target-text(attr(href), before); }       /* "2.1 " from the heading's ::before */
nav a::after  { content: leader(dotted) target-counter(attr(href), page); }
h2::before    { content: counter(chapter) "." counter(section) " "; }
```

- `target-counter(url, name[, style])` and `target-counters(url, name, separator[, style])`, with `page` for the page
  number; the URL is `attr(href)` (or a `"#id"` string). Page numbers of elements further on are found by laying the
  document out again (up to four times, until they stop changing), so documents using them take about two to three
  times as long to convert.
- `target-text(url[, content|before|after|first-letter])`.
- `leader(dotted|solid|space|"string")`: the pattern repeats to fill the rest of the line; leaders on one line share
  the space.

Headings become PDF bookmarks (the outline in a viewer's navigation panel), nested by level. The GCPM properties
customize them, and `HtmlConverterOptions.CreateBookmarks = false` turns them off:

```css
h1.subtitle { bookmark-level: none; }                        /* not in the outline */
.chapter    { bookmark-level: 1; bookmark-label: "Chapter: " attr(data-title); }
h2          { bookmark-state: closed; }                       /* collapsed until opened */
```

- `bookmark-level`: `none` or a level (`h1`-`h6` default to 1-6; a skipped level nests under the nearest shallower
  entry).
- `bookmark-label`: strings, `content()` (the element's text, the default), `content(first-letter)`,
  `content(before)`/`content(after)` (the text of the `::before`/`::after`) and `attr()`.
- `bookmark-state`: `open` (the default) or `closed`.

Links are clickable: `<a href>` to web pages, `mailto:`, `tel:` and other URIs (relative URLs resolve against
`HtmlConverterOptions.BaseUri`, or stay as written without one), and `href="#id"` to any element with that `id` (or
an `<a name>`), on whatever page it ends up. A link that wraps is clickable on every line and page it covers, and an
`<a>` around blocks (a card, a button with `display: block`) is clickable over their whole box. Links to ids that do
not exist are left out.

The document metadata comes from the HTML: the title from `<title>`, the language (the PDF's `/Lang`, which screen
readers use) from `<html lang>` or `xml:lang`, and the author, subject, keywords and creator from
`<meta name="author|description|keywords|generator">`. Options override them:

```csharp
var pdf = HtmlToPdf.Convert(html, new HtmlConverterOptions { Title = "Invoice 2026-0042", Language = "da-DK" });
```

Network access is off unless you pass a `ResourceLoader`. It gets a `ResourceRequest` with the resolved `Uri`, the
`Kind` of resource (style sheet, image or font) and a `CancellationToken` that is canceled with the conversion:

```csharp
var http = new HttpClient();
var options = new HtmlConverterOptions
{
    BaseUri = new Uri("https://example.com/invoices/"),
    ResourceLoader = request => request.Uri.Scheme is "https"
        ? http.GetByteArrayAsync(request.Uri, request.CancellationToken).GetAwaiter().GetResult()
        : ResourceLoaders.Default(request),
};
```

`HtmlToPdf.CreateFlowDocument` returns the laid-out document before rendering, e.g. to set your own page decorator.

`HtmlConverterOptions.Diagnostics` tells you why the output may differ from the browser's: images, style sheets and
fonts that did not load (and why: nothing loaded, unreadable, too large, not embeddable), and CSS the converter does
not support: properties, invalid values and values laid out differently (`display: grid`), selectors and
pseudo-elements it cannot match (`::first-letter`), and at-rules it skips (`@import`, `@layer`); and content that
does not fit: lines and boxes wider than their room, lines taller than a page (cut off), images scaled down to fit
a page. CSS that has no effect in print anyway (`cursor`, `transition`, vendor prefixes) comes as `Info`. Each issue
comes once per conversion, with the path of the first element it concerns and how many elements it concerns
(`FlowDocument.Diagnostics` reports the layout issues for documents built with the layout API):

```csharp
var options = new HtmlConverterOptions
{
    Diagnostics = d => logger.LogWarning("{Kind} {Subject} at {Path} ({Count}x): {Message}", d.Kind, d.Subject, d.ElementPath, d.Count, d.Message),
};
```

`HtmlConverterOptions.Encryption` password-protects the PDF or restricts permissions (see
[Drawing directly](#drawing-directly) for `PdfEncryption`); Razor conversion takes the same options:

```csharp
var pdf = HtmlToPdf.ConvertToBytes(html, new HtmlConverterOptions
{
    Encryption = new PdfEncryption { OwnerPassword = "admin", Permissions = PdfPermissions.Print },
});
```

`HtmlConverterOptions.PdfAConformance` writes PDF/A-2b, PDF/A-3b, PDF/A-2a or PDF/A-3a for archiving (see
[PDF/A](#pdfa)); the conversion throws if the document breaks a rule, e.g. text that no font has a glyph for:

```csharp
var pdf = HtmlToPdf.ConvertToBytes(html, new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA3B });
```

**Untrusted HTML.** `HtmlConverterOptions.Limits` bounds what a document can make the conversion do, so a server
cannot be crashed or stalled by hostile input. A structural limit fails the conversion with
`ResourceLimitExceededException` (its `LimitName` says which); an image, stylesheet or web font over a size limit is
skipped like a broken one. Parsing, layout and rendering check the time limit and the cancellation token as they go.

```csharp
var limits = ResourceLimits.Default with { MaxPages = 100, Timeout = TimeSpan.FromSeconds(30) };
var pdf = await HtmlToPdf.ConvertAsync(html, new HtmlConverterOptions { Limits = limits }, cancellationToken);
```

| Limit | Default | Exceeded |
| --- | --- | --- |
| `MaxNestingDepth` (HTML elements) | 256 | fails |
| `MaxElements` | 1,000,000 | fails |
| `MaxTableCells` (one table's grid) | 2,000,000 | fails |
| `MaxPages` | 10,000 | fails |
| `Timeout` | 5 minutes | fails |
| `MaxImagePixels` (one image) | 100,000,000 | image skipped |
| `MaxResourceBytes` (one image, stylesheet or font) | 64 MB | resource skipped |
| `MaxFontBytes` (a WOFF/WOFF2 font, decompressed) | 64 MB | font skipped |
| `MaxDecompressedBytes` (one PDF stream) | 256 MB | fails |

The same defaults protect `PdfImage.Load`, `PdfFont.Load` and `PdfReader`; `using (limits.Enforce(cancellationToken))`
applies other limits (and a time limit) to the work in its scope. The default `ResourceLoader` reads local files
(`file:` URLs); for untrusted HTML, pass one that only loads what you allow.

Not yet: grid layout (rendered as blocks), `@page :nth()` and document counters in margin
boxes (use `string-set`). `overflow` other than `visible` clips to the padding box (rounded corners included), and
`text-overflow: ellipsis` ends a line that does not fit with "…". The size of an image in text (`max-width: 100%` on an
inline `<img>`) and `min()`/`max()`/`clamp()` of a percentage are resolved against the width the containing block is
expected to have, so they do not follow a flex item that shrinks; give the image `display: block` to make its
`max-width` follow.

### Razor to PDF

`RaiCore.Pdf.Razor` renders Razor components (`.razor` files) to HTML with Blazor's static `HtmlRenderer` and converts
the HTML with `HtmlToPdf`. It needs no web server, browser or HTTP request, so it works in console apps, background
jobs and Azure Functions as well as in ASP.NET Core.

```razor
@* InvoiceDocument.razor *@
<!DOCTYPE html>
<html>
<head>
    <title>Invoice @Invoice.Number</title>
    <style>
        @@page { size: A4; margin: 20mm; @@bottom-right { content: counter(page) " / " counter(pages); } }
        body { font-family: Arial, sans-serif; }
    </style>
</head>
<body>
    <h1>Invoice @Invoice.Number</h1>
    <table>
        @foreach (var line in Invoice.Lines)
        {
            <tr><td>@line.Description</td><td>@line.Amount.ToString("N2")</td></tr>
        }
    </table>
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

When components `@inject` services, register the renderer with dependency injection. It is scoped, so components
share the request's scoped services. Here `InvoicePage` takes an `Id` parameter, injects a `DbContext` and loads the
invoice in `OnInitializedAsync`:

```csharp
builder.Services.AddRazorPdf();

app.MapGet("/invoices/{id}.pdf", async (int id, RazorPdfRenderer renderer) =>
{
    var pdf = await renderer.ConvertAsync<InvoicePage>(new Dictionary<string, object?> { ["Id"] = id });
    return Results.File(pdf.ToArray(), "application/pdf", $"invoice-{id}.pdf");
});
```

For long documents, `ConvertToStreamAsync` writes the PDF straight to the response, page by page and with
asynchronous writes only, as ASP.NET Core requires:

```csharp
app.MapGet("/reports/{id}.pdf", async (int id, RazorPdfRenderer renderer, HttpResponse response, CancellationToken cancel) =>
{
    response.ContentType = "application/pdf";
    await renderer.ConvertToStreamAsync<ReportPage>(response.Body, new Dictionary<string, object?> { ["Id"] = id }, cancellationToken: cancel);
});
```

Outside a host, pass any `IServiceProvider` to `new RazorPdfRenderer(services)`.

- Parameters, child components, layout components with `ChildContent`, `@foreach`/`@if` and HTML encoding work as in
  Blazor. Rendering waits for `OnInitializedAsync`, so components can load their own data.
- Put CSS in a `<style>` element (or `HtmlConverterOptions.UserStyleSheet`) and write at-rules with a double `@` in
  `.razor` files: `@@page`, `@@media print`. CSS isolation files (`.razor.css`) are not applied.
- `RenderHtmlAsync` returns the HTML for debugging; `CreateFlowDocumentAsync` returns the laid-out document.
- Static rendering only: event handlers and JavaScript interop do nothing, interactive render modes are not
  supported, and `<PageTitle>`/`<HeadContent>` have no effect (use a `<title>` element). MVC views and Razor Pages (`.cshtml`) are not
  supported.

### Drawing directly

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

Document metadata goes to the Info dictionary and to an XMP metadata stream, which always agree (same values, same
dates); custom properties too:

```csharp
doc.Info.Author = "Ada Lovelace";
doc.Info.Keywords = "invoice, 2026";                      // also XMP dc:subject, split at commas and semicolons
doc.Info.CustomProperties["Invoice number"] = "2026-0042"; // Info /Invoice#20number and XMP pdfx:Invoice_x0020_number
doc.Language = "en-GB";                                    // the catalog's /Lang and XMP dc:language
```

Colors may be translucent: a color's alpha becomes the fill or stroke alpha. A group is composited as a whole with
one opacity, so its overlapping shapes do not show through each other:

```csharp
page.Canvas
    .SetFillColor(new PdfColor(1, 0, 0, alpha: 0.5)).Rectangle(72, 500, 100, 100).Fill()   // 50% red
    .BeginGroup(0.5)                                                                     // like CSS opacity
    .SetFillColor(PdfColor.FromRgb(0, 128, 0)).Rectangle(200, 500, 100, 100).Fill()
    .SetFillColor(PdfColor.FromRgb(0, 0, 255)).Rectangle(250, 550, 100, 100).Fill()
    .EndGroup();
```

Bookmarks (the document outline) jump to a page and position, nest, and take any Unicode title:

```csharp
var chapter = new PdfOutlineItem("Chapter 1", page, top: page.Height - 72);
chapter.Children.Add(new PdfOutlineItem("Section 1.1", page, top: 400) { IsOpen = false });
doc.Outline.Add(chapter);   // viewers open the outline panel (see PdfDocument.PageMode)
```

Links are rectangles of a page that open a URI or jump to a place in the document, directly or through a named
destination (which can be added after the link):

```csharp
page.AddLink(72, 700, 120, 14, new Uri("https://example.com/"));       // x, y, width, height
page.AddLink(72, 680, 120, 14, "appendix");                           // a named destination
doc.NamedDestinations["appendix"] = new PdfDestination(lastPage, top: lastPage.Height - 72);
page.Canvas.AddLink(0, 0, 50, 20, new PdfDestination(firstPage));     // in the canvas's transformed coordinates
```

Encryption protects a document with a password, or lets anyone open it but restricts what they may do (permissions
are enforced by the viewer). AES-256 (PDF 2.0, security handler revision 6) is the default; AES-128 (revision 4) is
there for old readers:

```csharp
doc.Encryption = new PdfEncryption
{
    UserPassword = "open sesame",                 // leave out to open without a password
    OwnerPassword = "full access",                // leave out for a random one (nobody can lift the restrictions)
    Permissions = PdfPermissions.Print | PdfPermissions.PrintHighQuality,   // default: PdfPermissions.All
    // Algorithm = PdfEncryptionAlgorithm.Aes128,
};
```

Every string and stream is encrypted (the XMP metadata stream optionally not: `EncryptMetadata = false`). Passwords
are prepared with a simplified SASLprep (non-ASCII spaces become spaces, NFKC normalization, prohibited characters
are rejected) and cut to 127 UTF-8 bytes; AES-128 passwords must be Latin-1. Encrypted output is not byte-for-byte
reproducible, because keys and IVs are random.

### Accessible PDF (tagged PDF and PDF/UA)

`HtmlConverterOptions.Tagged` writes a tagged PDF: the logical structure screen readers, reflowing viewers and
accessibility checkers use, from the HTML semantics. `PdfUA` also makes it PDF/UA-1 (it needs a `<title>` and
`<html lang>`, or the `Title`/`Language` options, and `alt` text on images):

```csharp
var pdf = HtmlToPdf.Convert(html, new HtmlConverterOptions { PdfUA = true });
```

- Headings `h1`-`h6`, paragraphs (`p`, `pre`, `address`, and text directly in other blocks), `blockquote`, `section`,
  `article`, captions; lists with a label (`Lbl`, the marker) and body (`LBody`) per item and the numbering style;
  tables with header, body and footer row groups, header cells with a scope (`scope`, else by position) and spans;
  images and inline SVG as figures with their `alt` text (an SVG's `<title>` or `aria-label`), `alt=""` as an
  artifact; links with their text as the annotation's description; `lang` on blocks.
- Backgrounds, borders, text decorations, `hr`, page margin boxes and table header and footer rows repeated on later
  pages are artifacts, so screen readers skip them. Reading order is document order.
- The document title is shown in the viewer's title bar (`/DisplayDocTitle`), and pages use the structure's tab order.

Drawn content is tagged with `PdfDocument.Tagged` and `PdfCanvas.BeginTag`/`EndTag` around structure elements
(`PdfDocument.StructureRoot.AddChild`), `BeginArtifact` for decorations; content outside any tag becomes an artifact
(or, with `PdfDocument.StrictTagging`, throws), and links get Link elements. Figures, formulas and tables drawn on one
page get a `/BBox` computed from what is drawn for them. Pages imported from other files keep their own content as
an artifact (their structure is not taken over); what is drawn on them is tagged as usual. For PDF/UA, their fonts
must still be embedded. Layout blocks take a `Tag` (`StructureTag`), with defaults for paragraphs, images and
tables:

```csharp
var doc = new PdfDocument { PdfUA = true, Language = "en", Info = { Title = "Report" } };
var heading = doc.StructureRoot.AddChild(PdfStructureTypes.Heading1);
doc.AddPage().Canvas.BeginTag(heading).DrawText("Report", font, 24, 72, 760).EndTag();
```

### PDF/A

`PdfAConformance.PdfA2B` and `PdfA3B` write PDF/A (ISO 19005-2 and -3, level B) for long-term archiving: the
document gets an sRGB output intent and XMP that identifies it as PDF/A, and saving checks the rules, throwing an
`InvalidOperationException` that names the problem. Every font must be embedded: `EmbedStandardFonts()` (in
RaiCore.Pdf.Fonts) embeds the metric-compatible Liberation fonts in place of Helvetica, Times and Courier, also for
SVG text. Encryption, CMYK images, text that the font has no glyph for, and pages taken from other PDF files are
rejected. CI validates the output with [veraPDF](https://verapdf.org).

```csharp
var doc = new PdfDocument { PdfAConformance = PdfAConformance.PdfA2B }.EmbedStandardFonts();
doc.AddPage().Canvas.DrawText("Archived", StandardFont.Helvetica, 12, 72, 720);
```

`PdfA2A` and `PdfA3A` (level A, "accessible") add the logical structure: the document is tagged (see
[Accessible PDF](#accessible-pdf-tagged-pdf-and-pdfua); HTML and layout documents are tagged from their semantics),
every glyph maps to Unicode, and saving also requires a language (a BCP 47 tag, like every element's), the standard
structure types only, and alternate text on figures (`alt` in HTML; `alt=""` makes an image decorative). As at level
B, a rule the content breaks throws `InvalidOperationException` naming it; the check runs when saving, so set the
level before drawing, which is what tags the content:

```csharp
var pdf = HtmlToPdf.ConvertToBytes(html, new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA2A }); // <html lang> and alt text
```

Files can be attached (`PdfDocument.Attachments`; PDF/A-3 allows any file, PDF/A-2 none), and
`AttachFacturXInvoice` makes a Factur-X / ZUGFeRD e-invoice: a PDF/A-3b carrying the invoice as CII XML. The XML is
checked to be well-formed, not validated against the profile's rules:

```csharp
var invoice = HtmlToPdf.Convert(invoiceHtml, new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA3B });
invoice.AttachFacturXInvoice(File.ReadAllBytes("factur-x.xml"), FacturXProfile.EN16931);
invoice.Attachments.Add(new PdfAttachment("timesheet.csv", csv, "text/csv") { Relationship = PdfAttachmentRelationship.Supplement });
invoice.Save("invoice.pdf");
```

### Existing PDFs

`PdfReader` reads existing PDF files, so a generated document can take pages from them: add a cover page, append
attachments, merge documents, or print on a letterhead:

```csharp
var terms = PdfReader.Open("terms.pdf");
var letterhead = PdfReader.Open("letterhead.pdf");

var doc = new PdfDocument();
var cover = doc.AddPage(PdfPageSize.A4);
cover.Canvas.DrawPage(letterhead.Pages[0], 0, 0);              // an existing page drawn under new content
cover.Canvas.DrawText("Contract", StandardFont.HelveticaBold, 32, 72, 600);

var pages = doc.ImportPages(terms, new PdfImportOptions { KeepOutline = true });   // all pages, with bookmarks
doc.InsertPages(1, PdfReader.Open("summary.pdf"), [0, 2]);    // pages 1 and 3, after the cover
pages[0].Canvas.DrawText("Annex A", StandardFont.Helvetica, 10, 500, 20);         // stamped on an imported page
doc.Save("contract.pdf");
```

Imported pages keep their content, fonts and images exactly (streams are copied, not decoded and re-encoded), and
what pages share is written once, also across several imports from one reader and pages drawn with `DrawPage`. Links
within the file are pointed at the imported pages (links to pages left out are dropped), and the outline can be kept.
The reader handles classic and cross-reference-stream files, object streams, incremental updates and damaged files
(it scans for objects when the cross-reference data is missing or wrong). Encrypted files open with their password
(the standard security handler: AES-256, AES-128 and the older RC4 40 and 128-bit); without one, the empty user
password is tried, which opens files that are only restricted by permissions. A wrong password throws
`PdfPasswordException`, and `IsEncrypted`, `IsOpenedWithOwnerPassword` and `Permissions` tell what was found; the
permissions are the author's wishes, which the reader leaves to you to honor.

```csharp
var report = PdfReader.Open("report.pdf", password: "secret");
```

The `RaiCore.Pdf.Extraction` package reads the text of existing PDFs from any producer, page by page and in reading
order: columns one after the other, tables row by row, Arabic and Hebrew in logical order. `HasText` tells which pages
are scans without text. `Find` searches the text and gives each match's page and rectangles, and `ImageExtractor` lists
the images on each page with their placement and exports them as PNG, JPEG or TIFF files. See the
[text extraction](https://raicevic.github.io/raicore-pdf/guides/text-extraction.html) and
[image extraction](https://raicevic.github.io/raicore-pdf/guides/image-extraction.html) guides.

```csharp
foreach (var page in new TextExtractor(PdfReader.Open("report.pdf")).ExtractPages())
{
    Console.WriteLine(page.HasText ? page.Text : $"Page {page.PageIndex + 1} is a scan");
}
```

The `RaiCore.Pdf.Editing` package changes existing PDFs: delete, insert, move and rotate pages, and save as often as
needed. Each save writes only what is still used, so deleted pages' fonts and images leave the file, along with the
bookmarks and links that pointed at them. See the [editing guide](https://raicevic.github.io/raicore-pdf/guides/editing.html).

```csharp
var editor = PdfEditor.Open("contract.pdf");
editor.Pages.Delete(0);
editor.Pages.Add(PdfReader.Open("appendix.pdf"));
editor.Save("contract-edited.pdf");
```

The `RaiCore.Pdf.Email` package converts email messages (`.eml`), Outlook messages (`.msg`) and web archives (`.mht`)
to PDF: a header block
(from, to, cc, date, subject), the HTML or plain text body with its inline images in place, and the attachments
embedded, listed and, if you like, appended as pages (images, text, PDFs, HTML files and attached messages). See the
[email guide](https://raicevic.github.io/raicore-pdf/guides/email.html).

```csharp
var pdf = EmailToPdf.Convert("message.eml", new EmailConverterOptions { AppendAttachments = true });
pdf.Save("message.pdf");
```

The `RaiCore.Pdf.Sheets` package converts CSV files and Excel workbooks to PDF. CSV becomes a table with the header
row repeated on every page, column widths fitted to the content within the page width (wrapping, never clipping), and
numbers and dates right-aligned; the delimiter, the encoding and the decimal separator (`1,234.56` or `1.234,56`) are
detected. Workbooks (`.xlsx`) keep their number formats, fonts, fills, borders, merged cells, column widths and row
heights, print areas and repeated rows, each sheet from a new page; formulas show their last calculated values. See
the [spreadsheet guide](https://raicevic.github.io/raicore-pdf/guides/sheets.html).

```csharp
var pdf = CsvToPdf.Convert("orders.csv", new CsvConverterOptions { Sheet = new SheetOptions { Landscape = true } });
pdf.Save("orders.pdf");

var report = XlsxToPdf.Convert("report.xlsx", new XlsxConverterOptions { SheetNames = ["Summary"] });
report.Save("report.pdf");
```

The `RaiCore.Pdf.Slides` package converts PowerPoint presentations (`.pptx`) to PDF, one page per slide at the slide's
size: the master's and layout's shapes and backgrounds, placeholders with their inherited positions and text styles,
theme colors and fonts, preset and free-form shapes with gradient and picture fills, lines and arrowheads, text with
bullets, numbering and autofit, cropped pictures, tables with their styles, groups and SmartArt drawings. See the
[PPTX guide](https://raicevic.github.io/raicore-pdf/guides/slides.html).

```csharp
var deck = PptxToPdf.Convert("deck.pptx", new PptxConverterOptions { Tagged = true });
deck.Save("deck.pdf");
```

The `RaiCore.Pdf.Documents` package converts Word documents (`.docx`) to PDF: paragraph, character and table styles,
multi-level numbered and bulleted lists, tab stops, tables with merged cells and header rows repeated on every page,
sections with their own page size and orientation, headers and footers for first, even and odd pages with page numbers,
images (inline or floating), hyperlinks, bookmarks, footnotes and endnotes, and a table of contents with the PDF's page
numbers. Headings become PDF bookmarks. It converts OpenDocument text documents (`.odt`, `.ott` and flat `.fodt`, as
LibreOffice writes them) the same way: styles, lists, tables, page styles with their headers and footers, images and
notes. `RtfToPdf` converts Rich Text Format documents (`.rtf`) the same way too, with their code pages, lists,
tables (nested ones too), sections, headers and footers, fields, pictures and notes. See the
[Word documents guide](https://raicevic.github.io/raicore-pdf/guides/documents.html).

```csharp
var pdf = DocxToPdf.Convert("letter.docx", new DocxConverterOptions { Tagged = true });
pdf.Save("letter.pdf");
OdtToPdf.Convert("report.odt").Save("report.pdf");
RtfToPdf.Convert("memo.rtf").Save("memo.pdf");
```

### Fonts

Any TrueType or OpenType font (`.ttf`, `.otf`, `.ttc`, `.woff`, `.woff2`) can be embedded. Only the glyphs you use
are included, and text stays searchable and copyable in every script:

```csharp
var font = PdfFont.Load("NotoSans-Regular.ttf");
page.Canvas.DrawText("Zażółć gęślą jaźń · Καλημέρα · Привет", font, 14, 72, 700);
double width = font.MeasureText("Hello", 14); // in points
```

Find fonts by family name, weight and style with CSS matching rules, falling back to metric-compatible substitutes:

```csharp
var fonts = BundledFonts.CreateCollection();   // identical output on every machine
// var fonts = new FontCollection().AddSystemFonts().AddBundledFonts();  // or use installed fonts too

PdfFont heading = fonts.Find("Arial", weight: 700)!;           // → Liberation Sans Bold
PdfFont body = fonts.Find(["Inter", "Helvetica", "sans-serif"])!; // first available family
```

Fonts with CFF or CFF2 outlines (`.otf`) are subset too: the used glyphs are embedded as a compact CID-keyed CFF
font, with subroutines inlined (which compresses better than subroutinized charstrings), and variable CFF2 fonts are
embedded at their default instance. Fonts whose license asks not to be subset are embedded whole, and fonts whose
license forbids embedding are rejected.

### Images

JPEG, PNG, GIF, BMP, WebP, TIFF and SVG images can be drawn directly or placed in the layout. JPEGs are embedded without
re-encoding (EXIF orientation is applied); PNGs of every color type and bit depth, including interlaced and
transparent ones, are decoded and verified against the full [PngSuite](http://www.schaik.com/pngsuite/). BMPs of
every common kind (palettes, RLE, 16/24/32-bit, bit fields, alpha) and WebP, lossy and lossless with alpha, are
decoded in managed code; lossy WebP matches libwebp bit for bit, and an animated WebP shows its first frame. TIFF
(bilevel, gray, palette, RGB and CMYK, 1 to 16 bits, strips or tiles, PackBits, LZW, Deflate, alpha, BigTIFF) is
decoded too, while its JPEG and CCITT fax data are embedded as they are; `PdfImage.GetPageCount` and
`PdfImage.Load(data, pageIndex)` read the pages of a multi-page TIFF, such as a fax. SVG is
drawn as PDF vector graphics, not rasterized, so logos and icons stay sharp at any zoom. Each image is stored once per document however
often it is drawn.

```csharp
var logo = PdfImage.Load("logo.png");
page.Canvas.DrawImage(logo, x: 400, y: 760, width: 120, height: 40);

flow.Add(new ImageBlock(logo, width: 150) { Alignment = BlockAlignment.Right });          // a block image
new Paragraph(body).Add("Status: ").AddImage(checkIcon, 10, 10).Add(" paid");            // an inline image
new Paragraph(body).Add("Total ").AddBox(badgeBlock).Add(" due");                        // an inline box (any block)

var icon = PdfImage.LoadSvg("""<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="10" fill="#1a4f8b"/></svg>""");
```

`PdfImage.Load` detects SVG files by their content; an SVG's natural size comes from its `width` and `height` or its
`viewBox` (300 x 150 pixels without either), and it keeps its aspect ratio per `preserveAspectRatio` when drawn into
another shape. The supported subset of SVG 1.1 covers what logos, icons and charts use: paths (all commands,
including arcs), basic shapes, groups, transforms, nested `svg`, `use` and `symbol`, fills and strokes (dashes, caps,
joins, fill rules), presentation attributes, `style` attributes and `<style>` sheets (type, class and id selectors),
`currentColor`, linear and radial gradients, clip paths, basic text (`text`/`tspan` with `text-anchor`, drawn in the
standard PDF fonts Helvetica, Times or Courier) and embedded `data:` images. `opacity`, `fill-opacity`,
`stroke-opacity` and translucent colors use PDF transparency; group opacity is a transparency group, so overlapping
shapes composite like in a browser (a gradient whose stops differ in opacity is approximated). Filters, masks, markers, patterns, gradient `spreadMethod` other than `pad`, text on a
path, animation, scripts and `foreignObject` are skipped; nothing external is loaded.

Embedded color profiles are ignored (images are treated as sRGB). BMPs with embedded JPEG or PNG data are not
supported.

### Text shaping

`RaiCore.Pdf.Layout` shapes text with [HarfBuzz](https://harfbuzz.github.io/): kerning, ligatures, Arabic joining,
Indic reordering and accent positioning, with per-character font fallback:

```csharp
using RaiCore.Pdf.Layout.Text;

var shaped = TextShaper.Shape("Total: سلام", [latinFont, arabicFont]);
page.Canvas.DrawText(shaped, 14, 72, 700);
double width = shaped.GetWidth(14); // includes kerning
```

`TextShaper` and the shaped-text types are experimental (diagnostic `RCPDF002`, see
[docs/experimental.md](docs/experimental.md)): they may still change after 1.0, so using them needs an opt-in.

Shaped runs are tagged so text extraction returns the logical text, even for Arabic. Paragraphs order mixed
left-to-right and right-to-left text with the Unicode bidi algorithm (UAX #9); `Paragraph.Direction` sets the base
direction, or detects it from the text.

### Paragraphs

Paragraphs break into lines with the [Unicode Line Breaking Algorithm](https://www.unicode.org/reports/tr14/)
(UAX #14, passing all 19,346 cases of the official conformance test) and lay out like CSS inline text:

```csharp
using RaiCore.Pdf.Layout;

var body = new TextStyle(fonts.Find("serif")!, 11) { LineHeight = LineHeight.Multiple(1.4) };
var layout = new Paragraph(body) { Alignment = TextAlignment.Justify, FirstLineIndent = 14 }
    .Add("Mixed ")
    .Add("styles", body with { Color = PdfColor.FromRgb(200, 0, 0) })
    .Add(" in one paragraph, wrapped to the width.")
    .Layout(width: 300);
layout.Draw(page.Canvas, x: 72, top: 770);   // or draw a range of lines per page
```

Supported: left/right/center/justify, `normal`/multiple/absolute line heights with the CSS line-box model,
first-line indent, hard line breaks, soft hyphens, automatic hyphenation and optional breaking of over-long words.

Hyphenation uses Liang's algorithm (as TeX does) with bundled patterns for a dozen languages; others can be loaded
from TeX pattern files and set on the style (for HTML, in `HtmlConverterOptions.Hyphenators`):

```csharp
var english = body with { Language = "en-US", Hyphens = Hyphens.Auto };
new Paragraph(english) { Alignment = TextAlignment.Justify }.Add("Justified text with hyphenation.").Layout(120);

var swedish = body with
{
    Hyphens = Hyphens.Auto,
    Hyphenator = Hyphenator.FromPatterns(File.ReadLines("hyph-sv.pat.txt"), minBefore: 2, minAfter: 2),
};
```

### Multi-page documents

Blocks stack like CSS block boxes and flow across pages:

```csharp
var flow = new FlowDocument(new PageSetup(PdfPageSize.A4, Thickness.All(56)));
flow.Add(new ParagraphBlock(new Paragraph(h1).Add("Report"), new BoxStyle { BreakAfter = PageBreak.Avoid }));
flow.Add(new ContainerBlock(
    new BoxStyle { Background = lightBlue, Border = Borders.All(1, blue), Padding = Thickness.All(8) },
    new ParagraphBlock(new Paragraph(body).Add("A note that may span pages."))));
flow.PageDecorator = (canvas, page) => /* header/footer, e.g. $"Page {page.Number} of {page.Count}" */;
flow.Render().Save("report.pdf");
```

- Box model: margin, border (solid, dashed, dotted, double, groove, ridge, inset or outset, with corners joined like
  browsers join them), rounded corners (`BoxStyle.BorderRadius`), padding, background and fixed widths.
- Transparency: colors with an alpha, and `BoxStyle.Opacity`, which draws a block with everything inside it as one
  transparency group per page.
- Vertical margins collapse between siblings and through parents without border or padding.
- Paragraphs split between lines with orphans and widows (default 2).
- Page breaks: forced (`break-before`/`break-after: page`), avoided (`break-after: avoid` keeps headings with their text,
  `break-inside: avoid`).
- Boxes split across pages are sliced: the top border on the first page, the bottom border on the last.
- Bookmarks: a block with `Bookmark = new Bookmark("Results", level: 2)` becomes an entry in the PDF outline, nested
  by level and pointing at where the block starts (also inside flex items and table cells).
- Links: `Paragraph.AddLink("the docs", LinkTarget.ToUri(uri))` (or `BeginLink`/`EndLink` around any content) makes
  text clickable on every line and page it covers; `Block.Link` makes a whole block clickable. `Block.Id` and
  `Paragraph.AddAnchor("id")` are destinations that `LinkTarget.ToDestination("id")` jumps to.
- Tables (`TableBlock`) use the CSS automatic or fixed table layout. Header rows repeat at the top of every page and
  footer rows (`TableRow.IsFooter`) below the body on every page, rows that do
  not fit are split with each cell continuing on the next page (unless `TableRow.BreakInside` is `Avoid`), rows that
  rowspans join may also break between them with the spanning cell continuing alongside, and borders can be separate
  or collapsed (conflicts resolved by width and style as in CSS 2.1).

HTML fragments go in such documents too: `HtmlFragment.Parse` (in `RaiCore.Pdf.Html`) converts HTML with optional
CSS into blocks for a `FlowDocument`, a `ContainerBlock` or a table cell, starting from a base `TextStyle`, e.g. rich
text from a database inside a code-built report. It renders as in a converted document; what only pages have
(`@page`, running elements, `string-set`, named pages) is left out and reported in `Diagnostics`:

```csharp
var notes = HtmlFragment.Parse(notesHtml, new HtmlFragmentOptions { BaseStyle = body, Width = flow.Setup.ContentWidth });
notes.AddTo(flow);                                  // Between code-built paragraphs.
var cell = new TableCell(HtmlFragment.Parse(cellHtml, new HtmlFragmentOptions { BaseStyle = body, Width = 200 }).Blocks);
```

### Thread safety

Fonts, images, hyphenation patterns and the converters are thread-safe and meant to be shared: create one
`FontCollection` at startup and pass it to every conversion, and run as many conversions in parallel as you like.
Parallel conversions are independent and give exactly the bytes of the same conversions run one at a time.

```csharp
var options = new HtmlConverterOptions { Fonts = BundledFonts.CreateCollection() }; // share fonts between threads
var pdfs = await Task.WhenAll(invoices.Select(i => Task.Run(() => HtmlToPdf.ConvertToBytes(Render(i), options))));
```

| Type | Guarantee |
| --- | --- |
| `PdfFont` | Immutable; one instance can be used by any number of documents and threads. It holds only the font data (bytes, parsed tables, character map, HarfBuzz face, each created once); the glyphs a document uses, its subset and its PDF objects are kept per document. |
| `FontCollection` | Thread-safe for adding and finding fonts. Each face is loaded once, on first use, and the same `PdfFont` is returned from then on; the faces of a `.ttc` share one copy of the file. |
| `BundledFonts` | The Liberation fonts are read and parsed once per process and shared by every collection, so a collection per document is cheap. |
| `PdfImage`, `Hyphenator`, `TextShaper` | Thread-safe; bundled hyphenation patterns load once per process. There is no global registry to change. |
| `HtmlToPdf`, `RazorToPdf`, `RazorPdfRenderer` | Thread-safe; each conversion has its own DOM, layout and document. `HtmlConverterOptions` can be shared (its `ResourceLoader`, and a renderer's services, are then called from several threads). Fonts from `@font-face` are loaded per conversion. |
| `PdfDocument`, `PdfPage`, `PdfCanvas`, `FlowDocument`, `Paragraph` | Not thread-safe: build and save each document on one thread at a time (different documents in parallel). |
| `PdfReader` | Not thread-safe, and documents importing its pages read it when they are saved: open a reader per thread. |

### Deterministic output

The same input gives the same bytes: an unencrypted document with the same content, options and dates is written
identically every time, page by page or at once, on any thread, in any culture or time zone. That makes golden-file
tests possible. The `/ID` is a hash of the content, the XMP has no random identifiers, and objects and resources are
written in a fixed order. Dates are the only input taken from outside: set `PdfDocument.TimeProvider` (or
`HtmlConverterOptions.TimeProvider`) to a fixed clock, or set `Info.CreationDate`. Encrypted documents differ on
every save, since their keys and salts are random by design. Flate compression comes from the .NET runtime's zlib;
CI checks that Linux, Windows and macOS write the same bytes.

```csharp
var options = new HtmlConverterOptions { TimeProvider = new FixedClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) };
byte[] pdf = HtmlToPdf.ConvertToBytes(html, options); // The same bytes every time.

sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
```

## Performance

A one-page invoice converts in about 25 ms and a 100-page report in about 0.3 s on an Apple M2; the
[benchmarks](benchmarks/README.md) have the numbers and how to run them. Long documents can be written page by page,
so the drawn content of finished pages is not kept in memory:

```csharp
using var file = File.Create("report.pdf");
var document = new PdfDocument();
document.BeginSave(file);
flow.RenderTo(document);           // Each page is written as soon as it is drawn; or call document.FlushPages().
document.EndSave();                // Fonts, links, outline and metadata, which need all pages.

HtmlToPdf.ConvertToStream(html, file); // The same for HTML.
```

`HtmlToPdf.ConvertToStreamAsync` (and `RazorToPdf.ConvertToStreamAsync`) write the stream only asynchronously, so it
can be an ASP.NET Core response body; while the stream is slow, the conversion waits.

## Architecture

```
 DOCX / ODT / RTF / XLSX / PPTX ─┐
 HTML + CSS / Razor             ─┼─► Document model ─► Layout engine ─► PDF writer
 XML (XSL-FO)                   ─┘
```

Every input format is mapped to one shared document model and laid out by one engine, so improvements to fonts, tables and pagination benefit all formats.

## Roadmap

1.0 is the first release: a stable, production-ready HTML/Razor to PDF library with its PDF writer and layout engine.
The way there and what comes next are tracked in [GitHub milestones](https://github.com/raicevic/raicore-pdf/milestones):

1. **Done:** the PDF writer, fonts, images, text shaping, line breaking, block layout and pagination, HTML/CSS with
   tables, flexbox and CSS Paged Media, Razor, and packaging with a release pipeline.
2. **0.2 Template fidelity:** web fonts, `inline-block`, `::before`/`::after`, links and bookmarks, text decoration,
   border styles, SVG.
3. **0.3 Layout and painting:** floats, positioning, transparency, backgrounds, rounded corners, `overflow`, `calc()`,
   bidi, hyphenation, counters, named pages.
4. **0.4 PDF standards:** PDF/A-3 and e-invoices, tagged PDF/PDF/UA, metadata, encryption, a PDF reader for merging.
5. **0.5 Hardening:** visual comparison with browsers, fuzzing, resource limits, performance, deployment targets.
6. **Done, 1.0:** API review and freeze, documentation, the release.
7. **1.x Reading, editing and conversions:** text extraction, text search, image extraction and page editing (done), forms,
   PDF/A level A, TIFF, email, CSV, XLSX, PPTX, DOCX, RTF and Office formats to PDF.
8. **After 1.0:** XSL-FO, grid layout, multi-column layout, digital signatures.

The 0.x milestones are internal stages, not releases.

JavaScript is not executed, by design: the target is print documents such as invoices and reports.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet build
dotnet test
```

Install [qpdf](https://github.com/qpdf/qpdf) and poppler's `pdftotext` to run the PDF structure and text
extraction tests locally, and a `verapdf` command for the PDF/A and PDF/UA checks; otherwise they are skipped (CI
always runs them). See [CONTRIBUTING.md](CONTRIBUTING.md) for the package smoke test, fuzzing and the release process,
and [tools/README.md](tools/README.md) for the maintainer scripts.

## Support and security

[SUPPORT.md](SUPPORT.md) says where to ask, which versions and .NET releases are supported and how often releases
come. Report vulnerabilities privately, as [SECURITY.md](SECURITY.md) describes. Everyone taking part follows the
[code of conduct](CODE_OF_CONDUCT.md).

## License

[Apache License 2.0](LICENSE). All shipped dependencies must use permissive licenses (MIT, BSD or Apache-2.0).
The Liberation fonts in `RaiCore.Pdf.Fonts` are under the [SIL Open Font License 1.1](src/RaiCore.Pdf.Fonts/Liberation/OFL.txt).
Third-party data and fonts the packages and tests include (Unicode data, hyphenation patterns, fonts, test images)
and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

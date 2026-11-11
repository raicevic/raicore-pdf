# RaiCore.Pdf

An open-source .NET library for creating PDF documents and converting HTML, Razor and office documents to PDF, built on its own layout engine with no headless browser.

> **Status:** in development toward 1.0, the first release. HTML and Razor conversion work, but nothing is published
> to NuGet yet and the API may change at any time. See the [roadmap](#roadmap).
> See the [changelog](CHANGELOG.md) for what is supported.

## Packages

| Package | Purpose | Status |
|---|---|---|
| `RaiCore.Pdf.Html` | HTML + CSS (including CSS Paged Media) → PDF | 🟡 Toward 1.0 |
| `RaiCore.Pdf.Razor` | Razor components → PDF | 🟡 Toward 1.0 |
| `RaiCore.Pdf.Layout` | Format-neutral layout engine: text shaping, line breaking, pagination, tables, flexbox | 🟡 Toward 1.0 |
| `RaiCore.Pdf.Fonts` | Bundled Liberation fonts (metric-compatible with Arial, Times New Roman, Courier New) | 🟡 Toward 1.0 |
| `RaiCore.Pdf` | PDF object model, writer, fonts, images and drawing API | 🟡 Toward 1.0 |
| `RaiCore.Pdf.Docx`, `.Xlsx`, `.Pptx`, `.XslFo` | Office and XML formats → PDF | ⚪ After 1.0 |

All packages target .NET 10 and run on Windows, Linux and macOS (x64 and Arm64) with no native dependencies beyond
HarfBuzz, which ships in the packages.

## Installation

The packages will be on NuGet from 1.0. Then you install the package for your input format, and the others come as
dependencies:

```sh
dotnet add package RaiCore.Pdf.Html     # HTML and CSS
dotnet add package RaiCore.Pdf.Razor    # Razor components (includes RaiCore.Pdf.Html)
```

Symbols and SourceLink will be published too, so you can step into the library code while debugging. Until 1.0,
reference the projects from a clone of this repository, or build packages locally with `dotnet pack`.

## Quick start

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
- The box model: margin, border (every `border-style`: solid, dashed, dotted, double, groove, ridge, inset and
  outset), `border-radius` (elliptical and percentage radii, every border style, rounded images), padding, background
  and `background-clip`, width, `min-width`, `max-width`, `height`, `min-height`, `max-height`, auto margins,
  `box-sizing` and percentages. `min-width`/`max-width` also limit shrink-to-fit boxes and scale images (with
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
- List markers: bullets, decimal, alpha and roman numerals, `start`, `reversed` and `value`.
- Images from `data:` and `file:` URLs (JPEG, PNG, GIF, BMP, WebP and SVG), inline `<svg>` elements drawn as vectors (sized
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

- Margin boxes: `top-`/`bottom-` `left`, `center`, `right` and corners, with font, color, alignment and
  vertical-align.
- Page selectors: `:first`, `:left` and `:right`.
- Content values: `counter(page)` and `counter(pages)` in any counter style, `string()` with
  `first`/`start`/`last`/`first-except`, `element()` for running elements, and strings with CSS escapes.

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

Network access is off unless you pass a `ResourceLoader`. `HtmlToPdf.CreateFlowDocument` returns the laid-out document
before rendering, e.g. to set your own page decorator.

Not yet: floats and positioning, grid layout (rendered as blocks),
different page margins for `:first`/`:left`/`:right`, named pages and counters other than
`page`/`pages`. Content taller than a fixed `height` overflows
the box (`overflow: hidden` is not supported yet).

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

Fonts with CFF outlines are embedded whole for now; CFF subsetting comes later. Fonts whose license forbids
embedding are rejected.

### Images

JPEG, PNG, GIF, BMP, WebP and SVG images can be drawn directly or placed in the layout. JPEGs are embedded without
re-encoding (EXIF orientation is applied); PNGs of every color type and bit depth, including interlaced and
transparent ones, are decoded and verified against the full [PngSuite](http://www.schaik.com/pngsuite/). BMPs of
every common kind (palettes, RLE, 16/24/32-bit, bit fields, alpha) and WebP, lossy and lossless with alpha, are
decoded in managed code; lossy WebP matches libwebp bit for bit, and an animated WebP shows its first frame. SVG is
drawn as PDF vector graphics, not rasterized, so logos and icons stay sharp at any zoom. Each image is stored once per document however
often it is drawn.

```csharp
var logo = PdfImage.Load("logo.png");
page.Canvas.DrawImage(logo, x: 400, y: 760, width: 120, height: 40);

flow.Add(new ImageBlock(logo, width: 150) { Alignment = TextAlignment.Right });          // a block image
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
from TeX pattern files:

```csharp
var english = body with { Language = "en-US", Hyphens = Hyphens.Auto };
new Paragraph(english) { Alignment = TextAlignment.Justify }.Add("Justified text with hyphenation.").Layout(120);

Hyphenator.Register("sv", Hyphenator.FromPatterns(File.ReadLines("hyph-sv.pat.txt"), minBefore: 2, minAfter: 2));
```

### Multi-page documents

Blocks stack like CSS block boxes and flow across pages:

```csharp
using RaiCore.Pdf.Layout.Blocks;

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

## Architecture

```
 DOCX / XLSX / PPTX ─┐
 HTML + CSS / Razor ─┼─► Document model ─► Layout engine ─► PDF writer
 XML (XSL-FO)       ─┘
```

Every input format is mapped to one shared document model and laid out by one engine, so improvements to fonts, tables and pagination benefit all formats.

## Roadmap

1.0 is the first release: a stable, production-ready HTML/Razor to PDF library with its PDF writer and layout engine.
Nothing is published before it. The way there is tracked in
[GitHub milestones](https://github.com/raicevic/raicore-pdf/milestones):

1. **Done:** the PDF writer, fonts, images, text shaping, line breaking, block layout and pagination, HTML/CSS with
   tables, flexbox and CSS Paged Media, Razor, and packaging with a release pipeline.
2. **0.2 Template fidelity:** web fonts, `inline-block`, `::before`/`::after`, links and bookmarks, text decoration,
   border styles, SVG.
3. **0.3 Layout and painting:** floats, positioning, transparency, backgrounds, rounded corners, `overflow`, `calc()`,
   bidi, hyphenation, counters, named pages.
4. **0.4 PDF standards:** PDF/A-3 and e-invoices, tagged PDF/PDF/UA, metadata, encryption, a PDF reader for merging.
5. **0.5 Hardening:** visual comparison with browsers, fuzzing, resource limits, performance, deployment targets.
6. **1.0:** API review and freeze, documentation, then the release.
7. **After 1.0:** DOCX, XLSX, PPTX and XSL-FO as their own packages, PDF editing, grid layout.

The 0.x milestones are internal stages, not releases.

JavaScript is not executed, by design: the target is print documents such as invoices and reports.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet build
dotnet test
```

Install [qpdf](https://github.com/qpdf/qpdf) and poppler's `pdftotext` to run the PDF structure and text
extraction tests locally; otherwise they are skipped (CI always runs them). See [CONTRIBUTING.md](CONTRIBUTING.md)
for the package smoke test and the release process, and [tools/README.md](tools/README.md) for the maintainer
scripts.

## License

[Apache License 2.0](LICENSE). All shipped dependencies must use permissive licenses (MIT, BSD or Apache-2.0).
The Liberation fonts in `RaiCore.Pdf.Fonts` are under the [SIL Open Font License 1.1](src/RaiCore.Pdf.Fonts/Liberation/OFL.txt).

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
- The box model: margin, border (every `border-style`: solid, dashed, dotted, double, groove, ridge, inset and
  outset), padding, background, width, max-width, `height`, `min-height`, `max-height`, auto margins, `box-sizing` and
  percentages. Percentage heights resolve against definite ancestor heights, starting with the page area (so
  `html, body { height: 100% }` and `100vh` fill the page).
- Fonts (`font-family` fallback lists, weight, style), color, `text-align`, `text-indent`, `line-height` and `text-transform`.
- Web fonts: `@font-face` with `url()` (TTF, OTF, TTC, WOFF, WOFF2, also as `data:` URIs) and `local()` sources,
  `format()` hints, `font-weight` ranges for variable fonts, `font-style` and `unicode-range` (so per-script files as
  Google Fonts serves them work as one family). Fonts load through the `ResourceLoader` when text first needs them;
  URLs in linked stylesheets resolve against the stylesheet.
- List markers: bullets, decimal, alpha and roman numerals, `start`, `reversed` and `value`.
- Images from `data:` and `file:` URLs, linked stylesheets, `@page` size and margins, and page break properties.
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

Network access is off unless you pass a `ResourceLoader`. `HtmlToPdf.CreateFlowDocument` returns the laid-out document
before rendering, e.g. to set your own page decorator.

Not yet: floats and positioning, grid layout (rendered as blocks),
`text-decoration`, different page margins for `:first`/`:left`/`:right`, named pages, counters other than
`page`/`pages`, and `calc()`. Content taller than a fixed `height` overflows
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

JPEG, PNG and GIF images can be drawn directly or placed in the layout. JPEGs are embedded without re-encoding
(EXIF orientation is applied); PNGs of every color type and bit depth, including interlaced and transparent ones,
are decoded and verified against the full [PngSuite](http://www.schaik.com/pngsuite/). Each image is stored once per
document however often it is drawn.

```csharp
var logo = PdfImage.Load("logo.png");
page.Canvas.DrawImage(logo, x: 400, y: 760, width: 120, height: 40);

flow.Add(new ImageBlock(logo, width: 150) { Alignment = TextAlignment.Right });          // a block image
new Paragraph(body).Add("Status: ").AddImage(checkIcon, 10, 10).Add(" paid");            // an inline image
new Paragraph(body).Add("Total ").AddBox(badgeBlock).Add(" due");                        // an inline box (any block)
```

Embedded color profiles are ignored (images are treated as sRGB). WebP, SVG and BMP are not supported yet.

### Text shaping

`RaiCore.Pdf.Layout` shapes text with [HarfBuzz](https://harfbuzz.github.io/): kerning, ligatures, Arabic joining,
Indic reordering and accent positioning, with per-character font fallback:

```csharp
using RaiCore.Pdf.Layout.Text;

var shaped = TextShaper.Shape("Total: سلام", [latinFont, arabicFont]);
page.Canvas.DrawText(shaped, 14, 72, 700);
double width = shaped.GetWidth(14); // includes kerning
```

Shaped runs are tagged so text extraction returns the logical text, even for Arabic. Mixed left-to-right and
right-to-left text on one line needs the Unicode bidi algorithm, which is planned.

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
first-line indent, hard line breaks, soft hyphens and optional breaking of over-long words.

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
  browsers join them), padding, background and fixed widths.
- Vertical margins collapse between siblings and through parents without border or padding.
- Paragraphs split between lines with orphans and widows (default 2).
- Page breaks: forced (`break-before`/`break-after: page`), avoided (`break-after: avoid` keeps headings with their text,
  `break-inside: avoid`).
- Boxes split across pages are sliced: the top border on the first page, the bottom border on the last.
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

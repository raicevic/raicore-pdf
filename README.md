# RaiCore.Pdf

An open-source .NET library for creating PDF documents and converting HTML, Razor and office documents to PDF, built on its own layout engine with no headless browser.

> **Status:** early development (0.1.0-alpha). The PDF writer works; HTML/Razor conversion is in progress. APIs will change.

## Packages

| Package | Purpose | Status |
|---|---|---|
| `RaiCore.Pdf` | PDF object model, writer, fonts and drawing API | 🟡 In progress |
| `RaiCore.Pdf.Fonts` | Bundled Liberation fonts (metric-compatible with Arial, Times New Roman, Courier New) | 🟢 Usable |
| `RaiCore.Pdf.Layout` | Format-neutral layout engine: text shaping, line breaking, pagination, tables | 🟡 In progress |
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

- Box model: margin, border, padding, background and fixed widths.
- Vertical margins collapse between siblings and through parents without border or padding.
- Paragraphs split between lines with orphans and widows (default 2).
- Page breaks: forced (`break-before`/`break-after: page`), avoided (`break-after: avoid` keeps headings with their text,
  `break-inside: avoid`).
- Boxes split across pages are sliced: the top border on the first page, the bottom border on the last.

## Architecture

```
 DOCX / XLSX / PPTX ─┐
 HTML + CSS / Razor ─┼─► Document model ─► Layout engine ─► PDF writer
 XML (XSL-FO)       ─┘
```

Every input format is mapped to one shared document model and laid out by one engine, so improvements to fonts, tables and pagination benefit all formats.

## Roadmap

1. **PDF writer:** objects, compression, standard fonts ✅, embedded TrueType/OpenType/WOFF fonts with subsetting ✅, JPEG/PNG/GIF images ✅, then links and bookmarks.
2. **Layout engine:** HarfBuzz text shaping and font fallback ✅, line breaking (UAX #14) and paragraphs ✅, block layout and pagination ✅.
3. **HTML/CSS core:** parsing (AngleSharp), the cascade and mapping HTML to the layout engine.
4. **0.1 release:** tables, `@page`, page breaks, page numbers and the Razor adapter.
5. **0.2:** flexbox, inline SVG, margin boxes, running headers, tagged PDF and PDF/A.
6. **Later:** DOCX, PDF editing, XSL-FO, XLSX and PPTX.

JavaScript is not executed, by design: the target is print documents such as invoices and reports.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet build
dotnet test
```

Install [qpdf](https://github.com/qpdf/qpdf) and poppler's `pdftotext` to run the PDF structure and text
extraction tests locally; otherwise they are skipped (CI always runs them). See [tools/README.md](tools/README.md)
for the font cross-checks against fontTools.

## License

[Apache License 2.0](LICENSE). All shipped dependencies must use permissive licenses (MIT, BSD or Apache-2.0).
The Liberation fonts in `RaiCore.Pdf.Fonts` are under the [SIL Open Font License 1.1](src/RaiCore.Pdf.Fonts/Liberation/OFL.txt).

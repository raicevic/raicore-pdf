# RaiCore.Pdf

Create PDF documents from .NET: the PDF object model, writer, embedded fonts, images and a drawing API. This is the
core of RaiCore.Pdf. Most applications use it through
[RaiCore.Pdf.Html](https://www.nuget.org/packages/RaiCore.Pdf.Html) or
[RaiCore.Pdf.Razor](https://www.nuget.org/packages/RaiCore.Pdf.Razor).

```sh
dotnet add package RaiCore.Pdf
```

```csharp
using RaiCore.Pdf;

var doc = new PdfDocument();
var page = doc.AddPage(PdfPageSize.A4);
var font = PdfFont.Load("NotoSans-Regular.ttf");   // TrueType, OpenType, TTC, WOFF, WOFF2

page.Canvas
    .SetFillColor(PdfColor.FromRgb(220, 30, 30))
    .Rectangle(72, 600, 200, 100)
    .Fill()
    .SetFillColor(PdfColor.Black)
    .DrawText("Hello from RaiCore.Pdf", font, 24, 72, page.Height - 80)
    .DrawImage(PdfImage.Load("logo.png"), x: 400, y: 760, width: 120, height: 40);

doc.Save("hello.pdf");
```

- Fonts are subset to the glyphs used and stay searchable and copyable in every script.
- JPEG (embedded as is), PNG (every color type, transparency), GIF, BMP, WebP (lossy and lossless, with alpha) and
  SVG (drawn as vector graphics) images, each stored once per document.
- Transparency: colors with an alpha (`new PdfColor(r, g, b, alpha)`) and transparency groups
  (`Canvas.BeginGroup(opacity)`/`EndGroup()`) that composite overlapping drawing as a whole.
- Bookmarks: `PdfDocument.Outline` holds nested `PdfOutlineItem`s with Unicode titles.
- Links: `PdfPage.AddLink` makes a rectangle open a URI or jump to a place in the document, also through named
  destinations (`PdfDocument.NamedDestinations`).
- Metadata: `PdfDocument.Info` (title, author, subject, keywords, dates and custom properties) is written to the
  Info dictionary and to an XMP metadata stream kept in sync with it; `PdfDocument.Language` sets the document
  language (`/Lang`).
- Encryption: `PdfDocument.Encryption = new PdfEncryption { UserPassword = ..., OwnerPassword = ..., Permissions = ... }`
  encrypts with AES-256 (or AES-128 for old readers) and sets print, copy and edit permissions.
- Existing PDFs: `PdfReader.Open` reads a file; `PdfDocument.ImportPages`/`InsertPages` merge its pages into a
  document (fonts and images shared, links and bookmarks kept), and `Canvas.DrawPage` draws a page under or over new
  content, e.g. a letterhead.
- Coordinates are PDF points (1/72 inch) with the origin at the bottom-left of the page.

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)

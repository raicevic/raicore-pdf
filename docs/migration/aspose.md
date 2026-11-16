# From Aspose.HTML and Aspose.PDF

Aspose.HTML converts HTML with its own rendering engine; Aspose.PDF can also load HTML (`HtmlLoadOptions`). Both are
commercial. RaiCore.Pdf is open source (Apache-2.0); like Aspose.HTML it has its own engine and no browser.

## Side by side

Aspose.HTML:

```csharp
using var document = new HTMLDocument(html, @"C:\app\templates\");
var options = new PdfSaveOptions();
options.PageSetup.AnyPage = new Page(new Size(Length.FromMillimeters(210), Length.FromMillimeters(297)));
Converter.ConvertHTML(document, options, "invoice.pdf");
```

Aspose.PDF:

```csharp
var load = new HtmlLoadOptions(@"C:\app\templates\");
load.PageInfo.Width = 595;   // points
load.PageInfo.Height = 842;
using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
using var document = new Document(stream, load);
document.Save("invoice.pdf");
```

RaiCore.Pdf:

```csharp
using RaiCore.Pdf.Html;

var options = new HtmlConverterOptions
{
    BaseUri = new Uri(@"C:\app\templates\"),
    UserStyleSheet = "@page { size: A4; margin: 20mm; }",
};
HtmlToPdf.Convert(html, options).Save("invoice.pdf");
```

## Options

| Aspose | RaiCore.Pdf |
|---|---|
| `PdfSaveOptions.PageSetup.AnyPage` (Aspose.HTML) | `@page { size: ...; margin: ... }` |
| `PageSetup.FirstPage`, `LeftPage`, `RightPage` | `@page :first`, `@page :left`, `@page :right` |
| `HtmlLoadOptions.PageInfo` (Aspose.PDF) | `@page { size: ...; margin: ... }` |
| `PageSetup.AdjustToWidestPage`, `IsRenderToSinglePage` | no equivalent: content is broken into pages |
| `PdfSaveOptions.BackgroundColor` | `html { background: ... }` (the page area) |
| `PdfSaveOptions.Encryption` | `HtmlConverterOptions.Encryption` |
| `PdfSaveOptions.DocumentInfo` | `HtmlConverterOptions.Title`, `Author`, `Subject`, `Keywords` |
| PDF/A (`Document.Convert(..., PdfFormat.PDF_A_2B, ...)`) | `HtmlConverterOptions.PdfAConformance` |
| Base path or `Configuration` with a network service | `BaseUri` and a `ResourceLoader` |
| `FontsSettings` / `UserStyleSheet` in `Configuration` | `HtmlConverterOptions.Fonts` and `UserStyleSheet` |
| Merging documents | `PdfDocument.ImportPages` |

## Headers, footers and page numbers

Both Aspose libraries follow CSS Paged Media in part, so `@page` rules may already be in your stylesheets. RaiCore.Pdf
supports the margin boxes (`@top-left` to `@bottom-right` and corners), `counter(page)` and `counter(pages)`,
`string-set`/`string()`, running elements (`position: running()` with `content: element()`), page selectors
(`:first`, `:left`, `:right`, `:blank`) and named pages. Headers added in code (Aspose.PDF's `HeaderFooter` with
`TextFragment`s and `$p`/`$P`) become margin boxes:

```css
@page { @bottom-right { content: "Page " counter(page) " of " counter(pages); font-size: 8pt; } }
```

## Fonts

Aspose looks for installed fonts and folders you configure. RaiCore.Pdf uses `@font-face` and its bundled Liberation
fonts by default; add font files with `new FontCollection().AddFile(...)` (or `AddDirectory`, or `AddSystemFonts()`)
and pass the collection as `HtmlConverterOptions.Fonts`, plus `AddBundledFonts()` for the substitutes of Arial and
Times New Roman.

## Differences to expect

- Aspose.HTML can run JavaScript; RaiCore.Pdf does not. See the [known differences](README.md#known-differences).
- Aspose's other conversions (DOCX, XLSX and so on) are not part of RaiCore.Pdf 1.0; they are planned as separate
  packages after it.

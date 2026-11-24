# From Aspose

Aspose sells a library per format: Aspose.HTML and Aspose.PDF, Aspose.Words, Aspose.Cells, Aspose.Slides and
Aspose.Email. RaiCore.Pdf is open source (Apache-2.0) and covers the conversion to PDF and the PDF work in a package per
job, all built on one layout engine (no browser, no Office):

| Aspose | RaiCore.Pdf package | Main classes |
|---|---|---|
| Aspose.HTML, Aspose.PDF `HtmlLoadOptions` | `RaiCore.Pdf.Html` (and `RaiCore.Pdf.Razor`) | `HtmlToPdf`, `RazorToPdf` |
| Aspose.PDF: creating documents | `RaiCore.Pdf`, `RaiCore.Pdf.Layout` | `PdfDocument`, `PdfCanvas`, the layout API |
| Aspose.PDF: text and images out of PDFs | `RaiCore.Pdf.Extraction` | `TextExtractor`, `ImageExtractor` |
| Aspose.PDF: changing existing PDFs | `RaiCore.Pdf.Editing` | `PdfEditor` |
| Aspose.Words: to PDF | `RaiCore.Pdf.Documents` | `DocxToPdf`, `DocToPdf`, `RtfToPdf`, `OdtToPdf` |
| Aspose.Cells: to PDF | `RaiCore.Pdf.Sheets` | `XlsxToPdf`, `XlsToPdf`, `CsvToPdf` |
| Aspose.Slides: to PDF | `RaiCore.Pdf.Slides` | `PptxToPdf`, `PptToPdf` |
| Aspose.Email: to PDF | `RaiCore.Pdf.Email` | `EmailToPdf` (`.eml`, `.msg`, `.mht`) |

What RaiCore.Pdf does not do: edit Word documents or presentations, or save to formats other than PDF (workbooks are the exception: `Workbook` edits `.xlsx` files and saves them as `.xlsx` or delimited text). The
[format support](../guides/format-support.md) page lists what each converter draws, and
[untrusted input](../guides/untrusted-input.md) what each one may load.

## HTML to PDF

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

### Options

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

### Headers, footers and page numbers

Both Aspose libraries follow CSS Paged Media in part, so `@page` rules may already be in your stylesheets. RaiCore.Pdf
supports the margin boxes (`@top-left` to `@bottom-right` and corners), `counter(page)` and `counter(pages)`,
`string-set`/`string()`, running elements (`position: running()` with `content: element()`), page selectors
(`:first`, `:left`, `:right`, `:blank`) and named pages. Headers added in code (Aspose.PDF's `HeaderFooter` with
`TextFragment`s and `$p`/`$P`) become margin boxes:

```css
@page { @bottom-right { content: "Page " counter(page) " of " counter(pages); font-size: 8pt; } }
```

### Fonts

Aspose looks for installed fonts and folders you configure. RaiCore.Pdf uses `@font-face` and its bundled Liberation
fonts by default; add font files with `new FontCollection().AddFile(...)` (or `AddDirectory`, or `AddSystemFonts()`)
and pass the collection as `HtmlConverterOptions.Fonts`, plus `AddBundledFonts()` for the substitutes of Arial and
Times New Roman.

## PDF documents (Aspose.PDF)

Aspose.PDF numbers pages from 1; RaiCore.Pdf from 0.

| Aspose.PDF | RaiCore.Pdf |
|---|---|
| `new Document(path)` to read | `PdfReader.Open(path)` |
| `new Document(path)` to change | `PdfEditor.Open(path)` ([which one?](../guides/editing.md#pdfdocument-or-pdfeditor)) |
| `TextAbsorber`, `Pages.Accept(absorber)`, `absorber.Text` | `new TextExtractor(reader).ExtractText()`, `ExtractPage(i).Text` |
| `TextExtractionOptions` (`Pure`, `Raw`) | `TextExtractionOptions.Mode` (`Reading`, `Layout`, `Raw`) |
| `TextFragmentAbsorber("phrase")`, `TextFragment.Rectangle` | `TextExtractor.Find("phrase")`, `TextMatch.Rectangles`, `PageIndex` |
| `TextFragmentAbsorber` with `TextSearchOptions(isRegularExpressionUsed: true)` | `TextExtractor.Find(regex)` |
| `ImagePlacementAbsorber`, `ImagePlacement.Rectangle`, `Image.Save` | `ImageExtractor.ExtractPage(i)`, `ExtractedImage.Bounds`, `GetData()`, `FileExtension` |
| `Form.Flatten()`, `Document.Flatten()` | `PdfEditor.FlattenForm()` |
| `Annotation.Flatten()`, `Page.Flatten()` | `PdfEditor.FlattenAnnotations(options)` |
| `Document.Form.Fields` | `PdfEditor.GetForm().Fields` |
| `Form.HasXfa`, `Form.Type` | `PdfEditor.GetForm().Xfa` (`None`, `Static`, `Dynamic`), `IsXfaPlaceholder` |
| `Form.Type = FormType.Standard` (XFA to AcroForm) | `PdfEditor.RemoveXfa()`: keeps the AcroForm saved with the file, without laying the XFA out again |
| `OptimizeResources()`, `Optimize()` | `PdfEditor.Optimize(options)` |
| `Pages.Delete(n)` | `PdfEditor.Pages.Delete(n - 1)` |
| `Pages.Insert(n, page)`, `Pages.Add(other.Pages)` | `PdfEditor.Pages.Insert(index, reader, pageIndexes)`, `Pages.Add(reader)` |
| `Page.Rotate = Rotation.on90` | `PdfEditor.Pages[i].Rotation = 90` |
| `PdfFileEditor.Concatenate` | `PdfEditor.Open(first)`, then `Pages.Add(reader)` for the others |
| `PdfFileEditor.SplitToPages`, split by size | `PdfDocument.ImportPages(reader, [i])` per page; `PdfEditor.SplitBySize(bytes)` |
| `PdfFileStamp`, `PdfPageStamp` | `PdfCanvas.DrawPage(reader.Pages[i], ...)` |
| `Document.Encrypt(...)` | `PdfDocument.Encryption` (new documents) |
| `Document.Convert(..., PdfFormat.PDF_A_2B, ...)` on an existing file | none: PDF/A is for documents RaiCore.Pdf writes (`PdfAConformance`) |
| Filling form fields, signing | none yet (signing: PAdES is planned) |

## Word documents (Aspose.Words)

```csharp
// Aspose.Words
new Document("report.docx").Save("report.pdf");

// RaiCore.Pdf
DocxToPdf.Convert("report.docx").Save("report.pdf");
```

| Aspose.Words | RaiCore.Pdf |
|---|---|
| `new Document(path).Save(pdf)` for `.docx`, `.doc`, `.rtf`, `.odt` | `DocxToPdf`, `DocToPdf`, `RtfToPdf`, `OdtToPdf` (`DocxToPdf` and `DocToPdf` take each other's format too) |
| `PdfSaveOptions.Compliance` (PDF/A) | `PdfAConformance` on the returned `PdfDocument`, before `Save` |
| `PdfSaveOptions.ExportDocumentStructure` | `DocumentConverterOptions.Tagged` |
| `PdfSaveOptions.OutlineOptions.HeadingsOutlineLevels` | `HeadingBookmarks` |
| `FontSettings.SetFontsFolder` | `Fonts = new FontCollection().AddDirectory(...)`, `FallbackFontFamilies` |
| `Document.WarningCallback` | `Diagnostics` |
| `Document.GetText()`, `ToString(SaveFormat.Text)` | `DocumentText.Extract(path)` (`DocumentTextOptions` for headers and footers, notes, comments, field codes) |
| `LoadOptions.Password` | none: encrypted documents throw `NotSupportedException` |
| `DocumentBuilder`, mail merge, editing | none: generate documents from HTML or Razor templates instead |

## Spreadsheets (Aspose.Cells)

```csharp
// Aspose.Cells
new Workbook("figures.xlsx").Save("figures.pdf", SaveFormat.Pdf);

// RaiCore.Pdf
XlsxToPdf.Convert("figures.xlsx").Save("figures.pdf");
```

| Aspose.Cells | RaiCore.Pdf |
|---|---|
| `Workbook.Save(pdf)` for `.xlsx`, `.xls`, `.csv` | `XlsxToPdf`, `XlsToPdf`, `CsvToPdf` |
| The worksheets' `PageSetup` | used by default (`UsePageSetup`): print area, print titles, orientation, scale, fit to width |
| `PdfSaveOptions.AllColumnsInOnePagePerSheet` | `SheetOptions.FitToWidth` |
| `PdfSaveOptions.SheetSet` | `SheetNames`, `SheetIndexes` |
| `TxtLoadOptions.Separator`, `Encoding` | `CsvConverterOptions.Delimiter`, `Encoding` |
| `PdfSaveOptions.Compliance` | `PdfAConformance` on the returned `PdfDocument` |
| `new Workbook(path)`, `Cells["A1"].PutValue`, `Cells.Find`, `InsertRows`, `DeleteRows`, `Row.IsHidden`, `Save(SaveFormat.Csv)` | `Workbook.Open(path)`, `worksheet["A1"].SetValue`, `Workbook.Find`, `InsertRows`, `DeleteRows`, `Row(n).IsHidden`, `SaveAsCsv` ([workbooks](../guides/workbooks.md)) |
| Charts, pictures, conditional formatting, comments, headers and footers in the PDF | planned ([#258](https://github.com/raicevic/raicore-pdf/issues/258), [#257](https://github.com/raicevic/raicore-pdf/issues/257), [#259](https://github.com/raicevic/raicore-pdf/issues/259)) |

## Presentations (Aspose.Slides)

| Aspose.Slides | RaiCore.Pdf |
|---|---|
| `new Presentation(path).Save(pdf, SaveFormat.Pdf)` for `.pptx`, `.ppt` | `PptxToPdf`, `PptToPdf` |
| `PdfOptions.ShowHiddenSlides` | `PptxConverterOptions.IncludeHiddenSlides` |
| `PdfOptions.Compliance` | `PdfAConformance` on the returned `PdfDocument` |
| `PdfOptions` with notes or comments | none: notes pages and comments are not converted |
| `IWarningCallback` | `Diagnostics` |

## Email (Aspose.Email)

Aspose.Email turns a message into MHTML, which Aspose.Words then converts; RaiCore.Pdf does it in one step.

| Aspose.Email | RaiCore.Pdf |
|---|---|
| `MailMessage.Load(eml)`, `MapiMessage.Load(msg)`, then MHTML and Aspose.Words | `EmailToPdf.Convert(path)` for `.eml`, `.msg`, `.mht` |
| `MhtSaveOptions`: which header fields, their labels | `HeaderFields`, `HeaderLabels` |
| Attachments | `EmbedAttachments`, `ListAttachments`, `AppendAttachments` |
| Keeping the original | `EmbedOriginal` (as PDF/A-3's source file) |
| `MailMessage` properties in code | none: no public message model |

## Differences to expect

- Aspose.HTML can run JavaScript; RaiCore.Pdf does not. See the [known differences](README.md#known-differences).
- RaiCore.Pdf lays out Office files with the fonts you give it, and with metric-compatible substitutes (such as
  Liberation for Arial and Times New Roman) where it has them. Add the documents' own fonts (Calibri, Aptos, ...) for
  the same line breaks as Office.
- Spreadsheet formulas are not recalculated: the PDF shows the values saved in the workbook.

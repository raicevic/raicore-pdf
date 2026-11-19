# RaiCore.Pdf.Documents

Converts Word documents (`.docx`) to PDF with [RaiCore.Pdf](https://www.nuget.org/packages/RaiCore.Pdf)'s layout engine:
styles, numbered and bulleted lists, tables, sections with their page setup, headers and footers with page numbers,
images, hyperlinks and notes. No word processor, no browser, no native dependencies.

```csharp
using RaiCore.Pdf.Documents;

var pdf = DocxToPdf.Convert("letter.docx");
pdf.Save("letter.pdf");
```

- **Styles and formatting:** paragraph, character and table styles with their inheritance, document defaults and theme
  fonts; alignment, indents, spacing, line spacing, borders, shading, keep with next and page breaks; fonts, sizes,
  bold, italic, underline, strikethrough, colors, highlighting, superscript, subscript and capitals.
- **Lists and tabs:** multi-level numbering (`1.`, `1.1.`, `a)`, `iv.`), bullets, restarts; tab stops with leaders.
- **Tables:** grid widths, merged cells, borders, shading, padding, header rows repeated on every page, nested tables,
  table styles with their first row, last row and banded rows.
- **Pages:** sections with their own size, orientation and margins; headers and footers for first, even and odd pages
  with page numbers (`PAGE`, `NUMPAGES`, `SECTIONPAGES`); page references of a table of contents with the PDF's page
  numbers.
- **More:** PNG, JPEG, GIF, BMP, TIFF and WebP images (inline, or floating with text wrapping around them), hyperlinks
  and bookmarks, footnotes and endnotes, tracked changes as if accepted, content controls, headings as PDF bookmarks,
  tagged (accessible) PDF.
- **Untrusted input:** no DTDs, nothing external; decompressed bytes, XML elements, nesting, table cells, pages and time
  are bounded by `ResourceLimits`. Malformed packages throw `PdfFormatException`; encrypted and `.doc` files
  `NotSupportedException`.

Not supported: text boxes and shapes as floating frames (their text follows their paragraph), charts, SmartArt,
embedded objects, EMF and WMF images, comments, several text columns, footnotes at the bottom of the page. What is left
out is reported to `DocxConverterOptions.Diagnostics`.

More: the [Word documents guide](https://raicevic.github.io/raicore-pdf/guides/documents.html).

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)

# RaiCore.Pdf.Documents

Converts Word documents (`.docx`), Word 97-2003 documents (`.doc`), OpenDocument text documents (`.odt`, `.ott`,
flat `.fodt`) and Rich Text Format documents (`.rtf`) to PDF with [RaiCore.Pdf](https://www.nuget.org/packages/RaiCore.Pdf)'s layout engine: styles,
numbered and bulleted lists, tables, sections with their page setup, headers and footers with page numbers, images,
hyperlinks and notes. No word processor, no browser, no native dependencies.

```csharp
using RaiCore.Pdf.Documents;

var pdf = DocxToPdf.Convert("letter.docx");
pdf.Save("letter.pdf");
OdtToPdf.Convert("report.odt").Save("report.pdf");
RtfToPdf.Convert("memo.rtf").Save("memo.pdf");
DocToPdf.Convert("minutes.doc").Save("minutes.pdf");
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
- **OpenDocument text:** common, automatic and default styles with their parent chains; lists with list styles
  (numbers, letters, roman numerals, bullets, continued lists) and outline numbering of headings; tables with column
  widths, spans, header rows and repeated rows; master pages with page layouts, headers and footers for left, right and
  first pages, switched by paragraph styles; images in the package or embedded, the replacement images of charts;
  notes, links, bookmarks, page numbers and page references; tracked changes as if accepted.
- **RTF:** RTF 1.9.1 with its code pages (`\ansicpg`, `\fcharset`, double-byte ones too) and Unicode escapes, the
  font, color and style tables, lists (`\listtable`, old-style `\pn`), tables (merged and nested), sections, headers
  and footers, fields (links, page numbers, page references), PNG and JPEG pictures, bookmarks and notes; groups,
  tokens, nesting, table cells and pictures bounded by `ResourceLimits`, and data that is not RTF throws
  `PdfFormatException`.
- **Word 97-2003:** the binary `.doc` format ([MS-DOC]) of Word 97 to 2003: the piece table (8-bit and UTF-16 text,
  fast-saved documents), the style sheet and font table, character, paragraph, table and section properties, lists
  with their overrides, tables (merged and nested), headers and footers, fields (links, page numbers, page
  references), inline and floating PNG, JPEG, TIFF and bitmap pictures, text boxes, bookmarks and notes; every offset
  checked against its stream, and paragraphs, nesting, table cells and pictures bounded by `ResourceLimits`.
  Encrypted documents and Word 6/95 files throw `NotSupportedException`.
- **Untrusted input:** no DTDs, nothing external; decompressed bytes, XML elements (and repeated table rows and cells),
  nesting, table cells, pages and time are bounded by `ResourceLimits`. Malformed documents throw
  `PdfFormatException`; encrypted documents `NotSupportedException` (and `DocxToPdf` given a `.doc` file names
  `DocToPdf`).

Not supported: text boxes and shapes as floating frames (their text follows their paragraph), charts (an OpenDocument
chart's replacement image is shown), SmartArt, embedded objects, EMF and WMF images, comments, forms, several text
columns, footnotes at the bottom of the page; in RTF and `.doc` also PICT pictures and drawing objects. What is left
out is reported to the options' `Diagnostics`.

More: the [Word documents guide](https://raicevic.github.io/raicore-pdf/guides/documents.html) and its
[RTF](https://raicevic.github.io/raicore-pdf/guides/documents.html#rtf-documents) and
[Word 97-2003](https://raicevic.github.io/raicore-pdf/guides/documents.html#word-97-2003-documents) sections.

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)

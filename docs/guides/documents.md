# Word documents to PDF

`RaiCore.Pdf.Documents` converts Word documents (`.docx`), Word 97-2003 documents (`.doc`, see
[below](#word-97-2003-documents)), OpenDocument text documents (`.odt`, see [below](#opendocument-text)) and RTF
documents (`.rtf`, see [below](#rtf-documents)) to PDF with the layout engine: styles, numbered and bulleted lists,
tables, sections with their page setup, headers and footers with page numbers, images, hyperlinks and notes. No word
processor, no browser, no native dependencies. The package is the home of the word-processing formats: the converters
share its document model and options.

```sh
dotnet add package RaiCore.Pdf.Documents
```

[!code-csharp[](../snippets/WordDocuments.cs#convert)]

`DocxToPdf.Convert` takes a path, a stream or bytes; `ConvertAsync` (on the thread pool, with a cancellation token)
and `ConvertToBytes` do the same. `.docx`, `.docm` (its macros are ignored), `.dotx` and strict Office Open XML
documents are read.

The [format support](format-support.md) page compares all formats side by side.

## What is converted

- **Styles:** paragraph, character and table styles with their `basedOn` chains, the document defaults and the theme's
  fonts and colors. A paragraph's format comes from the defaults, its table's style, its style, its list level and its
  own properties, in that order, as in Word.
- **Paragraphs:** alignment (justified too), indents (first line and hanging), space before and after (added together,
  and left out between paragraphs of the same style with contextual spacing), line spacing (multiple, exact, at
  least), borders (adjacent paragraphs with the same borders share a box) and shading, keep with next, keep lines
  together, page break before, widow and orphan control, right-to-left paragraphs.
- **Text:** fonts, sizes, bold, italic, underline (single, double, dotted, dashed, wavy), strikethrough, colors,
  highlighting and shading, superscript and subscript, raised and lowered text, letter spacing, all capitals and small
  capitals; hidden text is left out.
- **Lists:** multi-level numbering with labels built from the level texts (`%1.%2.`), decimal, letters, roman
  numerals, ordinals and bullets (the private-use bullets of Symbol and Wingdings drawn as Unicode bullets), start
  values, restarts and lists that continue across other paragraphs; the label hangs in the indent, followed by a tab,
  a space or nothing.
- **Tabs:** left, center, right and decimal stops with dot, hyphen and underscore leaders, the default stops and the
  hanging indent's stop. Tabs are measured on the line, so they are exact on lines that do not wrap before them; a
  right tab with a leader at the right margin (a table of contents) always fills the line.
- **Tables:** the grid's column widths (scaled down when wider than the page), cells merged across and down, borders
  (the cell's own, else the table's outer and inside borders), shading, cell padding, vertical alignment, row heights,
  rows that may not break across pages, header rows repeated on every page, nested tables, and table styles with their
  conditional formats (first and last row and column, banded rows and columns).
- **Sections:** page size, orientation and margins (the top and bottom margin grow when a header or footer needs the
  room), and how the section starts: on a new page, on the same page (continuous, when the page setup stays the same),
  or on the next even or odd page.
- **Headers and footers:** for the first page of a section (with a title page), even pages (with different odd and
  even headers) and the other pages, inherited from the section before when a section has none; with page numbers
  (`PAGE`, `NUMPAGES`, `SECTIONPAGES`), restarted and formatted per section (decimal, roman, letters).
- **Fields:** page references (`PAGEREF`, as a table of contents has them) show the PDF's page numbers: the document
  is laid out twice when it has them. `HYPERLINK` fields are links; other fields (dates, document properties, cross
  references) show the result the document was saved with.
- **Images:** PNG, JPEG, GIF, BMP, TIFF and WebP, inline in the text or anchored. Anchored images with text wrapping
  (square, tight, through) float at the left or right margin with the text around them; other anchored images stand
  on a line of their own before their paragraph. Images are scaled down to the column and the page.
- **Links and bookmarks:** hyperlinks to web addresses (http, https, mailto, ftp, tel; other schemes are shown as text
  without a link) and to bookmarks, which become the PDF's named destinations.
- **Notes:** footnotes after the section that refers to them, endnotes at the end of the document, each after a short
  rule with its number, linked from its mark.
- **Revisions and controls:** tracked changes as if accepted (insertions kept, deletions left out), content controls
  and custom XML unwrapped. Equations are shown as their plain text.
- **Outline and tagging:** headings (paragraphs with an outline level, such as Heading 1 to 9) become bookmarks of the
  PDF, nested by level. With `Tagged`, the PDF has headings by level, paragraphs, tables with their header rows and
  images with their alternative text; headers and footers are artifacts.

## Options

[!code-csharp[](../snippets/WordDocuments.cs#options)]

| `DocxConverterOptions` | What it does |
|---|---|
| `Fonts`, `FontFamily`, `FallbackFontFamilies` | The fonts: a collection (default the bundled Liberation fonts), the family for text whose font the collection lacks (default `sans-serif`), and fallbacks for characters a font lacks; Liberation Sans comes last. |
| `HeadingBookmarks` | Headings as the PDF's bookmarks (default `true`). |
| `Title` | The document's title; by default the title of the document's properties. |
| `Tagged` | A tagged (accessible) PDF (default `false`). |
| `Diagnostics` | Receives what could not be shown as the document does, below. |
| `Limits` | The `ResourceLimits` of the conversion, below. |

Page size, margins and orientation come from the document's sections. The returned `PdfDocument` can be changed
before it is saved: metadata, `Language` (set from the document's default language in a tagged PDF), `PdfUA`,
`PdfAConformance`, encryption, attachments.

## Fonts

A run's font is found by its family in `Fonts`; when the collection lacks it, Calibri and Cambria look for their
metric-compatible substitutes Carlito and Caladea, fonts known to be serif or monospace (Times New Roman, Cambria,
Georgia, Courier New, Consolas) use the bundled Liberation Serif or Mono, and other fonts `FontFamily`. Liberation
Sans, Serif and Mono have the widths of Arial, Times New Roman and Courier New, so text in those fonts breaks into
lines as in Word. Documents in Calibri or Aptos (Word's defaults) break differently with the bundled fonts: add the
system's fonts, or Carlito, for Word's line breaks.

[!code-csharp[](../snippets/WordDocuments.cs#fonts)]

## Diagnostics

`Diagnostics` receives a `DocumentDiagnostic` (a kind and a message) for each distinct issue, once per conversion:

- `UnsupportedContent`: charts, SmartArt, embedded objects (shown as their preview image when it can be drawn),
  shapes and text boxes (their text is laid out after their paragraph), several text columns (laid out as one),
  embedded document parts (`altChunk`), equations (shown as text).
- `ImageNotLoaded`: images in formats that cannot be drawn (EMF, WMF), damaged, missing or linked images; they are
  left out.
- `Layout`: content wider than its box or taller than a page.

## Untrusted documents

Documents are ZIP packages of XML parts. They are read with no DTDs and nothing external resolved (a document with a
DTD throws `DocumentFormatException`), and within `Limits`: `MaxDecompressedBytes` bounds the decompressed size of all parts
together (a ZIP bomb stops there), `MaxElements` the XML elements of all parts together (counted while they are read),
`MaxNestingDepth` the nesting of XML elements and of tables, `MaxTableCells` the cells of each table, `MaxImagePixels`
each image, and `MaxPages` and `Timeout` the pages and the time. Exceeding one throws `ResourceLimitExceededException`
naming it.

Nothing outside the document is loaded, in any of the formats: linked images (external relationships, ODT links
outside the package, linked pictures in `.doc` and RTF) are left out and reported as `ImageNotLoaded`,
`INCLUDEPICTURE`, `INCLUDETEXT` and `LINK` fields show the result stored with them, and `altChunk` parts are left out
and reported. The converters take no resource loader. See [Untrusted input](untrusted-input.md) for all converters.

Malformed packages (not a ZIP file, no main document part, XML that is not well-formed) throw `DocumentFormatException`.
Password-protected (encrypted) documents, which are Compound Files, throw `NotSupportedException`. A Word 97-2003
`.doc` file (also a Compound File) is converted as one, as [`DocToPdf`](#word-97-2003-documents) does; `DocToPdf` in
turn converts a `.docx` package given to it.

## OpenDocument text

`OdtToPdf` converts OpenDocument text documents, as LibreOffice and other office suites write them: `.odt` packages,
`.ott` templates and flat `.fodt` XML documents, ODF 1.0 to 1.3. It has the same overloads as `DocxToPdf` and takes an
`OdtConverterOptions`, which has the options above.

[!code-csharp[](../snippets/WordDocuments.cs#odt)]

- **Styles:** common styles, the automatic styles of the content and of the page styles, default styles, parent style
  chains and font face declarations. Paragraph properties: margins, indents, alignment, line height (proportional,
  fixed, at least, and leading), borders, background, keep with next, keep together, page breaks before and after,
  widows and orphans, and tab stops with leaders (relative to the paragraph's indent, unless the document's settings
  say otherwise). Text properties: fonts, sizes (also relative), bold, italic, underline styles, strikethrough,
  colors, backgrounds, superscript, subscript and raised text, small capitals, upper case, lower case and capitalized
  text, letter spacing and hidden text.
- **Headings and lists:** headings have their outline level (and become bookmarks) and the outline style's numbering.
  Lists take their list style's levels: numbers (`1`, `a`, `A`, `i`, `I`) with prefix, suffix, start value and the
  higher levels they display (`1.2.3`), bullets (symbol-font bullets drawn in the paragraph's font) and picture bullets
  (as round bullets); nested lists, list headers without a label, lists that continue an earlier one
  (`text:continue-numbering`, `text:continue-list`), items that restart the numbering, and both ways of positioning
  labels (label alignment with a tab stop, and the older label width and distance).
- **Tables:** column widths (absolute, or relative and shared out of the table's width), table width and alignment,
  header rows repeated on every page, cells spanning columns and rows, borders, padding, backgrounds (of the table,
  row and cell), vertical alignment, row heights and rows kept together, nested tables, and repeated rows, columns and
  cells.
- **Page styles:** each master page used gives a section with its page layout (size, orientation, margins, the height
  and spacing of its header and footer) and its headers and footers: for left pages, for the first page (ODF 1.3's
  `style:header-first`, or a first page style followed by another), and the others, unless hidden. A paragraph or
  table style with a master page starts a new page with it, and may restart the page numbers; page number formats come
  from the page layout.
- **Content:** sections (hidden ones left out), tables of contents and other indexes as their content, links to web
  addresses and bookmarks, bookmarks, footnotes and endnotes (with their own citations, numbered as the notes
  configuration says), page numbers, page counts and page references computed from the PDF's pages, other fields
  (dates, titles, chapters, sequences) as their text, tracked changes as if accepted, comments left out.
- **Frames:** images in the package (PNG, JPEG, GIF, BMP, TIFF, WebP, SVG) or embedded as base64 in flat documents;
  of several alternatives the first that can be drawn. Frames anchored as characters are in the line; frames anchored
  to the paragraph with text wrapping float at the left or right; others stand on a line of their own. Charts and
  other embedded objects are shown as their replacement image (when it can be drawn: LibreOffice often stores it as a
  StarView metafile, which cannot); the text of text boxes and shapes follows their paragraph.

Not supported in OpenDocument text: drawing shapes (their text is kept), several columns of a page style or section,
forms and their controls, scripts (ignored), comments, line numbering and page backgrounds; each is reported to
`Diagnostics`. Password-protected (encrypted) documents throw `NotSupportedException`; malformed packages and XML that
is not well-formed `DocumentFormatException`. Linked images (outside the package) are left out. The `Limits` apply as to
Word documents, the XML of flat documents included; table rows and cells that repeat others
(`table:number-rows-repeated`, `table:number-columns-repeated`) count against `MaxElements`, and their cells against
`MaxTableCells`.

## RTF documents

`RtfToPdf` converts Rich Text Format documents (`.rtf`, as Word, WordPad, LibreOffice and Outlook save them) with the
same renderer, so they are laid out like Word documents. `RtfConverterOptions` has the options above; `Convert`,
`ConvertToBytes` and `ConvertAsync` take a path, a stream or bytes.

[!code-csharp[](../snippets/WordDocuments.cs#rtf)]

What is converted (RTF 1.9.1):

- **Text and code pages:** text in the document's code page (`\ansicpg`, `\mac`, `\pc`) and its fonts' (`\fcharset`,
  `\cpg`), double-byte code pages such as Shift-JIS and GBK included; Unicode escapes (`\u` with the `\uc` fallback
  skipped, surrogate pairs) and `\upr`/`\ud`; text in Symbol, Wingdings and Webdings as the Unicode characters it shows.
- **Tables of fonts, colors and styles:** paragraph and character styles with their `\sbasedon` chains (headings by
  their names, `heading 1` to `heading 9`, or `\outlinelevel`), the document defaults (`\defchp`, `\defpap`) that
  `\plain` and `\pard` reset to.
- **Formatting:** what the [Word documents](#what-is-converted) list says for text and paragraphs, from RTF's control
  words: fonts, sizes, bold, italic, underline styles, strikethrough, colors, highlighting and shading, superscript and
  subscript, raised and lowered text, letter spacing, capitals, small capitals, hidden and deleted text left out;
  alignment, indents, spacing, line spacing (`\sl` with `\slmult`), keep together, keep with next, page break before,
  widow control, borders and shading; tab stops with their alignment and leaders.
- **Lists:** the list table and its overrides (`\ls`, `\ilvl`) with labels counted as Word counts them (start values,
  restarts, legal numbering, bullets), old-style paragraph numbering (`\pn`), and the writer's own label (`\listtext`,
  `\pntext`) where no list is defined.
- **Tables:** the grid from the cell edges of all rows (`\trleft`, `\cellx`), merged cells (`\clmgf`/`\clmrg`,
  `\clvmgf`/`\clvmrg`), row and cell borders, shading, padding, vertical alignment, row heights, header rows repeated
  on every page (`\trhdr`), rows that may not break (`\trkeep`) and nested tables (`\itap`, `\nestcell`,
  `\nestrow`).
- **Sections, headers and footers:** page size, orientation and margins of the document and of each section, how a
  section starts (`\sbknone`, `\sbkpage`, `\sbkeven`, `\sbkodd`), headers and footers for the first page
  (`\titlepg`), left and right pages (`\facingp`), inherited from the section before; page numbers (`\chpgn` and
  `PAGE` fields) restarted and formatted per section.
- **Fields:** `HYPERLINK` fields are links (to web addresses and bookmarks), `PAGE`, `NUMPAGES`, `SECTIONPAGES` and
  `PAGEREF` are computed, other fields show their last result.
- **Pictures:** PNG and JPEG (`\pngblip`, `\jpegblip`) and device-independent bitmaps, in hexadecimal or `\bin`
  data, at their goal size and scale; the shape picture (`\shppict`) rather than its fallback; a shape's picture and
  text, laid out in the text; an embedded object's result.
- **Bookmarks and notes:** bookmarks (`\bkmkstart`) as named destinations, footnotes (`\footnote`, numbered from
  `\chftn`) and endnotes (`\ftnalt`); the title and author of the document's information (`\info`) as the PDF's.

Not supported, and reported to `Diagnostics`: EMF, WMF and PICT pictures (`ImageNotLoaded` for pictures that cannot be
decoded or are larger than `MaxResourceBytes`), drawing objects (`\do`), shapes as drawings, embedded objects (their
result is shown), comments (annotations), several text columns. Also not supported: frames and positioned paragraphs
(laid out in the text), footnotes at the bottom of the page, page borders, right-to-left and vertical text beyond what
the layout engine does by itself. Revisions are shown as if accepted.

RTF is read in one pass without recursion, within `Limits`: `MaxElements` bounds the tokens (control words, groups and
runs of text), `MaxNestingDepth` the nesting of groups and of tables, `MaxTableCells` the cells of a table,
`MaxResourceBytes` each picture's data (a larger picture is left out) and `MaxImagePixels` its pixels, and `MaxPages`
and `Timeout` the pages and the time. Data that does not start with `{\rtf` throws `DocumentFormatException`.

## Word 97-2003 documents

`DocToPdf` converts Word 97-2003 documents (`.doc`, the binary format of Word 97 to Word 2003, which later versions
still save) with the same renderer, so they are laid out like Word documents. `DocConverterOptions` has the options
above; `Convert`, `ConvertToBytes` and `ConvertAsync` take a path, a stream or bytes.

[!code-csharp[](../snippets/WordDocuments.cs#doc)]

What is converted ([MS-DOC]):

- **Text:** the piece table's text, in 8-bit (code page 1252) and UTF-16 pieces, and that of fast-saved documents
  (their pieces' property modifiers included); the main text, headers and footers, footnotes, endnotes and text boxes;
  Symbol and Wingdings text, and symbols inserted as such (`sprmCSymbol`), as the Unicode characters they show.
- **Styles and formatting:** the style sheet's paragraph and character styles with their bases (headings by their
  outline level or built-in style), the font table, and each paragraph's and run's own property modifiers on top:
  fonts, sizes, bold, italic, underline styles, strikethrough, colors (palette and RGB), highlighting and shading,
  superscript and subscript, raised and lowered text, letter spacing, capitals, small capitals, hidden text and deleted
  revisions left out; alignment, indents, spacing, line spacing, keep together, keep with next, page break before,
  widow control, borders and shading; tab stops with their alignment and leaders.
- **Lists:** the list definitions and their overrides (`PlfLst`, `PlfLfo`) with labels counted as Word counts them
  (start values and overrides of them, restarts after a higher level, skipped levels, legal numbering, bullets in
  symbol fonts) and the levels' indents and label formatting.
- **Tables:** cells and rows from the table marks, nested tables, the grid from the cell edges of all rows
  (`sprmTDefTable`), merged cells both ways, table and cell borders, shading, padding, vertical alignment, row heights,
  header rows repeated on every page and rows that may not break.
- **Sections, headers and footers:** each section's page size, orientation, margins and start, headers and footers for
  the first page, even and odd pages (inherited from the section before), page numbers restarted and formatted per
  section. A page that is narrower or lower than its margins (as some writers save) gets the size of A4 or Letter
  paper.
- **Fields:** `HYPERLINK` fields are links (to web addresses and bookmarks), `PAGE`, `NUMPAGES`, `SECTIONPAGES` and
  `PAGEREF` are computed, other fields show their last result (field instructions are never shown).
- **Pictures:** inline pictures (in the Data stream) and floating ones (the drawing layer's pictures) in PNG, JPEG,
  TIFF and bitmap formats, at their size and scale less their cropping; an embedded object's preview picture.
- **More:** bookmarks as named destinations, footnotes and endnotes with their numbers, the title and author of the
  summary information as the PDF's.

Not supported, and reported to `Diagnostics`: EMF, WMF and PICT pictures (`ImageNotLoaded`, as are pictures larger
than `MaxResourceBytes` and damaged ones), drawing shapes other than pictures and text boxes, comments, several text
columns; text boxes are laid out after their paragraph. Also not supported: frames and positioned paragraphs, form
fields' boxes, footnotes at the bottom of the page, page borders, right-to-left and vertical text beyond what the
layout engine does by itself. Revisions are shown as if accepted.

Password-protected (encrypted) documents and documents saved by Word 6, Word 95 or earlier throw
`NotSupportedException`; data that is not a Word 97-2003 document (a compound file with a WordDocument stream, its
file information block, table stream and piece table) throws `DocumentFormatException` (naming `RtfToPdf` for an RTF
document), except a `.docx` package, which is converted as `DocxToPdf` converts it. Every offset and size in the file is checked against the stream it
points into, and reading is bounded by `Limits`: `MaxElements` the compound file's entries and the paragraphs and runs,
`MaxDecompressedBytes` each stream, `MaxNestingDepth` the nesting of tables, `MaxTableCells` the cells of a table,
`MaxResourceBytes` each picture (a larger picture is left out) and `MaxImagePixels` its pixels, and `MaxPages` and
`Timeout` the pages and the time.

## Not supported

Floating text boxes and shapes as frames (their text is laid out after their paragraph), charts, SmartArt, embedded
objects, EMF and WMF images, comments, several text columns, footnotes at the bottom of their page (they follow their
section), line numbers, page borders and backgrounds, watermarks, drop caps, vertical and right-to-left text beyond what
the layout engine does by itself, hyphenation settings, and fields that would need calculating (their saved result is
shown).

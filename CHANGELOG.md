# Changelog

All notable changes to RaiCore.Pdf are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **Embedded OLE objects in Outlook messages** (#293): attachments that Outlook keeps as OLE objects become the file
  inside them: a packaged file under its own name, an Office 2007+ document, a file kept whole (such as a PDF), or an
  Office 97-2003 document written out as its `.doc`, `.xls` or `.ppt` file. Else they become the object's picture
  (`.wmf`, `.emf`, `.bmp`). Objects with nothing to take out are reported to `Html.Diagnostics` instead of being
  dropped silently.
- **Sheets: chart sheets** (#290): the chart sheets of `.xlsx` workbooks are printed in the workbook's sheet order,
  each on a page of its own with its chart stretched over the content area, in landscape unless its page setup asks
  for portrait, with its headers and footers. `SheetNames`, `SheetIndexes` and the bookmarks include them. Chart
  sheets without anything that can be drawn, and those of `.xls` workbooks (BIFF charts), are still reported.
- **Charts: waterfall, treemap, sunburst, histogram, box and whisker and funnel charts** (#291): the chart types of
  Office 2016 (`chartex` parts) are drawn as vector graphics in workbooks, Word documents and presentations by the
  shared chart engine: waterfalls (running totals, totals, connector lines), histograms (Excel's binning, with
  underflow and overflow bins) and paretos, box and whisker plots (exclusive or inclusive quartiles, means, outliers),
  funnels, treemaps and sunbursts, with their titles, legends, data labels and theme colors. Word documents and
  presentations draw them instead of their stored pictures; workbooks read their data from the cells their hidden
  names refer to (and charts without cached data from their cells). Region maps are still shown as their stored
  picture or reported.

### Fixed
- **Large tables and spreadsheets render in linear time** (#250): drawing and splitting table rows looked up their
  cells by scanning every cell of the table for each row group placed, so a table's time grew with the square of its
  rows. A 16,000-row CSV file took 69 s and now takes about 11 s; 5,000 rows by 108 columns went from 27 s to 17 s.
  This helps CSV, XLSX and XLS sheets and long HTML tables alike. A new `Sheet10000Rows` benchmark tracks it.

## [1.2.0] - 2026-11-24

Closing migration gaps: dynamic XFA forms, charts, pictures and print formatting in spreadsheets, WMF and EMF
pictures, a workbook API and direct text of Word documents.

### Highlights
- **Dynamic XFA forms** (`RaiCore.Pdf.Xfa`, new): XFA forms laid out from their template and data as flattened PDF
  pages; `PdfEditor.RemoveXfa()` keeps the AcroForm instead, and `FlattenForm` can do either.
- **Charts** in workbooks, Word documents and presentations, drawn as vector graphics by one shared chart engine.
- **Spreadsheets:** pictures, shapes and charts; headers and footers, page breaks, conditional formatting (colour
  scales, data bars, icon sets, formula rules), comments, rotated and rich text, pivot styles; diagnostics for what is
  left out; and a `Workbook` API to open, edit and save `.xlsx` files and delimited text.
- **WMF and EMF pictures** (with EMF+) drawn as vector graphics in every Office converter.
- **Text of Word documents** (`DocumentText.Extract`) straight from DOCX, DOC, RTF and ODT, without a PDF.
- **Notes** (`PdfPage.AddNote`), **`ResourceLoaders.None`** for untrusted HTML, and an untrusted input guide.

### Added
- **Sheets: headers and footers, page breaks, rich text and conditional formatting** (#259): worksheets print their
  headers and footers (left, center and right sections, page number with offsets, pages, date, time, sheet name, font
  codes; first-page and even-page texts; their margins; the first page number), page breaks set by hand between rows
  (header rows repeated after them) and columns, rich text runs, stacked text and text shrunk to fit, from `.xlsx` and
  `.xls` alike. Conditional formatting of `.xlsx` workbooks is applied over the stored values: value comparisons,
  color scales, top/bottom, above/below average, duplicate/unique, text, blank and error rules, with their font, fill
  and border formats. `XlsxConverterOptions.PrintDate` sets the date and time headers print. Diagnostics no longer
  report what is printed now.
- **Sheets: comments, rotated text, data bars, icon sets, formula rules and pivot table styles** (#259): worksheets
  print their comments as their page setup asks (listed at the end of the sheet), or by the new
  `XlsxConverterOptions.Comments` at the end, as PDF notes at their cells, or not at all; rotated text, turned within
  its cell (rows grow to fit it); and the default pivot table styles (`PivotStyleLight15` to `21`). Conditional
  formatting adds data bars (also Excel 2010's, with negative values), icon sets (all of Excel's, and custom ones),
  formula rules and `cellIs` rules with formulas (computed over the stored values: references, operators and common
  functions, bounded) and date periods, and works the same for `.xls` workbooks (`CF`, `CF12` and `CFEX` records),
  whose rich text is shown now too. Diagnostics no longer report comments, rich text, rotated text, or the rules and
  pivot tables that print now.
- **Notes (text annotations)**: `PdfPage.AddNote` and `PdfCanvas.AddNote` add a "sticky note" with a text and an
  author, printed with an appearance of its own (as PDF/A requires) and tagged as an `Annot` element in tagged
  documents.
- **Removing XFA forms** (#262, `RaiCore.Pdf.Editing`): `PdfEditor.RemoveXfa()` deletes the XFA form and keeps the
  AcroForm with its fields, values and appearances, so files with static or dynamic XFA can be filled, flattened and
  shown by any viewer. `PdfForm.IsXfaPlaceholder` tells when a dynamic XFA form's PDF pages are only a "Please
  wait..." placeholder; `RemoveXfa` then refuses unless forced. `FlattenForm(PdfFormFlattenOptions)` can remove a
  dynamic XFA form first (`PdfDynamicXfaHandling.RemoveXfa`) instead of throwing. The editing guide recommends how to
  handle XFA files. `PdfDynamicXfaHandling.Render` with `PdfFormFlattenOptions.XfaRenderer` (for example
  `XfaToPdf.Convert` from `RaiCore.Pdf.Xfa`) replaces a dynamic form's pages with the rendered form instead.
- **Charts** in presentations and Word documents: one chart engine for DrawingML charts, drawn as vector graphics
  from the data cached in the file, with text that can be extracted. Columns and bars (clustered, stacked, 100%,
  horizontal), lines (markers, smoothed), pies (exploded) and doughnuts, areas, scatter, bubble, radar and stock
  charts, combinations with secondary axes; Excel's automatic axis scaling, number formats, logarithmic and reversed
  axes, gridlines, titles, legends, data labels, markers, manual layouts, theme colors and the chart style's automatic
  colors. 3-D charts are drawn flat. Newer chart types (waterfall, treemap, sunburst...) in Word documents show their
  stored picture, as they already did in presentations; what can't be drawn is reported to `Diagnostics`.
- **`ResourceLoaders.None`** (`RaiCore.Pdf.Html`): a resource loader that loads nothing, so untrusted HTML can embed
  images as `data:` URIs (which never reach a loader) but can't reach local files or the network. Use it with
  `HtmlConverterOptions`, `HtmlFragmentOptions` and `EmailConverterOptions.Html`.
- **Untrusted input guide**: what each converter may load besides its input (nothing, for the Office, spreadsheet,
  presentation and email converters), and the limits that bound its work. Tests prove that no kind of external
  reference (linked images, `INCLUDEPICTURE` fields, external workbook links, data connections, linked pictures) is
  followed, and that the Office and email packages contain no file, network or process access of their own.
- **Text of word-processing documents** (#261): `DocumentText.Extract` (and `ExtractAsync`) in
  `RaiCore.Pdf.Documents` gets the plain text of a DOCX, DOC, RTF or ODT document directly, the format detected from
  the data, without converting it to PDF: about 80 times faster than converting and extracting the PDF's text.
  `DocumentTextOptions` set the paragraph separator, list labels, table cell and row separators, and whether headers
  and footers, footnotes and endnotes, comments, text boxes, hidden text and deleted text of tracked changes are
  included, and fields as their codes or results. The same `Limits` and exceptions as the converters.
- **Workbook API** (#260, `RaiCore.Pdf.Sheets`): `Workbook.Open` opens an Excel workbook (`.xlsx`, `.xlsm`, strict
  Office Open XML), such as a template, and `Workbook.Create` a new one, to read typed cell values (text, numbers,
  booleans, dates by their number formats in either date system, errors, formulas with their cached values) by
  address or row and column, write text, numbers, booleans, dates and formulas (keeping the cell's style), set number
  formats, find cells (contains, whole cell, case, regular expressions; displayed values or formulas), hide rows,
  columns and sheets and group rows and columns. `InsertRows`, `DeleteRows`, `InsertColumns` and `DeleteColumns` move
  every reference as Excel does: formulas on every sheet (shared and array formulas too), defined names with print
  areas and titles, merged cells, conditional formatting, data validation, hyperlinks, the autofilter, tables, page
  breaks, drawing anchors, comments, chart series and pivot sources; deleted references become `#REF!`. `Save` writes
  only the parts that change and every other part byte for byte (a workbook saved unchanged is the same file), drops
  the calculation chain and asks Excel to recalculate when cells change. `SaveAsCsv` writes a sheet as delimited text
  with any separator, UTF-8 (with or without a byte order mark), UTF-16 or Windows-1252, RFC 4180 quoting, a line
  ending, hidden rows and columns or not, and values as displayed in a culture or as stored. Excel 97-2003 workbooks
  and CSV files open read only. `XlsxToPdf.Convert(Workbook)` converts a workbook with its changes. Opening is bounded
  by `WorkbookOptions.Limits`; a new fuzz target, `workbook`, opens, edits and saves.
- **WMF and EMF pictures** (#266), with EMF+ records: drawn as PDF vector graphics wherever the Office converters
  used to leave them out (`ImageNotLoaded`): pictures in DOCX, DOC (also old metafile pictures), RTF and ODT
  documents and in PPTX and PPT presentations, and the previews of embedded objects. Text stays text, in the
  converter's fonts at the picture's character positions; mapping modes, world transforms, clipping, paths, pens,
  hatched and bitmap brushes, bitmaps, regions, gradients and the common EMF+ records are supported, and a dual-mode
  file falls back to its EMF records when it uses EMF+ features that are not. A damaged picture is drawn up to the
  damage and reported; records and points count against `MaxElements`, bitmaps against `MaxImagePixels`.
  `PdfImage.Load` reads WMF and EMF too (new `PdfImageFormat.Wmf` and `PdfImageFormat.Emf`). Fuzz targets `wmf` and
  `emf`.
- **Sheets: diagnostics** (#256): `SheetOptions.Diagnostics` receives a `SheetDiagnostic` (sheet name,
  `SheetDiagnosticKind`, message with the count) for each kind of content a sheet has that the conversion leaves out
  or simplifies, once per kind and sheet: charts and chart sheets, pictures, shapes and form controls, embedded objects
  and ActiveX controls, comments, conditional formatting rules, sparklines, pivot tables, headers and footers, page
  breaks set by hand, and cells with rich, rotated or shrunk text, in `.xlsx` and `.xls` alike; and content that does
  not fit the page (`Layout`, with its page numbers, also for CSV). A workbook with none of these reports nothing.
- **Sheets: pictures, shapes and charts** (#257): the drawings of `.xlsx` and `.xls` worksheets are drawn over their
  cells where Excel shows them. Pictures (PNG, JPEG, GIF, BMP, TIFF, WMF, EMF) cropped, turned, mirrored and clipped
  to their shape, with their border; shapes and text boxes with their preset or free-form geometry, fill, outline and
  text; charts by the shared chart engine; groups. Two-cell, one-cell and absolute anchors are resolved against the
  laid-out columns and rows at the sheet's scale, with hidden rows and columns taking no room; a drawing that crosses
  a page break or a band of columns is split there, as Excel prints it, and without a print area the printed range
  takes in the drawings. In `.xls` workbooks, the OfficeArt drawing: pictures from the picture store (also DIB),
  the common shape types with their fill, line and text box text, and groups. Pictures over `MaxImagePixels`, and
  what still can't be drawn (newer chart types, SmartArt, ink, form controls, BIFF charts, free forms in `.xls`), are
  reported to `SheetOptions.Diagnostics`. The preset and free-form geometry that presentations draw moved to the
  core, shared by both packages.
- **Dynamic XFA forms to PDF** (#263): the new package `RaiCore.Pdf.Xfa` renders XFA forms (from LiveCycle and AEM
  Forms Designer), whose PDF pages are often only a "Please wait..." placeholder, to ordinary flattened PDF pages.
  `XfaToPdf.Convert`, `ConvertToBytes` and `ConvertAsync` take a PDF file with an XFA form (its packets as one stream
  or an array) or an XDP file. The form's data (or `XfaConverterOptions.Data`) is merged into the template with
  normal, global, data reference and no bindings and repeated subforms; subforms are laid out by position and in flows
  (`tb`, `lr-tb`, `rl-tb`, tables with column widths and spans), growing to fit their content, and paginated into the
  page areas and content areas with breaks, overflow leaders and trailers and keep rules (long text goes on between
  its lines); draws and fields are drawn with captions, borders, fills, plain and rich text, images, shapes, check
  boxes, radio buttons, choice lists and comb fields, their values formatted by picture clauses (numbers, dates,
  times, text) in the form's locales. Scripts are not run; they, and whatever else is left out, are reported to
  `Diagnostics` (`XfaDiagnostic`). Malformed forms throw `XfaFormatException`; the work is bounded by `Limits`,
  including the objects repetition creates. Tested against public U.S. government forms with generated data and an
  independent Python oracle of their XFA.

### Fixed
- **Tables with collapsed borders paint their cells' background images and gradients** (`BoxStyle.BackgroundLayers`;
  CSS `background-image` on cells of `border-collapse: collapse` tables), not only their color.
- **A CFF2 font with a huge blend count** in a DICT threw `ArgumentOutOfRangeException` when it was subset (the count times the
  number of regions overflowed); it now throws `FontFormatException`, and the font is embedded whole. Found by fuzzing.
- **Deeply nested layout content no longer overflows the stack** when `MaxNestingDepth` is raised: finding the forced
  break and the page name a block starts with walked its chain of first children recursively, without the stack check
  every other recursion has. A test that depended on how far the JIT had optimized the layout code is now stable.
- **Text drawn with a glyph that stands for two characters extracts as each**: when one glyph is drawn for different
  texts (HarfBuzz draws U+200B with the space glyph; fonts share glyphs between look-alike characters), the second text
  gets a character code of its own, mapped to the glyph through a `/CIDToGIDMap` stream, so copying, searching and
  screen readers give each text. Before, every use extracted as the first text drawn: after an empty paragraph in a
  Word document, which is drawn as U+200B, every space extracted as U+200B. TrueType fonts; CFF fonts keep one text
  per glyph.
- **Hidden text in Word 97-2003 documents** is no longer drawn by `DocToPdf` (#261).
- **Text with a surrogate that lacks its other half** (U+D800-U+DFFF on its own, as damaged files hold) threw
  `ArgumentException` in layout: `Paragraph` and `TextShaper` now draw each one as U+FFFD. This covers every
  converter at once; a damaged Word 97-2003 document found by fuzzing still had a way through, and so did `_xD800_`
  escapes in PowerPoint files (#248).

### Documentation
- **Format support**: one page comparing what each converter draws, format by format (DOCX, DOC, RTF, ODT; XLSX, XLS,
  CSV; PPTX, PPT; EML, MSG, MHT), linked from each format guide and the README.
- **Migrating from Aspose** covers 1.1: a package for each Aspose product, and mapping tables for Aspose.PDF
  (`TextAbsorber`, `TextFragmentAbsorber`, `ImagePlacementAbsorber`, `Form.Flatten`, `OptimizeResources`,
  `Pages.Delete`, ...), Aspose.Words, Aspose.Cells, Aspose.Slides and Aspose.Email.
- **PdfDocument or PdfEditor?**: a section in the editing guide on which class does what.

## [1.1.0] - 2026-11-23

Reading, editing and converting: text and images out of PDFs, editing existing files, and Office documents, email
and spreadsheets to PDF, in new packages released with the core.

### Highlights
- **Extraction** (`RaiCore.Pdf.Extraction`, new): text in reading or layout order, search with positions, and images.
- **Editing** (`RaiCore.Pdf.Editing`, new): delete, insert, move and rotate pages, split by size, optimize, read and
  flatten forms, flatten annotations.
- **Word documents** (`RaiCore.Pdf.Documents`, new): DOCX, Word 97-2003 (`.doc`), RTF and ODT to PDF.
- **Spreadsheets** (`RaiCore.Pdf.Sheets`, new): CSV, XLSX and Excel 97-2003 (`.xls`) to PDF.
- **Presentations** (`RaiCore.Pdf.Slides`, new): PPTX and PowerPoint 97-2003 (`.ppt`) to PDF.
- **Email** (`RaiCore.Pdf.Email`, new): `.eml`, Outlook `.msg` and `.mht` to PDF, with attachments.
- **HTML fragments** in the layout API, **TIFF** images, and **PDF/A-1b/1a, PDF/A-2a/3a**.

### Added
- **One format exception per kind of input**: the converters report data they cannot read with their own
  `PdfException`: `DocumentFormatException` (new, `RaiCore.Pdf.Documents`: DOCX, DOC, RTF, ODT),
  `SpreadsheetFormatException` (`RaiCore.Pdf.Sheets`), `PresentationFormatException` (`RaiCore.Pdf.Slides`) and
  `MessageFormatException` (new, `RaiCore.Pdf.Email`: `.msg`), next to the core's `PdfFormatException`,
  `FontFormatException` and `ImageFormatException`. Catch `PdfException` for all of them. Errors of the shared compound
  file reader (`.doc`, `.xls`, `.ppt`, `.msg`) come out as the package's own exception, with the reader's as
  `InnerException`.
- **PDF/A-1b and PDF/A-1a** (#195): `PdfAConformance.PdfA1B` and `PdfA1A` write PDF/A-1 (ISO 19005-1) for archives
  that do not accept PDF/A-2 or 3. The file is PDF 1.4 (`%PDF-1.4`, a cross-reference table), and XMP says
  `pdfaid:part` 1. PDF/A-1 has no transparency: saving throws `InvalidOperationException` naming the page and the
  feature (a translucent color, a transparency group as for CSS `opacity`, a gradient with translucent colors, an SVG
  image that uses one, or an image with semi-transparent pixels) instead of dropping it; images whose pixels are fully
  transparent or opaque (GIFs, PNGs with a transparent color) keep their transparency as a stencil mask. 16-bit images
  are written with 8 bits per sample, CID font subsets get a `CIDSet` of every CID the embedded program has, and a CFF font that would be embedded whole as
  OpenType is rejected. Attachments and Factur-X are rejected (email conversion leaves attachments out with a warning,
  as for PDF/A-2). Level A is tagged like PDF/A-2a with the structure types of PDF 1.4: table row groups (THead,
  TBody, TFoot) are left out, their rows going to the table, other PDF 1.5 types are rejected, and languages must be
  RFC 1766 tags (letters only). veraPDF (CI) validates canvas, HTML and layout documents for 1b and 1a, also combined
  with PDF/UA-1.
- **HTML fragments in the layout API** (`RaiCore.Pdf.Html`): `HtmlFragment.Parse` and `ParseAsync` convert HTML with
  optional CSS (`HtmlFragmentOptions.Css`) into layout blocks for a `FlowDocument`, a `ContainerBlock` or a table cell
  (`AddTo`, `Blocks`), with the engine and the options of the HTML converter (`BaseUri`, `ResourceLoader`, `Fonts`,
  `FallbackFontFamilies`, `Hyphenators`, `Limits`), e.g. rich text from a database inside a code-built report. The
  fragment starts from a base `TextStyle` (`BaseStyle`: font families, size, color, line height, spacing, language
  and hyphenation of its root, inherited unless its CSS says otherwise); `Width` is what `vw` and build-time
  percentages resolve against. It renders as in a converted document (same word positions, same structure tags in a
  tagged document). What only pages have (`@page`, running elements, `string-set`, named pages, page numbers of
  `target-counter()`) is left out and reported in `HtmlFragment.Diagnostics` with the converter's usual diagnostics;
  headings become bookmarks only with `CreateBookmarks`. A new `htmlfragment` fuzz target (#197).
- **PowerPoint 97-2003 to PDF** (`RaiCore.Pdf.Slides`): `PptToPdf.Convert` (also `ConvertToBytes` and `ConvertAsync`,
  over bytes, streams and paths, with `PptxConverterOptions`) converts binary PowerPoint presentations (`.ppt`, `.pps`,
  `.pot`) with the slide model and renderer of the PPTX converter. The compound file's "Current User" and
  "PowerPoint Document" streams, the user edit chain with its persist directories (newer edits win; a looping or
  broken chain is recovered from the directories in the stream), PowerPoint 95 files' PowerPoint 97 copy
  (`PP97_DUALSTORAGE`); the document (slide size, fonts, default text styles, the drawing group's default shape
  properties and picture store, slide headers and footers, hyperlinks), the slide list in order with its outline text,
  masters and title masters (shapes, backgrounds, color schemes, text master styles by text type and level), hidden
  slides; OfficeArt drawings: shapes with their primary, secondary and tertiary properties and master shapes, client
  and child anchors (rotated anchors turned back), groups (tables are groups), about 90 preset shape types with adjust
  values, free forms from their vertices and segments, solid, gradient, pattern, picture and texture fills, lines with
  dashes, joins, caps and arrowheads, pictures from the "Pictures" stream (PNG, JPEG, TIFF, DIB as BMP; EMF and WMF
  decompressed and reported, PICT reported), cropped, and embedded objects as their picture; text from text boxes or
  outline text references with paragraph and character runs, text rulers, bullets (symbol fonts as Unicode), links,
  WordArt as plain text, and the master's date, footer and slide number placeholders as the headers and footers say.
  Encrypted presentations, PowerPoint 95 and 4.0 files throw `NotSupportedException`; `PptxToPdf` given a `.ppt` file
  now points at `PptToPdf`. A new `ppt` fuzz target; a corpus of Apache POI and Apache Tika samples checked against an
  independent record reader and against POI's and Tika's own test expectations (#206).
- **Excel 97-2003 to PDF** (`RaiCore.Pdf.Sheets`): `XlsToPdf.Convert` (also `ConvertToBytes` and `ConvertAsync`, over
  bytes, streams and paths, with `XlsxConverterOptions`) converts binary workbooks (`.xls`, BIFF8 of Excel 97-2003,
  also BIFF5/7 of Excel 5.0 and 95 in their code page) into the same sheet model as XLSX workbooks, laid out the same
  way. The record reader joins CONTINUE records, and shared strings split across them re-read their flags; workbook
  globals (date system, fonts, number formats, XF styles with alignment, wrapping, indents, borders and fills, the
  workbook's palette, sheets and their visibility, print areas and titles from built-in names) and worksheets (every
  cell record, formulas' cached numbers, strings, booleans and errors, merged cells, column widths, row heights,
  hidden rows and columns, page setup, right-to-left sheets; embedded chart substreams skipped). Lengths, offsets and
  counts are checked against their records, sheets with wrong offsets are found in order, NUL characters and lone
  surrogates are cleaned out of strings; damaged compound files throw `SpreadsheetFormatException`, encrypted
  workbooks (also with Excel's default password) and Excel 2.x-4.0 files `NotSupportedException`. `XlsxToPdf` now
  converts `.xls` workbooks too (it threw `NotSupportedException`), and `XlsToPdf` converts `.xlsx` workbooks named
  `.xls`. A new `xls` fuzz target with raw BIFF seeds; a corpus of Apache POI workbooks checked against xlrd (#205).
- **Word 97-2003 to PDF** (`RaiCore.Pdf.Documents`): `DocToPdf.Convert` (also `ConvertToBytes` and `ConvertAsync`,
  over bytes, streams and paths, with `DocConverterOptions`) converts binary Word documents (`.doc`, [MS-DOC], Word 97
  to 2003) with the shared word-processing model and renderer of DOCX, on the core's compound file reader. The FIB
  (stories by their character counts), the piece table (8-bit code page 1252 and UTF-16 pieces, fast-saved documents
  with their pieces' property modifiers), the formatted disk pages of character and paragraph properties (with
  `sprmPHugePapx`), property modifiers sized by their `spra` bits; the style sheet (paragraph and character styles with
  their bases, toggles relative to the style) and font table; character formatting (fonts, sizes, bold, italic,
  underline styles, strikethrough, palette and RGB colors, highlighting and shading, superscript, subscript, raised
  text, letter spacing, capitals, small capitals, hidden text, deleted revisions left out, Symbol and Wingdings text as
  Unicode) and paragraph formatting (alignment, indents, spacing, line spacing, keep, keep with next, page break
  before, widow control, borders, shading, tab stops with leaders); lists (`PlfLst`/`PlfLfo` with level overrides and
  start values, counted as Word counts them: restarts after a higher level with `ilvlRestartLim`, skipped levels, legal
  numbering); tables (cell and row marks, nested tables by `sprmPItap`, the grid from all rows' `sprmTDefTable` cell
  edges, merged cells both ways, table and cell borders, shading, padding, vertical alignment, row heights, header
  rows); sections (PlcfSed/SEPX page size, margins, start, columns reported, page numbers), headers and footers for
  first, even and odd pages (PlcfHdd, inherited from the section before); fields (`HYPERLINK` as links, `PAGE`,
  `NUMPAGES`, `SECTIONPAGES` and `PAGEREF` computed, others as their result); inline pictures (PICF in the Data
  stream) and floating ones (the drawing layer's BLIP store and shapes, `FSPA` anchors) in PNG, JPEG, TIFF and bitmap
  formats; text boxes after their paragraph; bookmarks, footnotes and endnotes; title and author from the summary
  information. EMF/WMF/PICT pictures, other shapes, comments and several columns are reported to `Diagnostics`.
  Encrypted documents and Word 6/95 (and Word 1/2) files throw `NotSupportedException`, damaged ones
  `DocumentFormatException`; every offset is checked against its stream, within `ResourceLimits` (`MaxElements`,
  `MaxNestingDepth`, `MaxTableCells`, `MaxResourceBytes`, `MaxDecompressedBytes`, `MaxPages`, `Timeout`). `DocxToPdf`
  given a `.doc` file and `DocToPdf` given a `.docx` file convert it as what it is (no longer `NotSupportedException`);
  `OdtToPdf` names `DocToPdf` in its error. A new `doc` fuzz target; Apache POI and Tika
  samples checked against the text, list labels, links and errors their own unit tests expect, and olefile's summary
  information (#204).
- **ODT to PDF** (`RaiCore.Pdf.Documents`): `OdtToPdf.Convert` converts OpenDocument text documents (`.odt`, `.ott`
  templates and flat `.fodt` XML documents, ODF 1.0 to 1.3) with the shared word-processing model and renderer of
  DOCX. Common, automatic and default styles with `style:parent-style-name` chains and font face declarations;
  paragraph properties (margins, indents, alignment, proportional, fixed and at-least line height and leading,
  borders, background, keep with next, keep together, page breaks before and after, widows and orphans, tab stops with
  leaders, relative to the indent unless the settings say otherwise) and text properties (fonts, sizes, bold, italic,
  underline styles, strikethrough, colors, backgrounds, superscript, subscript, raised text, small capitals, upper,
  lower and capitalized text, letter spacing, hidden text); headings with outline levels and outline numbering; lists
  with list styles (number formats, prefix, suffix, displayed levels, start values, bullets), nesting, continued
  numbering (`text:continue-numbering`, `text:continue-list`), item start values, list headers and both indent modes;
  tables (column widths, absolute or relative, repeated columns, header rows repeated on every page, column and row
  spans, borders, padding, backgrounds, vertical alignment, row heights, nested tables, repeated rows and cells);
  sections, tables of contents and other indexes as their content; master pages with their page layout (size,
  orientation, margins, header and footer height), headers and footers for left, right and first pages (ODF 1.3
  `style:header-first`, and the next page style of a first page style), switched by paragraph and table styles, with
  page number restarts and formats; images in the package or embedded as base64 (the first drawable of several
  alternatives, and the replacement images of charts and objects), inline, floating or on lines of their own; text
  boxes and shape text after their paragraph; links, bookmarks, footnotes and endnotes with custom citations, page
  numbers, page counts and page references computed, other fields as their text; tracked changes accepted; title and
  author from the metadata. `OdtConverterOptions` has the options of `DocumentConverterOptions`. Encrypted documents
  throw `NotSupportedException`, malformed ones `DocumentFormatException`; repeated table rows and cells count against
  `MaxElements` and `MaxTableCells`. A new `odt` fuzz target with uncompressed seeds; fixtures from a committed script,
  checked against odfpy, and Apache Tika samples (#203).
- **RTF to PDF** (`RaiCore.Pdf.Documents`): `RtfToPdf.Convert` (also `ConvertToBytes` and `ConvertAsync`, over bytes,
  streams and paths, with `RtfConverterOptions`) converts Rich Text Format documents (RTF 1.9.1, as Word, WordPad,
  LibreOffice and Outlook write them) with the shared word-processing model and renderer of the DOCX converter. Text in
  the document's and its fonts' code pages (`\ansicpg`, `\fcharset`, `\cpg`, double-byte code pages too) and Unicode
  escapes (`\u` with `\uc`, `\upr`/`\ud`), Symbol and Wingdings text as Unicode; the font, color and style tables
  (paragraph and character styles with `\sbasedon`, headings by name, `\defchp`/`\defpap` defaults); character
  formatting (fonts, sizes, bold, italic, underline styles, strikethrough, colors, highlighting and shading,
  superscript, subscript, raised text, letter spacing, capitals, small capitals, hidden and deleted text left out) and
  paragraph formatting (alignment, indents, spacing, line spacing, keep, keep with next, page break before, widow
  control, borders, shading, tab stops with leaders); lists (`\listtable`/`\listoverridetable` counted as Word counts
  them, old-style `\pn` numbering, `\listtext` as the fallback); tables (the grid from all rows' cell edges, merged
  cells both ways, borders, shading, padding, header rows, nested tables with `\itap`/`\nestcell`/`\nestrow`);
  sections with page setup and starts, headers and footers for first, left and right pages, page numbers (`\chpgn`,
  `PAGE`, `NUMPAGES`, `SECTIONPAGES`) and `PAGEREF`; `HYPERLINK` fields as links, other fields as their result; PNG,
  JPEG and device-independent bitmap pictures at their goal size (the `\shppict` picture, shapes' pictures and text,
  objects' results); bookmarks, footnotes and endnotes; revisions accepted; title and author from `\info`. EMF/WMF/PICT pictures, drawing objects,
  comments and several columns are reported to `Diagnostics`. Untrusted input is read in one pass without recursion,
  within `ResourceLimits` (`MaxElements` for the tokens, `MaxNestingDepth` for groups and tables, `MaxTableCells`,
  `MaxResourceBytes` per picture, `MaxPages`, `Timeout`); data that is not RTF throws `DocumentFormatException`. A new
  `rtf` fuzz target; hand-made fixtures from a committed script and Apache Tika samples, checked against striprtf
  (#202).
- **PPTX to PDF** (`RaiCore.Pdf.Slides`, new package): `PptxToPdf.Convert` converts PowerPoint presentations (`.pptx`,
  `.pptm`, `.ppsx`, `.potx`, strict Office Open XML) with one page per slide at the slide's size. Each slide is drawn
  with its master's and layout's shapes (unless hidden) and background (own, layout, master or theme background
  style); placeholders inherit position, shape, fill and text styles from the layout and master (by index, then
  type), text boxes use the presentation's default text style. Theme colors through the color map and its overrides
  with their transforms (luminance, tint, shade, saturation, hue, alpha), theme fonts, and the style matrix of shapes
  inserted with a style. More than 80 preset shapes with adjust values and free forms with guide formulas (other
  presets as rectangles); solid, gradient (linear and radial), pattern (average color) and picture fills; lines with
  dashes, caps, joins and arrowheads; rotation, flips and nested groups. Text frames with insets, anchoring, wrapping,
  vertical text and autofit (the stored font scale and line spacing reduction, and further shrinking when the text
  still overflows); paragraphs with alignment, indents, spacing, line spacing, character and symbol-font bullets and
  automatic numbering; runs with font, size, bold, italic, underline, strikethrough, color, superscript, capitals,
  character spacing and links. Pictures (cropped, clipped to their shape, translucent, with borders; the poster of
  video and audio), tables (grid, rows growing to fit, merged cells, margins, fills, borders, the presentation's table
  styles and PowerPoint's default Medium Style 2), SmartArt from its stored drawing and embedded objects from their
  picture. Options: `Fonts`, `FallbackFontFamilies` (metric-compatible substitutes such as Carlito for Calibri are
  tried first), `IncludeHiddenSlides`, `SlideNumbers`, `Title`, `Tagged` (sections per slide with headings,
  paragraphs, tables and figures with alternative text), `Limits` and `Diagnostics` (the new `SlideDiagnostic`: charts,
  EMF/WMF pictures, unknown presets and other content that is not drawn). Encrypted presentations and `.ppt` files
  throw `NotSupportedException`; malformed packages the new `PresentationFormatException`. Parts are read with no
  DTDs and nothing external, within `ResourceLimits` (`MaxDecompressedBytes` and `MaxElements` for all parts,
  `MaxNestingDepth`, `MaxPages` for the slides, `MaxTableCells`, `Timeout`). The internal slide model is meant to be
  shared with a future PowerPoint 97-2003 converter (#206). A new `pptx` fuzz target covers it (#69).
- **DOCX to PDF** (`RaiCore.Pdf.Documents`, new package for the word-processing formats): `DocxToPdf.Convert` lays out
  Word documents (`.docx`, `.docm`, `.dotx`, strict Office Open XML) with the layout engine. Styles (paragraph,
  character and table styles with `basedOn` chains, document defaults, theme fonts and colors); paragraphs (alignment,
  indents, spacing added as Word adds it, contextual spacing, line spacing, borders and shading, keep with next, keep
  lines, page break before, widow control); text (fonts, sizes, bold, italic, underline styles, strikethrough, colors,
  highlighting, superscript, subscript, capitals, small capitals, hidden text left out); multi-level numbering with
  `%1.%2.` labels, letters, roman numerals, ordinals and bullets, start values and restarts; tab stops with leaders;
  tables (grid widths, merged cells, borders, shading, padding, header rows repeated on every page, rows that do not
  break, nested tables, table styles with conditional formats); sections (page size, orientation, margins, new page,
  continuous, even or odd page) with headers and footers for first, even and other pages and page numbers (`PAGE`,
  `NUMPAGES`, `SECTIONPAGES`, restarts and formats); `PAGEREF` with the PDF's page numbers (tables of contents);
  PNG, JPEG, GIF, BMP, TIFF and WebP images inline or floating; hyperlinks and bookmarks; footnotes and endnotes;
  tracked changes accepted; content controls unwrapped; headings as PDF bookmarks; tagged PDF. Options:
  `Fonts`, `FontFamily`, `FallbackFontFamilies`, `HeadingBookmarks`, `Title`, `Tagged`, `Diagnostics` (unsupported
  content such as charts and text boxes, images that cannot be drawn, layout overflow) and `Limits`
  (`MaxDecompressedBytes`, `MaxElements` for the XML elements, `MaxNestingDepth`, `MaxTableCells`, `MaxPages`,
  `Timeout`). Malformed packages throw `DocumentFormatException`; encrypted documents and `.doc` files
  `NotSupportedException`. A shared word-processing model and renderer for the RTF, ODT and `.doc` converters to come.
  A new `docx` fuzz target with uncompressed seeds; fixtures from a committed python-docx script and Apache POI
  samples. See the [Word documents guide](https://raicevic.github.io/raicore-pdf/guides/documents.html) (#67).
- **PDF/A level A** (`PdfAConformance.PdfA2A` and `PdfA3A`): PDF/A-2a and PDF/A-3a, PDF/A-2b and -3b plus the logical
  structure. Choosing them tags the document (HTML and layout documents from their semantics), maps every glyph to
  Unicode in the fonts' ToUnicode maps, identifies the file as conformance A in XMP, and adds save-time checks that
  throw `InvalidOperationException` like the other PDF/A rules: a document language, BCP 47 language tags, standard
  structure types only (no role map is written), alternate text on figures and formulas (`alt` in HTML), no content
  drawn before the document was tagged, and actual text for Private Use Area characters. Attachments need PDF/A-3a as
  they need PDF/A-3b, and `AttachFacturXInvoice` keeps PDF/A-3a. CI validates HTML, layout and canvas documents with
  veraPDF for 2a and 3a (#194).
- **XLSX to PDF** (`RaiCore.Pdf.Sheets`): `XlsxToPdf.Convert` converts Excel workbooks (`.xlsx`, `.xlsm`, strict
  Office Open XML) with the same table layout as CSV: each visible sheet (or those `SheetNames`/`SheetIndexes` choose)
  from a new page, with a bookmark per sheet and optional name headings. Values are shown in their number formats
  (built-in and custom: digit placeholders, thousands separators and scaling, percent, scientific, fractions, literal
  text and currencies, sections with colors and conditions, dates and times with elapsed hours and fractions of seconds,
  the 1900 and 1904 date systems), formulas as their cached values (never calculated), shared, inline and rich text
  strings, booleans and errors; invariant output unless `Culture` names a culture. Styles: fonts (bold, italic,
  underline, strikethrough, size, color, the workbook's family where the font collection has it), solid and pattern
  fills, borders (thin to thick, dashed, dotted, double, colored), horizontal and vertical alignment, wrapping, indents;
  theme and indexed colors with tints. Merged cells, the sheet's column widths and row heights (or `AutoFit`), hidden
  rows, columns and sheets left out, right-to-left sheets, and text that does not wrap running on over empty cells.
  From the page setup (`UsePageSetup`): the print area, print titles as header rows repeated on every page, landscape,
  print scale and fit to width. Encrypted workbooks, `.xls` and `.xlsb` throw `NotSupportedException`; malformed
  packages throw the new `SpreadsheetFormatException`. Parts are read with no DTDs and nothing external, within
  `ResourceLimits` (`MaxDecompressedBytes` for all parts, `MaxTableCells` per sheet, pages, time). Charts, images,
  conditional formatting, comments, headers and footers are not converted. A new `xlsx` fuzz target covers it (#68).
- **CSV to PDF** (`RaiCore.Pdf.Sheets`, new package): `CsvToPdf.Convert` lays out CSV data as a table with the
  layout engine: the header row repeated on every page, column widths fitted to the content within the page width
  (narrow columns whole, wide ones sharing the rest by how much text they hold), words wider than their column broken
  and columns that cannot all get a readable width printed in bands, so text is never clipped; numbers (signs,
  thousands separators, currency and percent signs) and dates right-aligned, text aligned to its direction. RFC 4180
  parsing (quoted fields with delimiters, line breaks and doubled quotes; CRLF, LF or CR), detection of the delimiter
  (comma, semicolon, tab, bar, Excel's `sep=` line), the encoding (byte order mark, UTF-8, UTF-16, Windows-1252) and
  the decimal separator, or options for them; landscape pages, fit-to-width (smaller text down to a minimum size),
  fonts with fallbacks, gridlines, tagged tables. Malformed data (stray quotes, ragged rows, invalid bytes) is read the
  way spreadsheet programs read it, within `ResourceLimits` (cells, pages, time). A new `csv` fuzz target covers it.
  See the [CSV guide](https://raicevic.github.io/raicore-pdf/guides/sheets.html) (#201).
- **TIFF images** wherever images are accepted (`PdfImage.Load`, `<img>`, CSS backgrounds): bilevel, grayscale,
  palette, RGB and CMYK with 1 to 16 bits per sample, strips or tiles, separate planes, uncompressed, PackBits, LZW and
  Deflate (with the horizontal predictor), associated and unassociated alpha, both byte orders and BigTIFF. JPEG data
  is embedded without re-encoding (several strips joined into one JPEG with restart markers), and CCITT Group 3 and 4
  fax data as it is (`/CCITTFaxDecode`). `PdfImage.GetPageCount` and `PdfImage.Load(data, pageIndex)` read multi-page
  files; the Orientation tag is applied, and the resolution sets the natural size. Fax data in several strips,
  floating-point samples and old-style JPEG are rejected with an `ImageFormatException` saying so. A new `tiff` fuzz
  target covers the decoder (#196).
- **Outlook messages** (`RaiCore.Pdf.Email`): `EmailToPdf` also converts `.msg` files (recognized by content), like
  the email they stand for: sender, recipients, date and subject from the message's properties ([MS-OXMSG]); the HTML
  body, else the HTML encapsulated in the compressed RTF body ([MS-OXRTFCP], [MS-OXRTF]), else the plain text one;
  attachments with images shown by `cid:` in place, and attached messages read recursively and embedded or appended as
  `.eml`. `EmbedOriginal` embeds the `.msg` itself. They are read by a new internal Compound File Binary reader
  ([MS-CFB], with property sets [MS-OLEPS]), hardened with `ResourceLimits` and the new `cfb` fuzz target (#199, #200).
  A `.msg` that is not a message or is damaged throws the new `MessageFormatException`.
- **Email to PDF** (`RaiCore.Pdf.Email`, new package): `EmailToPdf.Convert` converts email messages (`.eml`) and web
  archives (`.mht`, `.mhtml`) through the HTML engine: a header block (from, to, cc, date, subject; fields and labels
  configurable, styleable with CSS), the HTML body (or the plain text one, wrapped, with `format=flowed` and links)
  with its `cid:` and `Content-Location` images in place, and the attachments embedded as PDF file attachments, listed
  and optionally appended (images and text files as pages, PDFs imported, HTML files and attached messages converted).
  `EmbedOriginal` embeds the message itself, unchanged, as the PDF's source file (`message/rfc822` or
  `multipart/related`, `/AFRelationship /Source`; not under PDF/A-2, as for attachments). MIME parsing of its own: multipart/alternative, related and mixed, quoted-printable and base64, charsets including
  ISO-2022-JP, Shift_JIS, GB18030, Big5 and EUC-KR, RFC 2047 encoded words and RFC 2231 parameters, malformed messages
  read leniently within `ResourceLimits`, and no local files loaded. See the
  [email guide](https://raicevic.github.io/raicore-pdf/guides/email.html) (#198).
- **Form flattening** (`RaiCore.Pdf.Editing`): `PdfEditor.FlattenForm()` draws each widget's normal appearance (by
  state, with its box and matrix) into the page content, makes appearances where there are none or the form sets
  `/NeedAppearances` (text: single-line, multiline, comb, password, alignment, auto size; choice; check boxes and radio
  buttons), and removes the widgets, the fields and the form; hidden widgets are not drawn, a static XFA form is
  removed with the form, and a dynamic XFA form is refused (#191).
- **Annotation flattening** (`RaiCore.Pdf.Editing`): `PdfEditor.FlattenAnnotations(options)` draws the normal appearance
  of each annotation that is shown and printed (highlights and other text markup, ink, shapes, free text, stamps,
  notes) into the page content and removes it with its pop-up; hidden, screen-only and unprinted annotations are removed
  without being drawn. Links are kept unless `KeepLinks` is off, `Types` picks annotation types, and form fields are
  left to `FlattenForm()` (#192).
- **Form reading** (`RaiCore.Pdf.Editing`): `PdfEditor.GetForm()` tells whether a document has an AcroForm and an XFA
  form (static or dynamic, by `/NeedsRendering`) and lists its fields: fully qualified name, type (text, check box,
  radio buttons, push button, list, combo, signature), values, choice options, read-only and required flags, and widgets
  with their page, rectangle, hidden flag and appearance state; inherited attributes are followed (#190).
- **Optimization** (`RaiCore.Pdf.Editing`): `PdfEditor.Optimize(options)` makes documents smaller without changing how
  they look: resource entries no content uses are removed (pages, forms, patterns, Type 3 fonts; inherited and shared
  resources by the union of their users), identical streams and fonts/font descriptors/encodings/graphics states are
  merged, uncompressed streams are compressed, and `UseObjectStreams` writes object streams with a cross-reference
  stream (PDF 1.5). Merged files with repeated fonts and images typically shrink to a third to two thirds (#193).
- **Split by size** (`RaiCore.Pdf.Editing`): `PdfEditor.SplitBySize(maxBytes)` cuts a document into consecutive parts
  whose files stay below a size limit, each with as many pages as fit (shared fonts and images counted once per part;
  bookmarks and links kept within each part), and reports pages larger than the limit on their own as parts of their own
  (#189).
- **Page editing** (`RaiCore.Pdf.Editing`, new package): `PdfEditor.Open` opens an existing PDF (path, stream, bytes
  or reader; encrypted files with their password), `Pages` deletes, inserts (from other files, any selection and
  order), moves and rotates pages, and `Save`/`SaveAsync`/`ToArray` write a new file any number of times. Each save
  writes only the objects still in use, so deleted pages' resources leave the file, along with bookmarks, named
  destinations, links and form fields that pointed at them; the rest of the document (outline, forms, structure,
  metadata, attachments) is kept. See the [editing guide](https://raicevic.github.io/raicore-pdf/guides/editing.html)
  (#188).
- **Image extraction** (`RaiCore.Pdf.Extraction`): `ImageExtractor` lists the images drawn on each page (image
  XObjects, inline images, images in forms; hidden ones left out) with their bounds, displayed size and rotation in
  the page's user space, pixel size, color space, bits per component and masks. `ExtractedImage.GetData` exports
  JPEG and JPEG 2000 data as it is, other images as PNG (CMYK and indexed colors converted, soft masks, stencil
  masks and color key masks as alpha), CCITT fax images as TIFF, and JBIG2 and other color spaces raw. See the
  [image extraction guide](https://raicevic.github.io/raicore-pdf/guides/image-extraction.html) (#187).
- **Text search** (`RaiCore.Pdf.Extraction`): `TextExtractor.Find` and `FindOnPage` find plain text (optionally
  ignoring case) or the matches of a regular expression, with each match's page, text and rectangles: one per line,
  in PDF points in the page's user space, so they can be drawn over the page or turned into annotations as they are.
  Text matches across font changes and line breaks, in reading order, right-to-left text in logical order, on rotated
  and cropped pages (#186).
- **Text extraction** (`RaiCore.Pdf.Extraction`): `TextExtractor` reads the text of existing PDF files page by page
  (`ExtractPage`, `ExtractPages`, `ExtractText`), from any producer:
  - **Reading order:** lines from top to bottom, word spaces from gaps where a file has no space characters, columns
    read one after the other with headings across them kept in place, tables row by row, Arabic and Hebrew in logical
    order, turned pages and vertical writing.
  - **Other modes:** `TextExtractionMode.Layout` keeps columns in place on each line, as `pdftotext -layout` does, and
    `Raw` follows the drawing order.
  - **Clean-up:** ligatures and Arabic presentation forms become letters, text drawn twice for a bold effect is read
    once, and text hidden by clipping or outside the page is left out.
  - **Scans:** `PageText.HasText` finds pages that need OCR, and `IncludeInvisibleText` controls existing OCR layers.
  - **Problems** are reported in `PageText.Diagnostics`, never thrown.

  See the [text extraction guide](https://raicevic.github.io/raicore-pdf/guides/text-extraction.html) (#185).
- **RaiCore.Pdf.Extraction** (new package, no public API yet), the base for reading what is on the pages of
  existing PDFs: a content-stream interpreter that follows the graphics and text state and form XObjects, and reports
  each glyph (its text and its position, size and bounds on the displayed page, rotation and crop box applied) and
  each image placement. Fonts are decoded to text through ToUnicode maps, the standard encodings, `/Differences` glyph
  names (Adobe Glyph List), built-in encodings of embedded Type 1 fonts, composite fonts with Identity, Unicode and
  embedded CMaps, Type 3 fonts, and the `cmap` of embedded TrueType fonts. Malformed content is skipped with
  diagnostics, never thrown, within resource limits, and the interpreter has its own fuzz target (`extract`). It
  follows clipping paths and form bounding boxes, so hidden text and images can be told apart (#184, #185).

### Fixed
- Tagged PDF: an empty table cell (such as `<td></td>`) is written as a cell without content instead of being left
  out, so every row of a table keeps its columns, as PDF/UA requires (ISO 14289-1, 7.2).
- HTML conversion no longer throws on CSS that fuzzing found: colors whose alpha is empty or a number with a unit
  in `lab()`, `lch()`, `oklab()`, `oklch()` and `rgb()` (`rgb(1 2 3 / )`) are dropped as invalid; an empty
  `local()` in an `@font-face` `src` is skipped; and a nesting selector (`&`) outside a nested rule is reported as
  unsupported.

## [1.0.0] - 2026-11-17

The first release: HTML, CSS and Razor to PDF with RaiCore.Pdf's own layout engine, no browser involved, on Windows,
Linux (also Alpine) and macOS, x64 and Arm64, trimmed or with Native AOT.

### Highlights
- **HTML and CSS to PDF** with the cascade, flexbox, floats, positioning, tables that repeat their headers across
  pages, backgrounds and gradients, web fonts, SVG, bidirectional text and hyphenation; see the
  [CSS support](https://raicevic.github.io/raicore-pdf/guides/css-support.html).
- **CSS Paged Media and GCPM:** page sizes and margins, headers and footers in margin boxes, page numbers, running
  elements, named pages, `string-set`, tables of contents with `target-counter()` and `leader()`, and bookmarks.
- **Razor components to PDF**, in any app or with ASP.NET Core dependency injection, also streamed into a response.
- **A layout API** for documents built in code, and a **PDF API** for drawing, reading and merging PDFs.
- **Standards:** PDF/A-2b and -3b, Factur-X/ZUGFeRD e-invoices, tagged PDF and PDF/UA-1, AES-256 encryption.
- **Safe with untrusted input:** resource limits and time limits, no network access unless you allow it, and fuzz
  testing in CI.
- **The same output everywhere:** bundled metric-compatible fonts and deterministic bytes with a fixed clock.

### Project
- Documentation: [guides and API reference](https://raicevic.github.io/raicore-pdf/), with every code sample compiled
  and run in CI; [migration guides](docs/migration/README.md) from wkhtmltopdf/DinkToPdf, Puppeteer/Playwright,
  IronPDF, Aspose and SelectPdf; runnable [samples](samples/README.md) (invoice, report with a table of contents,
  letter for a window envelope, ASP.NET Core with Razor, background worker, Docker image).
- The public API is recorded in each package's `PublicAPI.Shipped.txt` and checked by the build, and packing checks
  binary compatibility with the last release from 1.0.0 on. APIs marked `[Experimental]` are the exception
  ([docs/experimental.md](docs/experimental.md)).
- The core packages share one version and depend on each other at exactly that version; packages for further formats
  will have versions of their own (see "Releasing" in [CONTRIBUTING.md](CONTRIBUTING.md)).
- [SECURITY.md](SECURITY.md) (private vulnerability reporting), [SUPPORT.md](SUPPORT.md) (supported versions and .NET
  releases, versioning, release cadence) and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

### RaiCore.Pdf
- PDF writer with compressed streams, document information and the 14 standard fonts.
- Embedded TrueType, OpenType, TTC, WOFF and WOFF2 fonts, subset to the glyphs used, with ToUnicode maps so text
  stays searchable and copyable. Fonts whose license forbids embedding are rejected.
- CFF and CFF2 subsetting: OpenType fonts with PostScript outlines are embedded as a CID-keyed CFF
  (`/FontFile3 /CIDFontType0C`) holding only the used glyphs, which keep their original glyph IDs as CIDs.
  Name-keyed and CID-keyed fonts (each Font DICT with its own Private DICT and subroutines) are supported;
  subroutines are inlined, since that compresses smaller than keeping the called ones. CFF2 (variable) fonts are
  converted to CFF at their default instance. Cross-checked against fontTools on the 137 CFF faces of macOS, Source
  Sans 3 and Noto Sans JP.
- JPEG (embedded without re-encoding, EXIF orientation applied), PNG (all color types and bit depths, interlacing,
  transparency; verified against PngSuite) and GIF images, each stored once per document.
- BMP images: BITMAPCOREHEADER, BITMAPINFOHEADER and V2-V5 headers; 1, 2, 4 and 8-bit palettes (kept indexed),
  16, 24 and 32-bit pixels, BI_RLE8/BI_RLE4 (skipped pixels are transparent), BI_BITFIELDS/BI_ALPHABITFIELDS,
  bottom-up and top-down rows, and 32-bit alpha (ignored when every alpha byte is zero, as browsers do). BMPs with
  embedded JPEG or PNG data are rejected.
- WebP images, decoded in managed code with no dependencies: lossy (VP8) and lossless (VP8L), and the extended
  format with alpha (ALPH chunks, raw or lossless-compressed, with every prediction filter). Lossy decoding matches
  libwebp's output bit for bit, including its chroma upsampling. An animated WebP shows its first frame.
- SVG images drawn as PDF vectors in a form XObject (`PdfImage.Load` detects them, `PdfImage.LoadSvg` takes markup;
  `PdfImage.NaturalWidth`/`NaturalHeight` give the natural size in points). A static SVG 1.1 subset: paths with arcs,
  basic shapes, groups, transforms, nested `svg`, `use`/`symbol`, fills and strokes with dashes, caps, joins and fill
  rules, presentation attributes, `style` attributes and `<style>` sheets with type, class and id selectors,
  `currentColor`, linear and radial gradients (shading patterns), clip paths, `text`/`tspan` in the standard fonts
  and `data:` images. `viewBox` and `preserveAspectRatio` are honored, also when drawn at another aspect ratio.
  Unsupported features are skipped; DTDs are ignored and nothing external is loaded.
- Drawing API: paths, fills, strokes, colors, text and images.
- Document outline (bookmarks): `PdfDocument.Outline` with nested `PdfOutlineItem`s (Unicode titles, a
  `PdfDestination` to jump to, open or closed), and `PdfDocument.PageMode`, which by default opens the outline panel when
  the document has an outline.
- Links: `PdfPage.AddLink` writes link annotations (borderless) that open a URI (a URI action, written as 7-bit ASCII
  with other characters percent-encoded) or jump to a `PdfDestination` or a named destination.
  `PdfDocument.NamedDestinations` is written as the catalog's `/Names` tree; links may be added before their
  destinations, and links to names missing when the document is saved are left out. `PdfCanvas.AddLink` places links
  in the canvas's current coordinates (it follows `Transform`, `SaveState` and `RestoreState`), and `PdfPage.Links`
  lists them.
- Transparency: `PdfColor` has an `Alpha` (`new PdfColor(r, g, b, alpha)`, `FromRgba`, `WithAlpha`; opaque by
  default), which `SetFillColor` and `SetStrokeColor` apply as the fill or stroke alpha (extended graphics states with
  `/ca` and `/CA`, written only when the alpha changes and stored once per document). `PdfCanvas.BeginGroup(opacity)`
  and `EndGroup()` draw everything in between as an isolated transparency group (a form XObject) composited with one
  opacity, so overlapping shapes do not show through each other; groups nest. Images are drawn opaque whatever the
  fill alpha. Pages that use transparency blend in RGB (a page group). SVG `opacity`, `fill-opacity`,
  `stroke-opacity`, `stop-opacity` and translucent colors use real alpha, and group opacity (`<g opacity>`, or a
  shape with both fill and stroke) is a transparency group.
- Document metadata in XMP: an uncompressed metadata stream (`/Type /Metadata /Subtype /XML`, in an `xpacket` with
  padding) kept in sync with the Info dictionary: `dc:title`, `dc:creator`, `dc:description`, `dc:subject` (the
  keywords split at commas and semicolons), `dc:language`, `xmp:CreateDate`/`ModifyDate`/`MetadataDate` (the same
  moments as the Info dictionary's dates), `xmp:CreatorTool`, `pdf:Producer` and `pdf:Keywords`.
  `PdfDocument.IncludeXmpMetadata = false` leaves it out. `PdfDocumentInfo.ModificationDate` (defaults to the creation
  date) is written as `/ModDate`, and `PdfDocumentInfo.CustomProperties` as custom Info entries (text strings) and in
  Adobe's `pdfx:` XMP namespace, as Acrobat does.
- Document language: `PdfDocument.Language` (a BCP 47 tag) is written as the catalog's `/Lang` and as `dc:language`.
- Encryption (the standard security handler): `PdfDocument.Encryption = new PdfEncryption { ... }` with a user
  password (to open the file), an owner password (full access; random when left out) and `PdfPermissions` (print,
  high-quality print, modify, copy, annotate, fill forms, accessibility extraction, assemble). AES-256 (PDF 2.0,
  revision 6, `/AESV3`, SHA-2 password hashing; written as Adobe extension level 8 of PDF 1.7) by default, AES-128
  (revision 4, `/AESV2`) for old readers. Every string and stream of every object is AES-CBC encrypted with a random
  IV, after compression; `EncryptMetadata = false` leaves the XMP metadata stream readable. With only an owner password
  the file opens without one and the viewer enforces the permissions. Passwords go through a simplified SASLprep and
  are cut to 127 UTF-8 bytes. Verified with qpdf, pypdf and PDFium.
- PDF/A-2b and PDF/A-3b (ISO 19005-2/-3, level B) for archiving: `PdfDocument.PdfAConformance`. The document gets an
  sRGB output intent (a 456-byte ICC v2 profile, CC0), `pdfaid:part`/`conformance` in its XMP and a PDF/A extension
  schema for the `pdfx:` custom properties; links get the Print flag. Saving enforces the rules the library could
  break, with an `InvalidOperationException` that names the problem: every font embedded, no .notdef glyph (text a
  font has no glyph for), no encryption, no CMYK images, no pages imported or drawn from other PDF files, XMP on.
  `PdfDocument.StandardFontSubstitute` embeds a TrueType font in place of the standard fonts (a simple font with
  WinAnsi encoding and a (3,1) `cmap`, so the codes already drawn keep working); `EmbedStandardFonts()` from
  RaiCore.Pdf.Fonts uses Liberation. Validated with veraPDF in CI.
- Attachments: `PdfDocument.Attachments` with `PdfAttachment` (file name, data, MIME type, description, relationship,
  dates). Each is an embedded file (Flate-compressed, `/Subtype` MIME type, `/Params` with size, dates and MD5
  checksum) with a file specification (`/F` and `/UF`, `/Desc`, `/AFRelationship`) in the `/EmbeddedFiles` name tree
  and in the catalog's associated files (`/AF`). PDF/A-2b rejects attachments; PDF/A-3b allows any file.
- Factur-X 1.0 / ZUGFeRD 2.x e-invoices: `document.AttachFacturXInvoice(xml, FacturXProfile.EN16931)` attaches the
  CII invoice XML as `factur-x.xml` (`xrechnung.xml` for XRECHNUNG) with the relationship the profile needs (`Data`
  for MINIMUM and BASIC WL, `Alternative` otherwise), makes the document PDF/A-3b and writes the `fx:` XMP properties
  (document type, file name, version, conformance level) with their PDF/A extension schema. Profiles MINIMUM,
  BASIC WL, BASIC, EN 16931, EXTENDED and XRECHNUNG. Validated with veraPDF in CI.
- Reading existing PDFs: `PdfReader.Open` (file, stream or bytes) parses classic cross-reference tables,
  cross-reference streams, object streams, hybrid files and incremental updates (the newest revision wins), and
  repairs damaged files by scanning for objects when the cross-reference data is missing or wrong
  (`PdfReader.IsRepaired`). Streams decode with Flate and LZW (with PNG and TIFF predictors), ASCIIHex, ASCII85 and
  RunLength; image codecs are passed through untouched. `PdfReader.Pages` lists `PdfReaderPage`s with their inherited
  resources, media and crop boxes and rotation resolved; `GetContent()` returns a page's decoded content. Reference
  loops, `/Prev` loops and page tree loops are cut, nesting depth and decoded stream sizes are limited, and malformed
  input throws `PdfFormatException`.
- Reading encrypted PDFs (#111): `PdfReader.Open(..., password)` opens files of the standard security handler with
  the user or owner password (AES-256 revision 6, AES-128 and RC4 revision 4, RC4 128-bit revision 3 and 40-bit
  revision 2), trying the empty user password when none is given. Strings and streams are decrypted as objects load,
  following the crypt filters (`/StmF`, `/StrF`, `/Identity`, a stream's own `/Crypt` filter) and leaving the
  encryption dictionary, cross-reference streams, the objects inside object streams and unencrypted metadata alone;
  imported and drawn pages come out decrypted. A wrong password throws `PdfPasswordException`; `IsEncrypted`,
  `IsOpenedWithOwnerPassword` and `Permissions` report what was found. Other security handlers (certificates) and
  revision 5 throw `NotSupportedException`.
- Merging: `PdfDocument.ImportPages` appends pages of a read file (all, or chosen ones in any order) and
  `InsertPages` inserts them anywhere; `InsertPage` adds a new page anywhere, e.g. a cover. The reachable objects are
  deep-copied once per source file, so fonts and images shared by pages (also across several imports and
  `DrawPage`) are written once, and streams keep their data and filters. `PdfImportOptions`: annotations are kept
  by default, with links within the file pointed at the imported pages (named destinations made explicit, links to
  pages left out dropped); `KeepOutline` adds the file's bookmarks to `PdfDocument.Outline`. More can be drawn on
  imported pages (e.g. page numbers), in a form XObject of its own over the original content; positions on them are
  measured from the media box.
- `PdfCanvas.DrawPage` draws a page of a read file as a form XObject, at its size or scaled into a rectangle, as it
  is displayed (crop box, `/Rotate`), e.g. a letterhead under generated content. `PdfRectangle` holds page boxes.
- Resource limits for untrusted input (`ResourceLimits`): nesting depth, element and table cell counts, image pixels
  (checked from the header, before decoding), resource, font and decompressed stream sizes, pages and time. Exceeding
  one throws `ResourceLimitExceededException`, naming the limit. The defaults apply everywhere (image decoding, WOFF
  and WOFF2, the PDF reader, layout); `limits.Enforce(cancellationToken)` applies others to the work in its scope, with
  a time limit and cancellation checked between pages, blocks, table rows and lines. Deep nesting stops with that
  exception instead of overflowing the stack. PNG data is inflated only as far as the image needs.
- Fuzzed (#53): the HTML/CSS converter (plain, tagged, PDF/UA, PDF/A and streamed output), the image decoders, the font
  parsers (OpenType, TTC, WOFF, WOFF2) and the PDF reader, nightly with libFuzzer and locally with a managed mutation
  fuzzer (`tests/RaiCore.Pdf.Fuzz`). Malformed input now fails only with the documented exceptions: corrupt zlib and
  Brotli data, out-of-range WOFF2 values and short `head` tables throw `FontFormatException`, fonts without a `name`
  table embed, and zlib errors in PDF streams and PNG data no longer escape. Every input fuzzing found is replayed by
  the unit tests.

- Tagged PDF and PDF/UA-1: `PdfDocument.Tagged`/`PdfUA`, structure elements (`PdfDocument.StructureRoot`,
  `PdfStructureElement` with alternate and actual text, language, table scope and spans, list numbering;
  `PdfStructureTypes`), `PdfCanvas.BeginTag`/`BeginArtifact`/`EndTag`. Marked content is written flat and only around
  path and painting operators, with its own MCIDs in each transparency group's form; untagged drawing becomes an
  artifact; links are tagged (`/OBJR`, `/StructParent`, `PdfLink.Description` as `/Contents`); the parent tree,
  `/MarkInfo`, `/Tabs /S`, `/DisplayDocTitle` and `pdfuaid:part` are written. PDF/UA checks the title, language,
  XMP and embedded fonts when saving. Untagged documents are unchanged.
- Bounding boxes for tagged figures, formulas, forms and tables (#123): each one drawn on a single page gets the
  Layout attribute `/BBox`, measured from what is painted for it and its descendants (exact curve extents, strokes,
  text, images, imported pages and gradients, cut to the clip, through transformations and transparency groups), so
  core, Layout and HTML output all get it. `PdfStructureElement.BoundingBox` sets one by hand, for any element.
- Deterministic output (#124): `PdfDocument.TimeProvider` and `HtmlConverterOptions.TimeProvider` supply the dates
  that are not set (creation, modification, attachments), so the same input and a fixed clock give the same bytes,
  also page by page, on other threads and in other cultures and time zones. CI compares samples written on Linux,
  Windows and macOS.
- Imported pages in tagged documents (#127): a page taken with `ImportPages` keeps its own content as an artifact, and
  what is drawn on it is tagged in the form it goes in (`/StructParents` on the form, `/Stm` in the references), so
  tagged and PDF/UA documents can start from letterheads and other PDF pages.
- Strict tagging (#123): `PdfDocument.StrictTagging` makes painting outside both a structure element and an artifact
  throw (naming the page) instead of quietly becoming an artifact. Layout and HTML output are tested under it.
- Writing page by page: `PdfDocument.BeginSave(stream)`, `FlushPages()` (writes the pages so far and frees their
  content) and `EndSave()`. Fonts, page dictionaries, links, outline, named destinations, attachments, the structure
  tree and metadata are written at the end, so links may point at any page; tagged, PDF/UA and PDF/A documents can be
  written page by page too. `Save` writes each page's content as it goes too; object numbers changed
  (fonts and page dictionaries now follow the content).
- `PdfDocument.SaveAsync(stream, cancellationToken)`: encodes page by page into a buffer and writes it with
  `WriteAsync`, so streams that forbid synchronous writes (an ASP.NET Core response body) work.
- `PdfException` is the base class of every exception the library throws for input it rejects: `PdfFormatException`,
  `PdfPasswordException`, `FontFormatException`, `ImageFormatException` and `ResourceLimitExceededException`.
- `PdfPageSize.FromInches`.

### RaiCore.Pdf.Fonts
- Bundled Liberation Sans, Serif and Mono (SIL OFL 1.1), metric-compatible with Arial, Times New Roman and Courier New.
- `StandardFontSubstitutes`: the Liberation face for each standard font (`Get`), and `PdfDocument.EmbedStandardFonts()`,
  which embeds them in place of Helvetica, Times and Courier (as PDF/A requires).
- `FontCollection` with CSS font matching, system font discovery and metric-compatible substitutes.
- Thread safety: `PdfFont`, `FontCollection`, `BundledFonts`, `PdfImage`, `TextShaper`, `Hyphenator`, `HtmlToPdf`,
  `RazorToPdf` and `RazorPdfRenderer` are thread-safe; documents, pages, canvases, flow documents and `PdfReader` are
  used by one thread at a time (see "Thread safety" in the README). Font data (the file's bytes, parsed tables,
  character map and HarfBuzz face) is shared by every document and thread; what a document uses of a font is kept per
  document. The bundled fonts are loaded once per process and shared by every collection, so conversions without a
  `FontCollection` no longer read and parse them each time; the faces of a `.ttc` file share one copy of its data.
  `FontCollection.AddFont(Func<PdfFont>, ...)` adds a lazily created font that can be shared between collections.
  Fixed: a font's character map could be seen without its symbol flag by a concurrent first use, and a hyphenation
  `Register` racing with lookups could leave them cached with the old answer.

### RaiCore.Pdf.Layout
- One namespace, `RaiCore.Pdf.Layout`, for blocks, `FlowDocument`, page setup and paragraphs; text processing
  (shaping, line breaking, bidi, hyphenation) is in `RaiCore.Pdf.Layout.Text`.
- One `Length` type (points plus a percentage, with an implicit conversion from a number of points) for widths,
  margins, padding, insets and radii: `BoxStyle.Width`, `MinWidth` and `MaxWidth` and `Thickness` take percentages
  directly, and `PageSetup.Margins` may hold percentages of the page's width or height.
- Experimental APIs (`docs/experimental.md`): the PDF object model (`RCPDF001`: `RaiCore.Pdf.Objects`,
  `PdfReader.Trailer`/`Catalog`/`Resolve`, `PdfReaderPage.Dictionary`/`Resources`/`GetContent`) and text shaping
  (`RCPDF002`: `TextShaper`, `ShapedText`, `ShapedRun`, `ShapingOptions.Features`, `LineBreaker`,
  `PdfCanvas.DrawGlyphs`, `PositionedGlyph`) need an opt-in and may change in minor versions.
- `TextStyle.Hyphenator` sets a style's hyphenation patterns, and `Hyphenator.ForLanguage(language, hyphenators)`
  picks from your own by language before the bundled ones. There is no process-wide registry.
- The package includes the Unicode License v3 notice (`licenses/unicode.txt`) for its bidi and line break tables,
  generated from the Unicode Character Database, and its license expression names `Unicode-3.0`. All third-party
  material is listed in `THIRD-PARTY-NOTICES.md`.
- HarfBuzz text shaping (kerning, ligatures, Arabic joining, Indic reordering, mark positioning) with per-character
  font fallback.
- Unicode line breaking (UAX #14, passing the full conformance test) and paragraphs with alignment, justification,
  line heights, indents, soft hyphens and hard breaks; inline images and inline boxes (`Paragraph.AddBox`: any block
  as one item of a line, shrink-to-fit, aligned on its last baseline), leaders (`Paragraph.AddLeader`: a pattern
  repeated to fill the rest of the line, as in a table of contents); `InlineAlignment` positions inline text, images
  and boxes vertically (CSS 2.1 section 10.8); `Paragraph.BeginInline`/`EndInline`/`AddInline` give part of the text an
  inline element's box (`InlineDecoration`).
- Hyphenation: `TextStyle.Hyphens` (`Manual`, the default: only at soft hyphens; `None`: never, not even at soft
  hyphens; `Auto`: also where the hyphenation patterns of `TextStyle.Language` allow), `HyphenateCharacter` (U+2010 by
  default, or the hyphen-minus when the font lacks it) and `HyphenationLimits` (the shortest word, and the fewest
  characters before and after a hyphen; by default 5 and the language's own minimums). A hyphenation point is a break
  opportunity whose hyphen, shaped in the text's font, counts when fitting and justifying the line, and in min-content
  widths. Words with a soft hyphen are hyphenated only there, and the style of a word's first letter decides for the
  whole word. `Hyphenator` implements Liang's algorithm (TeX's) with patterns and exceptions; patterns for en-US,
  en-GB, de, da, nl, fr, es, it, pt, pl, nb and nn are bundled from hyph-utf8 (compressed, loaded on first use; MIT,
  BSD-3-Clause or all-permissive licences, listed in `Text/Hyphenation/Patterns/LICENSES.md` and packed as
  `licenses/hyphenation-patterns.md`), language tags fall back to shorter ones (`de-CH` uses `de`), and
  `Hyphenator.FromPatterns` with `Register` adds other languages.
- Bidirectional text: the Unicode Bidirectional Algorithm (UAX #9, passing the full `BidiTest.txt` and
  `BidiCharacterTest.txt` conformance tests) orders mixed left-to-right and right-to-left text on every line, after
  lines are broken in logical order. `Paragraph.Direction` sets the base direction (left to right, right to left, or
  automatic from the first strong character); `TextAlignment.Start` (now the default) and `End` follow it, and the
  first-line indent and the last line of justified text are on the start side. Embedding, override and isolate
  controls in the text are honored and not drawn; right-to-left runs are shaped right to left (mirroring brackets),
  numbers keep their order. An inline element's box or a link split by reordering gets one part per visual piece,
  with the element's edges where CSS 2.1 section 8.6 puts them (`BeginInline`'s `direction`). Text is still drawn in
  logical order, so extraction reads it as written. `TextShaper.Shape` takes an optional direction.
- Right-to-left tables and flex containers: `TableBlock.RightToLeft` puts the first column (of the cells, spans and
  `ColumnWidths`) on the right, mirroring the grid so collapsed borders and splitting work unchanged;
  `FlexBlock.RightToLeft` starts rows on the right (`RowReverse` then runs left to right) and the cross axis of
  columns on the right (alignment and wrapped lines). In a reversed or right-to-left row, the item margin on the
  main-start side (the right one) now comes first.
- Block layout and pagination: the box model, margin collapsing, orphans and widows, forced and avoided page breaks,
  boxes sliced across pages, page decorators for headers and footers. A paragraph whose orphans and widows leave no
  possible break needs all its lines at the bottom of a page before it starts there.
- A page setup per page: `FlowDocument.PageSetupResolver` chooses each page's size and margins from a `PageContext`
  (page number, `IsFirst`, page name, `PageSide` and whether it is a blank page), and `PageInfo` gives decorators the
  page's own setup, name, side and blankness. `Block.PageName` names the pages a block goes on: a change of name
  between siblings forces a page break, a container starts with its first child's name, and pages created by
  automatic breaks keep the name. `PageBreak.Left` and `PageBreak.Right` start a page on that side, inserting a blank
  page when needed (before the first content they set the first page's side). Blocks laid out on a page with another
  content width get that width through containers without a width of their own (backgrounds follow on every page),
  and pages with another left margin shift their content (links included).
- `height`, `min-height` and `max-height` on every block (`BoxStyle.Height`, `MinHeight`, `MaxHeight`). Content
  taller than a fixed height overflows, as with `overflow: visible`; a fixed-height box that fits on a page is not
  split.
- `min-width` and `max-width` on every block (`BoxStyle.MinWidth`, `MaxWidth`, as border-box widths), applied by the
  layout engine to fixed, filling and shrink-to-fit widths alike (inline boxes, tables, flex items in rows and
  columns) and to intrinsic widths; the minimum wins over the maximum, as in CSS. Images scale to them, keeping their
  aspect ratio unless both a width and a height are given; a table without a width does not get narrower than its
  content. They replace `FlexItem.MinWidth`/`MaxWidth`.
- Percentage sizes (`Length` in `BoxStyle.Width`, `MinWidth`, `MaxWidth`, `Margin` and `Padding`): a width, min/max
  width, margins and padding of points plus a percentage of the containing block's width, resolved in layout against
  the width that block actually gets: a flex item after it grew or shrank, a table column, a shrink-to-fit float or
  inline box, an absolute box's containing block. Min-content and max-content widths treat a percentage width as
  auto, and an image with a percentage width or max-width can shrink with its container (CSS Sizing 3 section 5.2).
  `FlexBasis.Percent` also applies to rows.
- Flex shrinking weighs items by their content box (#132): the negative free space is split by flex-shrink times the
  inner base size (CSS Flexbox section 9.7), so items with padding or borders shrink as in browsers.
- Inline images sized relative to their paragraph (#112): `InlineImageSizing` (`InlineImage.Sizing`,
  `Paragraph.AddImage(..., sizing)`) keeps a percentage width, min-width or max-width until layout, so an inline
  `<img style="max-width: 100%">` or `width: 50%` follows a flex item that shrinks or a table column, like a block
  image. Such images add nothing to min-content (beyond a fixed minimum) and their natural size to max-content.
- Tables: automatic and fixed layout, `colspan`/`rowspan`, separate and collapsed borders. Header rows repeat at the
  top of every page and footer rows (`TableRow.IsFooter`) right below the body; footers taller than a quarter of the
  page are not repeated, as in Chrome. Rows that do not fit are split like browsers do, each cell continuing on the
  next page in its own column with backgrounds and borders sliced; rows joined by row spans may also break between
  them, with the spanning cell continuing alongside. `TableRow.BreakInside` keeps a row whole when it fits on a page.
- Flexbox (`FlexBlock`, `FlexItem`): all directions, wrapping (including `wrap-reverse`), `justify-content`,
  `align-items`/`align-self` (`stretch`, `flex-start`, `flex-end`, `center`, `baseline`), `align-content`, gaps,
  `order`, grow/shrink/basis with min/max sizes and the automatic minimum size, and auto margins. Containers with a
  definite or minimum height flex their columns vertically and wrap into side-by-side columns; a flex item's flexed or
  stretched size is definite for a flex container inside it. Rows taller than the space left on a page are split like
  browsers do, each item continuing in its own column and stretched items running down to the end of the row.
- Border styles (`BorderStyle` on `BorderSide`): solid, dashed, dotted, double, groove, ridge, inset and outset, drawn
  like browsers draw them, with sides of different colors or styles meeting on the corner diagonals. Collapsed table
  borders resolve conflicts as in CSS 2.1 (`hidden` wins, then the wider border, then the style) and keep their style.
- Rounded corners (`BoxStyle.BorderRadius`, `CornerRadii`): circular or elliptical radii in points, percentages of the
  border box or both, with overlapping radii scaled down as CSS specifies. Backgrounds, borders in every style (sides of
  different widths and colors meet on the corner diagonals; dashes and dots run around the curves, and translucent ones
  are painted once where sides meet) and the images of
  image blocks and inline images (`InlineImage.BorderRadius`) follow them. A box split across pages is rounded only at
  its own top and bottom, an inline element only at its first and last line; tables with collapsed borders are not
  rounded. `BoxStyle.BackgroundClip` paints the background over the border, padding or content box.
- Text decorations (`TextStyle.Decorations`): underline, overline and line-through, solid, double, dotted, dashed or
  wavy, with their own color, thickness and underline offset. Each is drawn with the metrics of the font that declared
  it (the `post` and `OS/2` tables; `PdfFont.UnderlinePosition`, `StrikeoutPosition` and their thicknesses) as one
  continuous line across adjacent text.
- Letter and word spacing (`TextStyle.LetterSpacing`, `WordSpacing`): added after every character (a character with
  its marks, or a ligature) and word separator, included in measuring, line breaking and intrinsic widths, and
  combined with justification. Letter spacing turns optional ligatures off and leaves cursive scripts such as Arabic
  unspaced, as browsers do.
- Bookmarks: `Block.Bookmark` makes a block an entry in the PDF outline. Entries nest by level and point at where
  the block starts (where its content starts, if that moved to a later page), also inside flex items and table cells.
- Links: `LinkTarget` (a URI, or a destination id), `Paragraph.BeginLink`/`EndLink`/`AddLink` for links in text (one
  rectangle per line, as high as the line, on the page each line is drawn on; `LineLayout.Links` gives the pieces)
  and `Block.Link` for whole blocks (their border box on every page they continue on). `Block.Id` and
  `Paragraph.AddAnchor` (the top of the line it falls on) become the PDF's named destinations when rendered; the first
  one with an id wins. Links work in flex items, table cells, inline boxes and page decorators.
- Opacity: translucent colors (text, backgrounds, borders, decorations) are painted with real alpha, and
  `BoxStyle.Opacity` draws everything a block paints (background, borders, content and descendants) as one
  transparency group per page it is on, also in flex items, table cells and inline boxes. `InlineImage.Opacity` (and
  the `opacity` argument of `Paragraph.AddImage`) does the same for an image in a line. Translucent dashed and dotted
  borders are painted once where adjacent sides' dashes meet in a corner.
- Overflow (`BoxStyle.ClipOverflow`): a block's content (also its floats and positioned children, which paint in later
  layers) is clipped to its padding box, with the inner rounded corners, on each page of a box split across pages. A
  clipped box of a fixed height that fits on the page is laid out in one piece, so content it hides makes no pages; a
  taller one ends at its height. Drawing scopes now nest clips with opacity groups. `Paragraph.Ellipsis` cuts a line
  too wide for its room at the last grapheme where "…" still fits (CSS `text-overflow: ellipsis`).
- A fixed height (or min-height) that reaches below the page now continues on the next pages, splitting the box,
  instead of running off the page.
- Background layers (`BoxStyle.BackgroundLayers`): images (`ImageBackground`) and gradients (`LinearGradient` at an
  angle or toward a corner, `RadialGradient` with circle or ellipse, extent keywords or radii and a center; both
  repeating if wanted) over the background color, the first on top. Each layer is sized (`BackgroundSize`: auto,
  cover, contain or lengths), positioned from either edge of its origin box (`BackgroundPosition`), repeated
  (`BackgroundRepeat`: repeat, no-repeat, space, round) and painted in its clip box, rounded corners included. A box
  split across pages is painted as if whole. Gradients are PDF shadings painted with `sh` (so they follow relative
  offsets and other transformations); translucent stops get a soft mask. Repeated images are stored once and drawn
  per tile (up to 10,000 tiles per fragment).
- Positioning (`BoxStyle.Position`, `Insets`, `ZIndex`): a relative block is laid out in the flow and drawn moved by
  its insets. An absolute block leaves the flow and is placed in its containing block (the padding box of the nearest
  positioned ancestor, or the page area of the page it is on) once that is laid out: its width from its own, from a
  left and a right inset, or shrink-to-fit; its height from its content or from a top and a bottom inset; auto insets
  keep where it would have been in the flow; percentages are of the containing block. It is laid out as one piece, on
  the page its containing block starts on (or ends on, for a bottom inset). A fixed block is placed in the page area of
  every page. Positioned blocks are painted over the flow and floats, ordered by z-index (a negative one goes under
  the flow), with one stacking order per page.
- Floats (`BoxStyle.Float`, CSS 2.1 section 9.5): a floated block is taken out of the flow and placed as high as it
  can at its place and as far to its side as earlier floats allow (moving down until it fits), without collapsing
  its margins; without a width it shrinks to fit. Text in the same formatting context flows around it: each line is
  broken to the room beside the floats where it is, and moves down past them when its first word does not fit.
  `BoxStyle.Clear` moves a block (also an empty one) below earlier floats, the clearance replacing its top margin.
  Tables, flex containers, images and blocks with `BoxStyle.ContainsFloats` go beside floats or below them; those
  blocks, floats, flex items and table cells contain their own floats (they grow to hold them, and their margins do
  not collapse with their children's). A float that does not fit on the rest of the page moves to the next one with
  what follows it; one that continues on later pages keeps the text there flowing around it. Floats are painted over
  the backgrounds of the blocks around them, as in CSS.

- Tagging: `Block.Tag`, `TableCell.Tag` and `Paragraph.BeginTag`/`EndTag` (`StructureTag`) give layout content its
  structure elements, created in document order when rendering into a tagged document; paragraphs, images and tables
  get default tags, table rows their row groups, and links in text Link elements. Backgrounds, borders, text
  decorations, repeated table header and footer rows and the page decorator are artifacts.
- Faster layout: paragraph layouts (by width), intrinsic widths and short shaped texts are cached for one layout pass
  (never across threads or documents; the output is byte for byte the same), clusters' scripts are looked up once per
  code point, one-character glyph texts are shared, and long tables no longer scan every cell on every page.
  `FlowDocument.RenderTo` flushes each page as soon as it is drawn into a document written page by page.

### RaiCore.Pdf.Html
- A `ResourceLoader` gets a `ResourceRequest`: the `Uri`, the `Kind` of resource (style sheet, image or font) and a
  `CancellationToken` canceled with the conversion or at its time limit. `ResourceLoaders.Default` loads `data:` and
  `file:` URIs.
- `HtmlToPdf.ConvertToStreamAsync` writes the stream only asynchronously, in chunks of up to 64 KiB while the
  conversion runs on its own thread, so it can be an ASP.NET Core response body. A slow stream makes the conversion
  wait; a failing one stops it and its exception is thrown. `HtmlToPdf.ConvertToBytesAsync` returns the bytes.
- `HtmlConverterOptions.Hyphenators`: hyphenation patterns by language (matched like `lang`, so `de` covers `de-CH`),
  for languages without bundled patterns or in place of them.
- `rgb()`/`rgba()` colors whose alpha has a unit (`rgb(1 2 3 / 4px)`) no longer make the conversion throw
  (AngleSharp.Css throws on them while parsing; fuzzing found it): the declaration is dropped, as CSS requires.
- HTML and CSS to PDF via AngleSharp: the cascade, inheritance, `!important`, custom properties and print media
  queries, with browser default styles.
- Block and inline formatting, white-space handling, the box model, fonts, colors, text alignment and transforms,
  list markers, images, linked stylesheets and tables (including the legacy table attributes, `thead`, `tfoot` and
  `tr { break-inside: avoid }`).
- SVG in `<img>` (files and `data:` URIs, URL-encoded or base64) and inline `<svg>` elements, sized and placed like
  images by CSS `width`/`height` or the element's attributes; `currentColor` follows the element's CSS `color`.
- `display: flex`, with all the flex properties above.
- Backgrounds, borders, padding and horizontal margins on inline elements: drawn behind the text of each line they
  cover (the font's content area plus padding and border, raised with the element), sliced at line breaks and around
  blocks inside them; horizontal edges take space on the line, vertical ones do not change its height.
- `vertical-align` on inline content (text, images, inline boxes): `sub` and `super` (a fifth and a third of the
  parent's font size, as browsers do; `<sub>`/`<sup>` use them by default), lengths, percentages of the line height,
  `middle`, `text-top`, `text-bottom`, `top` and `bottom`. Nested shifts add up; a block's or table cell's own
  `vertical-align` does not move its text.
- `inline-block`, `inline-flex` and `inline-table` as atomic inline boxes: one unbreakable item of the line with its
  own padding, border, background and width (or shrink-to-fit), aligned on the baseline of its last line of text (the
  first row for tables, the first baseline for flex containers, the bottom margin edge without text).
- `text-decoration` (and its `-line`, `-style`, `-color` and `-thickness` longhands) and `text-underline-offset`;
  `<u>`, `<ins>`, `<s>`, `<del>` and `<strike>` by default. Decorations propagate to all inline content, cannot be
  removed by descendants, skip atomic inline boxes and running elements, and are drawn with the font, size and
  baseline of the element that declared them, as browsers do.
- `letter-spacing` and `word-spacing` (lengths, including negative ones).
- `border-style` and `border-{side}-style` (also through the `border` shorthands), including `hidden` in collapsed tables.
- The legacy table `border` attribute draws as Chrome does: a gray outset border on the table and a 1px gray inset
  border on each cell, both replaced by any border the CSS sets.
- `border-radius` and its longhands: one to four values, the elliptical `/` form, lengths, percentages and `calc()`, on
  blocks, flex items, table cells, inline elements and images (an `<img>` is clipped to its rounded content box); and
  `background-clip`.
- Generated content: `::before` and `::after` (also the legacy `:before`/`:after`), inserted as the element's first
  and last child in blocks, inline elements, list items, table cells and flex containers. They have their own styles
  (inheriting from their element): inline with an inline box for background, border and padding, `inline-block` as
  atomic inline boxes, or block-level. `content` takes strings with CSS escapes, `attr()`, `open-quote`/`close-quote`
  (with `quotes`; by default double quotes, then single quotes for nested ones), `no-open-quote`/`no-close-quote` and
  `url()` images; `none` and `normal` generate nothing. `<q>` elements get quotes from the browser default styles.
- CSS counters (CSS Lists 3): `counter-reset`, `counter-increment` and `counter-set` with nested scopes (a reset on a
  sibling replaces the counter, one on a descendant nests a new one), computed in document order with `::before` and
  `::after`; `counter()` and `counters()` in every list-style counter style in generated content, `string-set` and
  `bookmark-label`. List items increment the implicit `list-item` counter (`ol`/`ul`/`menu` reset it, honoring
  `start`, `reversed` and `value`), and list markers show it.
- Page-context counters: `counter-reset`, `counter-increment` and `counter-set` in `@page` rules (cascaded like the
  other page properties, so named pages and `:first`/`:left`/`:right`/`:blank` rules apply to the pages they match)
  change the `page` counter (incremented on every page, blank pages included, unless named) and named counters, shown
  in margin boxes with `counter()`.
- Cross-references (CSS GCPM): `target-counter()` and `target-counters()` (the page, or any counter, of the element
  an `attr(href)` or `"#id"` points to), `target-text()` (its text, `::before`/`::after` or first letter) and
  `leader()`, for tables of contents with dotted leaders and right-aligned page numbers. Page references to later
  elements are resolved by laying the document out again until the pages stop changing (at most four layouts).
  `FlowDocument.MapPages` gives the pages (with their names and sides) and the page of each destination.
- `min-width` and `max-width` (also with percentages and `calc()`), applied in layout, so they also limit
  shrink-to-fit boxes (`inline-block`, tables, flex items) and the content inside them; a block narrowed by
  `max-width` is centered by auto margins. Images scale to them, and a declared `width` or `height` (even `auto`, as
  in `img { max-width: 100%; height: auto }`) overrides the `width`/`height` attributes, as in browsers.
- Percentages (also in `calc()`) in `width`, `min-width`, `max-width`, `margin`, `padding` and row `flex-basis`
  resolve against the width the containing block gets in layout, so the children of a flex item that grew or shrank,
  of a table cell, of a shrink-to-fit float or `inline-block` and of an absolute box follow its final width (issue
  #90). A block with a `width` centered by `margin: 0 auto` stays centered in that final width.
- `direction`, `unicode-bidi` (`embed`, `isolate`, `bidi-override`, `isolate-override`, `plaintext`) and the `dir`
  attribute (`ltr`, `rtl`, `auto`), `<bdi>` and `<bdo>`: right-to-left paragraphs start on the right, inline elements
  become the matching bidi embeddings, overrides and isolates, and `text-align: start`/`end` follow the direction.
  Right-to-left tables and flex containers run right to left; a box narrower than its containing block (a table or
  image that shrinks to fit, or a block with a `width`) goes to the containing block's start side, the right when
  that is right to left (CSS 2.1 section 10.3.3). `margin-inline-start`/`-end`, `margin-inline`,
  `padding-inline-start`/`-end` and `padding-inline` follow each element's direction, through the cascade (AngleSharp
  maps them as if everything were left to right, so they are carried in custom properties); lists and `<dd>` are
  indented with them, so right-to-left lists are indented on the right with their markers there.
- `height`, `min-height` and `max-height`; percentage heights resolve against definite ancestor heights, starting
  with the page area, and `vh` units are the page area (to within a pixel).
- `calc()`, `min()`, `max()` and `clamp()` in every length (sizes, margins, padding, `flex-basis`, gaps, `font-size`,
  `line-height`, `text-indent`, `vertical-align`, `letter-spacing`, `word-spacing`, `text-decoration-thickness`,
  `text-underline-offset`, table and `<col>` widths, `@page` size and margins): nested, with
  CSS variables, any mix of units, and percentages resolved against the containing block like plain percentages.
  Invalid expressions fall back to the property's initial value; results are clamped at 0 where lengths cannot be
  negative. Viewport units (`vw`, `vh`, `vmin`, `vmax`) follow the page area set by `@page`.
- CSS Paged Media: `@page` size (lengths, `landscape`/`portrait`, and the ISO A0-A10 and B0-B10, JIS-B4/B5, letter,
  legal and ledger keywords) and margins, `:first`/`:left`/`:right`, margin boxes for headers and footers,
  `counter(page)`/`counter(pages)`, `string-set` and running elements (which can be flex containers).
- Named pages and page-specific geometry: the `page` property (cascaded by specificity) with `@page name` rules, page
  selectors combining a name with `:first`, `:left`, `:right` and `:blank` (also in selector lists), cascaded by
  page selector specificity then source order, give each page its own `size` and margins (e.g. landscape pages for
  wide tables, mirrored margins); margin boxes use each page's size and margins. `break-before`/`break-after: left`,
  `right`, `recto` and `verso` insert blank pages, which `@page :blank` styles.
- Web fonts: `@font-face` with `url()` and `local()` sources, `format()` hints (unsupported formats are not
  downloaded), weight ranges, `font-style` and `unicode-range`, matched like CSS Fonts 4 and loaded on first use. A
  declared family hides installed fonts of the same name; a family that fails to load falls back to the next one.
  `PdfFont.WithUnicodeRanges` restricts a font to code point ranges, embedding it once however many views use it.
- PDF bookmarks from `h1`-`h6`, nested by level, customized with the CSS GCPM properties `bookmark-level`,
  `bookmark-label` (strings, `content()`, `content(first-letter)`, `content(before)`/`content(after)` and `attr()`)
  and `bookmark-state`, cascaded by specificity. `HtmlConverterOptions.CreateBookmarks = false` turns them off.
- Links: `<a href>` to URIs (`http(s):`, `mailto:`, `tel:` and others; relative URLs resolve against `BaseUri`, or
  are kept as written without one; `javascript:` and empty links are dropped) and `href="#id"` to the element with
  that `id` or an `<a name>` (block elements at the top of their box, inline ones at the line they start on). A link
  around blocks is clickable over their boxes; links to ids that do not exist are left out.
- Translucent colors keep their alpha (`rgba()`, `hsla()`, `#rrggbbaa`, `#rgba`, `transparent`) instead of being
  blended with white, and `opacity` makes an element a transparency group (including inline images); a translucent
  CSS `color` reaches `currentColor` in inline SVG.
- Hyphenation: `hyphens` (also `-webkit-hyphens`), `hyphenate-character` (also `-webkit-hyphenate-character`) and
  `hyphenate-limit-chars`, with the language from the nearest `lang` (or `xml:lang`) attribute; the language also
  reaches HarfBuzz for localized glyph forms. Text with `white-space: nowrap` or `pre` is not hyphenated, not even at
  soft hyphens.
- `overflow` (`hidden`, `clip`, `auto`, `scroll`: all clip on paper) and `text-overflow: ellipsis` (in a box that clips).
- `background-image` (`url()`, `linear-gradient()`, `radial-gradient()` and their repeating forms),
  `background-repeat`, `-position` (one to four values), `-size`, `-origin`, `-clip` and multiple layers. The
  `background` shorthand is split into its longhands before AngleSharp.Css parses it (it throws on several layers and
  resets `background-origin` wrongly), gradients toward a corner keep their corner, and `url()` in a linked stylesheet
  is relative to that stylesheet.
- `position` (`relative`, `absolute`, `fixed`; `sticky` is static), `top`/`right`/`bottom`/`left` and `z-index`:
  absolutely positioned elements are block-level and do not float, also spans and images in the middle of text (they
  leave the paragraph without splitting it). A relative element's percentages resolve against its containing block's
  width (and a definite height); an absolute one's against its containing block in layout.
- `float` (`left`, `right`, `inline-start`, `inline-end`) and `clear`: floated elements are block-level, also images,
  inline tables and spans in the middle of text (they go before their paragraph, without splitting it); flex items
  do not float. `display: flow-root` and `overflow` other than `visible`/`clip` start a new formatting context that
  contains its floats, and clearfix rules (`::after { content: ""; display: table; clear: both }`) work.
- Network access is off unless you supply a `ResourceLoader`.
- Document metadata from the HTML: `<title>`, the language from `<html lang>` (or `xml:lang`, or a
  `<meta http-equiv="content-language">` pragma), and `<meta name="author|description|keywords|generator">` as the
  author, subject, keywords and creator. `HtmlConverterOptions.Title`, `Author`, `Subject`, `Keywords`, `Creator` and
  `Language` override them.
- `HtmlConverterOptions.Encryption` encrypts the converted PDF (Razor conversion included).
- `HtmlConverterOptions.Tagged` and `PdfUA`: the structure from HTML semantics (headings, paragraphs, lists with labels
  and bodies, tables with header cells and scopes, figures with `alt` text, sections, quotations, links, `lang`).
  Validated with veraPDF (PDF/UA-1) in CI.
- `HtmlConverterOptions.PdfAConformance` writes PDF/A-2b or PDF/A-3b, with Liberation embedded for SVG text in the
  standard fonts.
- Styles are computed with one AngleSharp style collection instead of one per element, which made styling quadratic in
  the size of the document: a 10,000-row table converts in about 10 s instead of 208 s.
- `HtmlToPdf.ConvertToStream(Async)` writes the PDF to a stream page by page.
- Style sheets with rules AngleSharp cannot parse the selector of (such as `li::marker`) no longer fail the conversion.
- `HtmlConverterOptions.Limits` (resource limits, see RaiCore.Pdf): a document nested too deeply or with too many
  elements, table cells or pages, or that takes too long (parsing included), fails with
  `ResourceLimitExceededException`; an image, stylesheet or web font over a size limit is skipped like a broken one.
  `ConvertAsync` observes its cancellation token throughout. Conversions run on a thread of their own with a large
  stack, so deeply nested documents do not overflow it. `colspan`, `rowspan` and `<col span>` are clamped as HTML
  says (1000, 65534 and 1000).
- Hostile CSS no longer fails a conversion with an exception (found by fuzzing): numbers must be finite and lengths
  are clamped to 1,000,000 points (`flex-grow: 1e3082` looped forever), image sizes to 14,400 points, `@page`
  margins that leave no room are dropped, declarations and rules AngleSharp cannot read are removed and an element
  whose style it cannot compute takes its parent's (it threw `NullReferenceException` on them), `box-shadow` and `text-shadow` (not drawn) are ignored, invalid
  `font-feature-settings` are skipped, and web fonts whose license forbids embedding fall back to other fonts.
- `white-space: nowrap` and `pre` no longer wrap at hyphens, slashes, dashes or hyphenation points (#142): text with
  `TextStyle.NoWrap` has no soft wrap opportunities between its characters (a break at its edge belongs to the
  wrapping text, as in CSS), and keeps regular spaces instead of non-breaking ones, so copied text has spaces.
- `text-decoration` with a thickness in the shorthand (`underline wavy red 2px`, CSS Text Decoration 4) is split into
  its longhands before AngleSharp parses the CSS, which dropped the whole declaration (#137).
- Diagnostics (#59): `HtmlConverterOptions.Diagnostics` receives `HtmlDiagnostic`s for images (`img`, CSS `url()`),
  inline SVGs, linked style sheets and web fonts that did not load, telling apart nothing loaded (with a hint for
  relative URLs without a base URI), unreadable data, a per-resource limit and fonts that may not be embedded. Each
  issue is reported once per conversion, after rendering, with the first element's path
  (`html > body > div.card:nth-of-type(2) > img#logo`) and the number of elements it concerns; a web font only when
  none of its sources loaded. Unsupported CSS is reported as written (before the converter's rewrites), only where
  a rule applies to an element: properties it ignores (`Info` for those with no effect in print and vendor
  prefixes), values AngleSharp rejects and values laid out differently (`display: grid`, `contents`,
  `position: sticky`), pseudo-elements it does not generate and selectors it cannot match, and skipped at-rules
  (`@import`, `@layer`, `@container`; `@keyframes` as `Info`). Content that does not fit comes from the layout
  engine (`FlowDocument.Diagnostics`, `LayoutDiagnostic`): lines wider than their box (unbreakable words, images,
  inline boxes), tables and fixed-width boxes or images wider than their room (unless a box clips its overflow on
  purpose), lines taller than a page (cut off) and images scaled down to fit a page, each with the page it is on;
  a document from `CreateFlowDocument` reports them when it is rendered.

### RaiCore.Pdf.Razor
- Razor components to PDF with Blazor's static `HtmlRenderer`: `RazorToPdf` for the simple case, `RazorPdfRenderer`
  for components that inject services, and `AddRazorPdf()` to register it per scope.
- The component's `<html lang>`, `<title>` and `<meta>` elements become the document metadata as in RaiCore.Pdf.Html;
  the creator is `RaiCore.Pdf.Razor` unless the options or a `<meta name="generator">` name another.
- `RazorToPdf.ConvertToStreamAsync` and `RazorPdfRenderer.ConvertToStreamAsync` write the PDF page by page with
  asynchronous writes only, e.g. to an ASP.NET Core response body. `RazorPdfRenderer.CreateFlowDocumentAsync` takes a
  component `Type` too, and lays out on the conversion thread.

### Trimming, Native AOT and containers
- CI publishes the package smoke test trimmed and with Native AOT on Windows, Linux and macOS, runs it
  (`tests/PackageSmoke/publish-and-run.sh`) and fails on trim or AOT warnings other than the known ones in
  `Microsoft.AspNetCore.Components`. HarfBuzz's native library is published for the target runtime.
- CI also runs the package smoke test, trimmed and Native AOT publishing included, in Alpine (musl) and Debian
  containers on x64 and Arm64, and builds the sample Dockerfile in `samples/Docker` (an HTML-to-PDF app on Alpine).

### Known limitations
The [roadmap issues](https://github.com/raicevic/raicore-pdf/issues) track these.
- Borders on inline elements are drawn solid, unless they have rounded corners.
- `min()`, `max()` and `clamp()` with percentages in `border-radius` are evaluated against the box's width as far as it
  is known when the styles are read (plain percentages and `calc()` follow the painted box exactly).
- Percentages that are resolved when the styles are read, against the width the containing block is expected to
  have (so they do not follow a flex item that grew or shrank, a table column or a shrink-to-fit box): `min()`,
  `max()` and `clamp()` of a percentage in sizes, margins and padding; the size of an image in text (`<img>` is
  inline unless it is `display: block` or a flex item) and the `width` and `height` of a block image (its
  `max-width` and `min-width` follow); `text-indent`, gaps and `inset`s of relative boxes; a `flex-basis` with a
  percentage and content-box padding or borders; the auto margins of a block limited by `min-width` or `max-width`; the vertical padding of a box with a content-box `height`, `min-height` or `max-height` (which
  includes it); the margins of `inline-block` boxes; and the padding of table cells.
- Generated content: no `::marker`, `::first-line` or `::first-letter`; the legacy `:before`/`:after` count as a class
  in specificity (AngleSharp), not as an element. `counter(page)` in the document's own content (outside margin boxes)
  generates nothing; use `target-counter()`. Document counters are not in scope in margin boxes (they show 0; pass
  them with `string-set`). The `content` values AngleSharp drops (with `target-counter()`, `target-counters()`,
  `target-text()` or `leader()`) are cascaded without `!important`. `target-*()` only follow same-document `#id`
  URLs, and `target-text()` of a `::before`/`::after` further on leaves out quotes. Page references that keep
  changing after four layouts keep the last values. A leader is drawn once on a line that is already full, and only
  in its own font (a pattern needing font fallback is not repeated).
- Ids on table rows and cells are not link destinations (ids inside cells are), and neither are ids inside inline
  boxes (`inline-block`) or in running elements; links inside them work. The same goes for `target-counter(..., page)`. A link's clickable area is the line box
  (rectangles, not rotated with transforms).
- Positioning: z-indexes are not nested (one stacking order per page); absolute blocks do not break across pages; a
  relative or absolute inline element (a span in text) is not moved, and an absolutely positioned element in text
  keeps the static position of its paragraph's start; absolutely positioned children of a flex container are laid out
  as flex items; inside a table cell the containing block of an absolute block without a positioned ancestor in the
  cell is the cell's content.
- Grid (rendered as blocks). `overflow` clips both axes, even when only one is set; `text-overflow` takes only `clip`
  and `ellipsis` (no strings), at the line's end; content hidden in a clipped box taller than a page may still add
  pages at the end of the document.
- Backgrounds: `background-attachment` is ignored (`fixed` acts as `scroll`); color hints in gradients are dropped
  (the colors blend evenly); `image-set()`, `conic-gradient()` and `cross-fade()` are not supported; inline elements
  (a `span` in text) and collapsed table cells paint only their background color. `calc()` with percentages in a
  background position or size uses 0 for the percentage.
- Floats: a float in the middle of a paragraph's text starts at the paragraph's first line, not at the line it is
  on. A line is checked against floats at its strut's height, so a taller line (a large font or image) may overlap a
  float that starts lower down.
- `opacity` on inline elements (other than images and inline boxes) and on table rows and cells is ignored; where
  translucent collapsed table borders cross, they are painted twice.
- `unicode-bidi: isolate-override` in author styles (AngleSharp drops it; `<bdo>` works). The logical border
  properties (`border-inline-start` and the like) and `inset-inline` are left to right; in a right-to-left element a
  logical margin or padding written with `var()` is 0.
- Hyphenation fills lines greedily, like browsers (no Knuth-Plass paragraph optimization, no
  `hyphenate-limit-lines`, `hyphenate-limit-zone` or `hyphenate-limit-last`). Text in `::before`/`::after` has no
  language, so it is not hyphenated automatically. Patterns for languages under the LPPL alone (such as Swedish) or
  the GPL (Czech) are not bundled, and decomposed (NFD) text may miss hyphenation points.
- No synthetic bold or italic: a family without a bold or italic face uses its closest face as is.
- Razor components under trimming and Native AOT: `Microsoft.AspNetCore.Components` reports four IL2072 warnings and
  Microsoft does not support it with Native AOT; the smoke test's components work, but components found only by
  name at run time can be trimmed away.
- In globalization-invariant mode (no ICU, as in the Alpine runtime images), AES-256 passwords are not
  NFKC-normalized: .NET cannot normalize without ICU, so a password that is not already in NFKC (a decomposed `é`,
  a ligature) gives a key that viewers will not derive.
- Pages: no `@page :nth()`. Percentages, `vw`/`vh` and auto margins resolve against the content width of the
  element's named page (that of a right page that is not the first; unnamed pages use the plain `@page` rules), so
  `:first`/`:left`/`:right` pages with another content width do not change them. Content continuing onto a page with
  another content width (after an automatic break) keeps the width it started with, and `page` is ignored inside flex
  items, table cells and inline boxes. Page progression is left to right (`recto` is a right page). Inline elements
  cannot be bookmarks.
- PDF/A: only level B of parts 2 and 3 (no PDF/A-1, no levels A and U), always with the sRGB output intent, so
  CMYK JPEG images are rejected rather than converted. Pages imported from other PDFs are rejected, even when they are
  PDF/A themselves. Text in characters no font has fails under PDF/A instead of showing the missing-glyph box. Symbol
  and ZapfDingbats have no substitute. The nesting limit of 28 `q` levels is not checked.
- Tagging: `lang` on inline elements, abbreviations (`abbr` expansions), `figure`/`figcaption` grouping, description
  lists as lists, table `headers`/`id` associations, form fields and notes are not tagged yet; a `dl` is a sequence of
  paragraphs. Flex items are read in document order, not in their visual order (`order`, `row-reverse`). PDF/UA needs
  embedded fonts (a standard font only with a `StandardFontSubstitute`, such as `EmbedStandardFonts()`), and imported
  pages are not tagged.
- Factur-X / ZUGFeRD: the invoice XML is only checked to be well-formed with a CII root element
  (`rsm:CrossIndustryInvoice`); it is not validated against the profile's XML schema or business rules (use the
  official validators or Mustang for that), nor compared with the visible pages. Attachments are document-level
  associated files only: no file attachment annotations on pages and no portfolios.
- Layout time grows exponentially with the nesting of tables (each level measures and lays out the next more than
  once): 12 nested tables take about a second, 20 more than a minute, so a few hundred bytes of such markup run until
  the `ResourceLimits.Timeout` (5 minutes by default; lower it on servers). Nested `inline-block`s and flex
  containers are linear, and a 10,000-row table takes about 9 s.
- Metadata: custom property names that are not XML names are encoded (`_x0020_` for a space).
  Keywords and the author are plain text (`dc:creator` holds one entry). Language alternatives are written with
  `x-default` only, and the language is not checked against BCP 47. `HtmlToPdf.CreateFlowDocument` returns no
  metadata: set `PdfDocument.Info` and `Language` on the document you render it into. XMP in images and other
  objects is not written.
- Encryption: no RC4 (40/128-bit), no deprecated AES-256 revision 5 and no public-key (certificate) security. The
  SASLprep for AES-256 passwords is simplified: unassigned code points and the bidirectional rule are not checked.
  AES-128 passwords must be printable Latin-1. Encrypted output is not reproducible byte for byte (random keys, salts
  and IVs). Permissions are advisory, as in every PDF: viewers enforce them, but they are not a protection against
  a determined user who can open the file.
- Reading PDFs: encrypted files are rejected (`NotSupportedException`); decryption will follow encryption. Imports
  do not copy the source's interactive form (`/AcroForm`: widgets keep their appearance but are not fields of the
  new document), structure tree (tagged PDF), optional content configuration, named destinations, JavaScript or
  page labels; a page imported twice has its annotations on its first copy only. `DrawPage` draws a page's content
  without its annotations. Decoding is needed only for cross-reference and object streams and for pages drawn with
  several content streams, so the image filters (DCT, JPX, JBIG2, CCITT) are never decoded.
- Thread safety: fonts declared with `@font-face` are loaded per conversion (put fonts shared by many documents in the
  `FontCollection`); a loaded font's data stays in memory as long as the `PdfFont` (and a collection that found it) is
  referenced.
- Variable fonts are embedded at their default instance: CFF2 fonts are not instanced at other axis values (nor are
  TrueType variable fonts), so `font-weight` or `font-variation-settings` cannot select another instance of one
  variable font file. CFF glyphs built with `seac` (accented glyphs from old Type 1 conversions) and charstrings using
  the arithmetic operators make the font embed whole, as do fonts whose license asks not to be subset.
- SVG: no filters, masks, markers, patterns, `spreadMethod` `reflect`/`repeat` (drawn as `pad`), text on a path or
  per-character positions (only the first `x`/`y`/`dx`/`dy`), and no web fonts in SVG text (the standard fonts are
  used). PDF shadings have no alpha: a gradient whose stops differ in opacity takes its most opaque stop's alpha and
  blends the others with white towards it (exact on white only). `opacity` on a `tspan` is ignored. A clip path made of
  several shapes uses the first one's clip rule; text in clip paths is ignored. HTML style sheets do not style
  the inside of inline `<svg>` (its own `<style>` and attributes do).
- Writing page by page saves the memory of the drawn content only: `FlowDocument` and HTML lay out every page before
  drawing the first (page counts, `target-counter()`), so their peak memory is the layout's and hardly changes.
  Pages imported from other PDFs keep what they copy until `EndSave`.

[Unreleased]: https://github.com/raicevic/raicore-pdf/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/raicevic/raicore-pdf/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/raicevic/raicore-pdf/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/raicevic/raicore-pdf/releases/tag/v1.0.0

# CSV, XLSX and XLS to PDF

`RaiCore.Pdf.Sheets` converts CSV files and Excel workbooks to PDF: tables laid out by the layout engine, with header
rows repeated on every page, column widths fitted to the content within the page width, and numbers right-aligned.
Workbooks keep their number formats, styles, merged cells and page setup ([Excel workbooks](#excel-workbooks) below).

```sh
dotnet add package RaiCore.Pdf.Sheets
```

[!code-csharp[](../snippets/Sheets.cs#convert)]

`CsvToPdf.Convert` takes a path, a stream or bytes; `ConvertAsync` (on the thread pool, with a cancellation token)
and `ConvertToBytes` do the same.

The [format support](format-support.md) page compares all formats side by side.

## Reading CSV

The data is read as RFC 4180 describes it, and as spreadsheet programs read what does not follow it:

- **Records** end at CRLF, LF or CR. A field that starts with a double quote may hold delimiters, line breaks and
  doubled quotes (`""` is one quote). Rows may have different numbers of fields: short rows get empty cells.
- **The delimiter** is detected unless `Delimiter` names it: a first line `sep=;` (as Excel writes it) names it and is
  not shown; otherwise it is the one of comma, semicolon, tab and vertical bar that splits the first records most
  consistently into more than one field.
- **The encoding** is detected unless `Encoding` names it: a byte order mark (UTF-8, UTF-16, UTF-32) decides, even
  over the option; otherwise UTF-16 if every other byte is zero, UTF-8 if the bytes are valid UTF-8, and Windows-1252
  (what Excel writes on Western Windows systems) if they are not. Other code pages:
  `CodePagesEncodingProvider.Instance.GetEncoding(1250)`, which also works with invariant globalization.
- **Malformed data** does not fail the conversion: a quoted field whose closing quote is followed by other text, or that
  is never closed, is read as an unquoted field (so one stray quote cannot swallow the rows after it), and bytes the
  encoding cannot decode become U+FFFD. Control characters are left out and tabs shown as spaces.

## Numbers, dates and alignment

Cells that hold a number are right-aligned, as in a spreadsheet: digits with a sign or in parentheses, thousands
separators (`,` `.` `'`, spaces), a decimal part, an exponent, and a currency or percent sign (`$1,234.50`,
`-12,5 %`, `(300.00)`, `1.5e-3`). So are dates and times (`2026-11-19`, `19.11.2026`, `11/19/26`, `08:30`,
`8:30 PM`). Other text is aligned to the start of its direction: left, or right for Hebrew and Arabic. The header of
a column of mostly numbers is right-aligned too. Values are shown as written; nothing is reformatted.

The decimal separator tells numbers from text (`1.234,56` is a number only with a decimal comma). `DecimalSeparator`
sets it; otherwise it is the one the numbers in the data need, and where they do not tell (`1,234` could be either), a
comma for semicolon-delimited data and a point otherwise.

## Column widths and pages

The table is as wide as its content needs, up to the page's content width:

1. When every column fits with its widest cell on one line, each column gets that width.
2. Otherwise columns whose content fits in an equal share of the width keep their width, and the others share the
   rest in proportion to how much text they hold, wrapping between words. A column is widened for its widest word up
   to about twenty characters; longer words (identifiers, links) are broken when there is no room. Numbers and dates
   are never broken.
3. When the columns cannot all get a readable width (about six characters, and their widest number), they are split
   into bands of consecutive columns that each fit the page width. The rows are printed once for each band, one band
   after the other, each starting a new page with its part of the header row.

Text is never clipped. Rows taller than the space left on a page continue on the next one, and the header row repeats
at the top of every page (unless `HasHeaderRow` is `false`).

## Options

[!code-csharp[](../snippets/Sheets.cs#options)]

| `CsvConverterOptions` | What it does |
|---|---|
| `Delimiter` | The field delimiter; detected when `null` (the default). |
| `Encoding` | The text encoding; detected when `null` (the default). |
| `HasHeaderRow` | The first record holds the column names: bold, shaded and repeated on every page (default `true`). |
| `DecimalSeparator` | The decimal separator of the numbers; detected when `null` (the default). |
| `Sheet` | How the table is laid out, below. |

| `SheetOptions` | What it does |
|---|---|
| `Page` | Page size and margins (default A4, 1.5 cm margins). |
| `Landscape` | The page with its longer side across (default `false`). |
| `FitToWidth` | Shrink the text, down to `MinimumFontSize` (default 6), so that the columns fit without wrapping (default `false`). |
| `FontSize` | The font size in points (default 9). |
| `Fonts`, `FontFamily`, `FallbackFontFamilies` | The fonts: a collection (default the bundled Liberation fonts), a family or generic family (default `sans-serif`), and fallbacks for characters it lacks; Liberation Sans comes last. |
| `Gridlines` | Thin gray cell borders (default `true`). |
| `Title` | The document's title. |
| `Tagged` | A tagged (accessible) PDF: the table with header (`TH`) and data (`TD`) cells (default `false`). |
| `Limits` | The `ResourceLimits` of the conversion, below. |
| `Diagnostics` | Receives what was left out or simplified ([Diagnostics](#diagnostics) below). |

The returned `PdfDocument` can be changed before it is saved: metadata, `Language`, `PdfUA`, `PdfAConformance`,
encryption, attachments.

## Fonts

The bundled Liberation Sans covers Latin, Greek, Cyrillic and Hebrew. For other scripts, add a font that has them and
name it as a fallback:

[!code-csharp[](../snippets/Sheets.cs#fonts)]

## Untrusted data

`Sheet.Limits` bounds the conversion: `MaxTableCells` the cells (rows times the widest row's columns, checked while
the data is read, before it takes up memory), `MaxPages` the pages, and `Timeout` the time for reading, measuring and
laying out. Exceeding one throws `ResourceLimitExceededException` naming it. The time a conversion takes grows with
the number of cells: about half a second per thousand rows of eight columns.

Not supported in CSV: formulas, cell formats and styles (CSV has none), and other quote characters than the double
quote.

## Excel workbooks

`XlsxToPdf` converts `.xlsx` and `.xlsm` workbooks (also strict Office Open XML) with the same table layout. Each
sheet starts a new page; when more than one is converted, each gets a bookmark.

[!code-csharp[](../snippets/Sheets.cs#xlsx)]

`XlsxToPdf.Convert` takes a path, a stream or bytes; `ConvertAsync` and `ConvertToBytes` do the same.

### Values and number formats

Cells show what a spreadsheet program shows:

- **Numbers** in their number format, built-in or custom: `0`, `0.00`, `#,##0.00`, `0%`, `0.00E+00`, fractions
  (`# ?/?`, `# ?/16`), thousands scaling (`#,##0,"K"`), literal text and currencies (`"$"#,##0.00`, `[$€-407]`),
  sections for positive, negative, zero and text values (`#,##0;[Red](#,##0);"-"`), colors and conditions in brackets
  (`[Red]`, `[>=1000]`). General shows up to eleven characters, as a spreadsheet's default column does
  (`1.23457E+11`). Codes that cannot be read are shown as General.
- **Dates and times** from their serial numbers in either date system (1900, with its 29 February 1900, or 1904):
  `yyyy-mm-dd`, `dd/mm/yyyy`, `d mmmm yyyy`, `ddd`, `h:mm AM/PM`, elapsed time (`[h]:mm:ss`) and fractions of seconds
  (`mm:ss.0`).
- **Formulas** as their last calculated values, which the workbook stores; formulas are never calculated, so a
  workbook saved without calculated values shows them empty.
- **Text** from shared strings, inline strings and rich text (its runs joined, in the cell's font); booleans as
  `TRUE` and `FALSE`, and errors (`#DIV/0!`) as they are.

Output is the same on every machine: the invariant culture, with ISO 8601 short dates (`2026-11-19`) for the
built-in date format and the dollar for the built-in currency formats. `Culture` uses a culture's separators, month
and day names, short date and currency symbol instead.

### Styles and layout

- **Fonts:** bold, italic, underline (single, double), strikethrough, size and color. A cell's font family is used
  where `SheetOptions.Fonts` has it; otherwise a serif or monospace family for fonts known to be one (Times New
  Roman, Cambria, Courier New, Consolas) and `FontFamily` for the others. The bundled fonts include none of Excel's,
  so add system fonts for its look.
- **Fills** (solid; patterns as their average color), **borders** (hair to thick, dashed, dotted, double, in their
  colors) over the gridlines, horizontal and vertical alignment (bottom by default, as in a spreadsheet), wrapping and
  indents. Theme and indexed colors are resolved with their tints.
- **Merged cells** span their rows and columns, with the borders of the cells at their edges. Ranges cut by a band of
  columns or by the header rows are split there; the value shows in the first part.
- **Text that does not wrap** runs on over the empty cells beside it, as in a spreadsheet: to the right, to the left
  when right-aligned, to both sides when centered. Where the next cell is not empty, or the cell has a fill or
  borders, it wraps in its column instead (text is never clipped).
- **Column widths and row heights** are the sheet's: widths in characters of the default font's digits, so a column
  sized for eight digits holds eight digits in the font used. A column is widened for a number that would not fit
  (where a spreadsheet shows `####`). A sheet that sizes no column at all (as many generated workbooks) has its columns
  fitted to their content; `AutoFit` does that for every sheet, and ignores row heights too.
- **Hidden** rows, columns and sheets are left out; a sheet set to read from right to left is laid out that way.

### Pictures, shapes and charts

A sheet's drawings are drawn over its cells where Excel shows them:

- **Pictures** in PNG, JPEG, GIF, BMP, TIFF, WMF and EMF, cropped, turned and mirrored, clipped to their shape, with
  their border.
- **Shapes and text boxes** with their geometry (the preset shapes, as in presentations, and free forms), fill (solid;
  gradients and patterns as one color), outline (width, color, dashes) and text (fonts, sizes, bold, italic,
  underline, colors, alignment, insets, vertical anchoring, wrapping), the theme's line and fill where a shape has
  none of its own. Connectors are drawn as lines, without arrowheads.
- **Charts** in a sheet, by the same chart engine that draws the charts of presentations and documents, from the data
  cached in the workbook.
- **Groups** of all of these.

**Chart sheets** are printed in the workbook's sheet order, each on a page of its own: its chart (and whatever else its
drawing holds) stretched over the page's content area, as Excel prints a chart sheet on a full page; the chart is laid
out again at that size, so its text keeps its size. With `UsePageSetup`, the page is landscape unless the chart sheet's
page setup asks for portrait, and its headers and footers are printed as on worksheets. `SheetNames` and
`SheetIndexes` choose chart sheets like worksheets (indexes count every sheet). A chart sheet without anything that can
be drawn is left out and reported. The chart sheets of `.xls` workbooks hold BIFF charts, which are not drawn: they are
reported.

Anchors are resolved against the laid-out columns and rows, at the sheet's scale: a two-cell anchor moves and
stretches with the cells between its corners (an offset is never more than its cell, which may be narrower than in
Excel), a one-cell anchor keeps its size, an absolute anchor is placed at its distance from the top-left corner.
Hidden rows and columns take no room, as in Excel. A drawing that crosses a page break or a band of columns is split
there, each page showing the part over its own rows and columns, as Excel prints it; on the pages a table continues
on, the repeated header rows show no drawings. Without a print area, the printed range grows to take in the drawings.
Pictures with more pixels than `Limits.MaxImagePixels`, and those that cannot be read, are left out and reported.

In `.xls` workbooks, the OfficeArt drawing is read the same way: pictures from the workbook's picture store (PNG,
JPEG, DIB, TIFF, WMF, EMF), lines, rectangles, ovals and the other common shape types with their fill and line, text
boxes with their text, and groups, anchored in fractions of their cells. Charts (BIFF charts), free forms, form
controls and embedded objects are not drawn, and reported.

### Page setup

With `UsePageSetup` (the default), each sheet's page setup is used:

- **The print area** (`_xlnm.Print_Area`, its first range) is what is printed; without one, the range from `A1` to the
  last cell that shows a value, a fill or a border.
- **Print titles** (`_xlnm.Print_Titles`, rows to repeat at top) become header rows, repeated at the top of every page
  (also when they are above the print area). Rows before them are printed once, above them. Columns to repeat at left
  are not supported.
- **Landscape orientation**, the **print scale** (10% to 400%) and **fit to width** ("fit all columns on one page")
  apply to the sheet's pages. Fitting shrinks the sheet at most until its default font reaches
  `SheetOptions.MinimumFontSize`; beyond that, the columns are printed in bands. `SheetOptions.Landscape` and
  `FitToWidth` turn these on for every sheet.

The page size and margins always come from `SheetOptions.Page`. Also with `UsePageSetup`:

- **Headers and footers** are printed on every page, between the page's margins, the header at its distance below
  the top and the footer at its distance above the bottom (`pageMargins` `header` and `footer`). Their left, center
  and right sections (`&L`, `&C`, `&R`), the fields `&P` (also `&P+n`, `&P-n`, from the sheet's first page number),
  `&N`, `&D`, `&T`, `&A` (the sheet's name) and `&&`, and the font codes (`&"Family,Bold Italic"`, `&nn` size, `&B`,
  `&I`, `&U`, `&E`, `&S`, `&X`, `&Y`, `&Krrggbb`) are applied; first-page and even-page texts too. The date and time
  are `XlsxConverterOptions.PrintDate` (the conversion's time by default) in `Culture`'s short formats. `&F` and `&Z`
  (file name and folder) print nothing, since a conversion gets data, not a file; pictures (`&G`) are left out. The
  texts scale with the sheet unless they are set not to.
- **Page breaks set by hand** start a new page before their row (the header rows are repeated after it), and a new
  band before their column.

Cells show **rich text** (runs in their own font, size, color, bold, italic, underline and strikethrough),
**stacked text** (one character under the other), **rotated text** (turned up to 90 degrees either way, within its
cell; a row without a height of its own grows to fit it, as Excel fits it) and text set to **shrink to fit**, which is
shown smaller until it fits its columns.

**Conditional formatting** is applied over the stored values (the workbook's formulas are not calculated, but the
rules' own formulas are): rules comparing values (`cellIs`, with constants or formulas), formula rules
(`expression`), two- and three-color scales, **data bars** (drawn behind the value; Excel 2010's, from the extension
list, solid or fading, with their borders, negative values left of an axis drawn as a dashed line where the values
change sign or in the middle of the cells, or no axis), **icon sets** (all of Excel's, and custom ones, drawn
at the start of the cell), top and bottom ranks and percents, above and below average, duplicate and unique values,
text that contains, begins or ends with a text, blanks, errors, and dates in a period (`timePeriod`, against
`PrintDate`), in their priority with `stopIfTrue`; their formats set the font's bold, italic, underline,
strikethrough and color, the fill and the borders. Formula and `cellIs` rules are tried on the empty cells of their
ranges too, as Excel does (so banding with `MOD(ROW(),2)=0` shades empty rows). The rules' formulas may use numbers,
texts, `TRUE`/`FALSE`, error values, references to cells and ranges of the same sheet, the operators
`+ - * / ^ & % = <> < > <= >=`, and the functions `AND`, `OR`, `NOT`, `IF`, `MOD`, `ROW`, `COLUMN`, `ISBLANK`,
`ISNUMBER`, `ISTEXT`, `ISERROR`, `ISNA`, `ISEVEN`, `ISODD`, `ABS`, `INT`, `ROUND`, `LEN`, `LEFT`, `RIGHT`,
`UPPER`, `LOWER`, `TRIM`, `SEARCH`, `FIND`, `TODAY`, `SUM`, `AVERAGE`, `MIN`, `MAX`, `COUNT`, `COUNTA`,
`COUNTBLANK` and `COUNTIF`; a rule with anything else (other sheets, names, other functions) is not applied, and
reported. Their work is bounded (length, size and nesting of each formula, and the cells they read per sheet).

**Comments** (notes) print as the sheet's page setup asks: listed after the sheet, on a page of their own, with each
comment's cell and text, when it says "At end of sheet"; the boxes of the comments shown on the sheet drawn over it
where they are, with their text, fill and outline, when it says "As displayed on sheet"; otherwise not. `XlsxConverterOptions.Comments` can list them
at the end anyway (`SheetComments.AtEnd`), leave them out (`None`), or make them PDF notes (text annotations, "sticky
notes") at the top right corner of their cells (`Annotations`), which viewers open to show the text:

[!code-csharp[](../snippets/Sheets.cs#comments)]

**Pivot tables** show the cells Excel stored for them with their style: `PivotStyleLight16` (Excel's default) and
its siblings `PivotStyleLight15` to `PivotStyleLight21` give the header rows and the grand total row a light fill in
the theme color, bold text and a line under or over them, and subtotals and the grand total column bold text.

| `XlsxConverterOptions` | What it does |
|---|---|
| `SheetNames`, `SheetIndexes` | The sheets to convert, by name (ignoring case) or position; hidden ones too. All visible worksheets by default. |
| `ShowSheetNames` | Each sheet's name as a heading above its table (default `false`). |
| `UsePageSetup` | Use the sheets' print areas, print titles, orientation, scale and fit to width (default `true`). |
| `AutoFit` | Fit every column and row to its content instead of the sheet's sizes (default `false`). |
| `Culture` | The culture numbers and dates are shown in (default invariant). |
| `PrintDate` | The date and time headers and footers print for `&D` and `&T`, and `TODAY()` and date periods of conditional formatting compare with (default: the time of the conversion). |
| `Comments` | How comments print: as the page setup asks (`AsPageSetup`, the default), listed at the end (`AtEnd`), as PDF notes at their cells (`Annotations`), or not (`None`). |
| `Sheet` | The `SheetOptions` above. `FontSize` is not used: cells keep the workbook's sizes. |

### Untrusted workbooks

Workbooks are ZIP packages of XML parts. They are read with no DTDs and nothing external resolved, and within
`Sheet.Limits`: `MaxDecompressedBytes` bounds the decompressed size of all parts together (a ZIP bomb stops there),
`MaxTableCells` the cells of each sheet (the cells it stores while it is read, and the rows times columns it prints,
checked before they take up memory), and `MaxPages` and `Timeout` the pages of all sheets and the time. Malformed
packages and XML throw `SpreadsheetFormatException`; password-protected (encrypted) workbooks and binary `.xlsb`
workbooks throw `NotSupportedException`. Excel 97-2003 `.xls` files are converted as `XlsToPdf` converts them (below).
External workbook links, data connections, query tables, web queries and DDE or OLE links are never followed or
refreshed: formulas show the values stored with them (see [Untrusted input](untrusted-input.md)).

Not supported in workbooks, and reported through `Diagnostics` ([below](#diagnostics)): chart sheets of `.xls`
workbooks, the newer chart types (waterfall, treemap, sunburst...), SmartArt, ink, form controls, embedded objects, conditional formatting rules
whose formulas are not supported, sparklines, pivot tables of other styles than those above (their cells are shown),
and the pivot tables of `.xls` workbooks.

### Excel 97-2003 workbooks

`XlsToPdf` converts Excel 97-2003 workbooks (`.xls`, the binary BIFF8 format; also Excel 5.0 and 95 workbooks) with
the same options, model and layout as `.xlsx` workbooks, so everything above applies to them too: number formats and
the 1904 date system, fonts, the workbook's palette, fills and borders, alignment, merged cells, column widths and row
heights, hidden rows, columns and sheets, print areas and titles, orientation, scale and fit to width.

```csharp
var pdf = XlsToPdf.Convert("archive.xls", new XlsxConverterOptions { ShowSheetNames = true });
pdf.Save("archive.pdf");
```

Text comes from the shared strings (also when a string runs on into a CONTINUE record), and from cells of every kind
(`LABEL`, `RSTRING`, `NUMBER`, `RK`, `MULRK`, `BOOLERR`, blank cells with a format); formulas show their last
calculated values (numbers, the text of the `STRING` record after them, booleans and errors). Rich text keeps its
formatting runs (of the shared strings and `RSTRING` records); comments come from `NOTE` records and the text of their
note objects (`TXO`); conditional formatting from `CF` records (Excel 97's rules, their formulas read from their
parsed tokens), `CF12` records (Excel 2007's: color scales, data bars, icon sets, top and bottom, formula rules made
from templates) and the priorities of `CFEX` records; their colors are the exact ones Excel 2007 and later keep after
their formats (RGB and theme colors), else the workbook's palette's. Comments shown on the sheet are the note objects
whose `NOTE` record says so, drawn where their shapes are anchored. Excel 5.0 and 95
workbooks are read in their code page (`CODEPAGE`). The data is checked, not trusted: every length and offset is
bounded by its record, records that cannot be read are skipped, sheets whose offsets are wrong are found by reading
the stream in order, and the workbook stream is read within `MaxDecompressedBytes`. Damaged compound files throw
`SpreadsheetFormatException`; encrypted workbooks (also those protected with Excel's default password, which open
without asking for one) and Excel 2.x-4.0 files throw `NotSupportedException`.

The format is told from the data, not the file name: `XlsxToPdf` converts `.xls` workbooks too, and `XlsToPdf`
converts a `.xlsx` workbook (a ZIP package) named `.xls`, as some programs save them.

## Diagnostics

A conversion never leaves content out silently. `SheetOptions.Diagnostics` receives a `SheetDiagnostic` for each kind
of content a sheet has that is left out or simplified, with the sheet's name, the `SheetDiagnosticKind` and a message
that says how many there are (`2 pictures are left out.`). Each kind is reported at most once per sheet, in the order
the sheets are converted, while the conversion runs, so a caller can flag the document, or convert it another way.

[!code-csharp[](../snippets/Sheets.cs#diagnostics)]

| `SheetDiagnosticKind` | What is reported | In `.xlsx` | In `.xls` |
|---|---|---|---|
| `Chart` | Charts the chart engine cannot draw: left out. | Newer chart types (`chartex`), chart frames without a chart | Embedded chart objects |
| `ChartSheet` | A chart sheet that would be converted (visible, or named in the options) but has nothing that can be drawn: left out. | Chart sheets without a drawing that can be drawn | Chart sheets (BIFF charts) |
| `Picture` | Pictures that cannot be drawn (unreadable format such as PICT, damaged, linked, over `MaxImagePixels`): left out. | Pictures in the drawing | Picture objects |
| `Shape` | SmartArt, ink and form controls: left out. | Other graphic frames, ink; form controls | Free forms, less common shape types, control objects |
| `OleObject` | Embedded and linked objects, ActiveX controls: left out. | `oleObjects`; ActiveX controls | Picture objects of embedded objects |
| `Comment` | No longer reported: comments print as the page setup or `Comments` says. | | |
| `ConditionalFormatting` | Conditional formatting rules that are not applied (formulas that are not supported, damaged rules). | Those `cfRule`s, also of the extension list | `CF`, `CF12` records |
| `Sparkline` | Sparklines: left out. | Sparklines of the extension list | |
| `PivotTable` | Pivot tables of styles other than `PivotStyleLight15` to `21`: their cells are shown as stored, without the pivot table style. | Pivot table parts | `SXVIEW` records |
| `RichText` | No longer reported: rich text is shown. | | |
| `RotatedText` | No longer reported: rotated text is shown. | | |
| `Layout` | Content that does not fit the page: drawn past its edge, or cut off, with the page numbers. | | |

Counts are for the whole sheet, also outside the print area. Drawing objects that do not print (hidden, or set not
to print with the sheet, such as the drop-down arrows of filters) and the cells that show no text are not counted. CSV
has no sheets: only `Layout` applies, with no sheet name. Layout problems are rare, since columns are printed in bands
and text wraps; text too big for the page, such as a very large font on a small page, is one.

Drawing and pivot table parts are always read, to draw them, and comments parts when their comments print. With a
callback, the parts a workbook keeps the other content in (controls and their relationships) are read too, within `MaxDecompressedBytes`; a part that
cannot be read counts nothing and does not fail the conversion. Without one (the default), nothing more is read.

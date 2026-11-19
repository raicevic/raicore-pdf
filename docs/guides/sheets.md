# CSV to PDF

`RaiCore.Pdf.Sheets` converts CSV files to PDF: a table laid out by the layout engine, with the header row repeated
on every page, column widths fitted to the content within the page width, and numbers right-aligned.

```sh
dotnet add package RaiCore.Pdf.Sheets
```

[!code-csharp[](../snippets/Sheets.cs#convert)]

`CsvToPdf.Convert` takes a path, a stream or bytes; `ConvertAsync` (on the thread pool, with a cancellation token)
and `ConvertToBytes` do the same.

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

Not supported yet: Excel workbooks (`.xlsx`, planned for this package), formulas, cell formats and styles (CSV has
none), and other quote characters than the double quote.

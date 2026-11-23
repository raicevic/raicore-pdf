# RaiCore.Pdf.Sheets

Converts CSV files and Excel workbooks (`.xlsx`, and Excel 97-2003 `.xls`) to PDF with [RaiCore.Pdf](https://www.nuget.org/packages/RaiCore.Pdf)'s
layout engine: tables with header rows repeated on every page, columns fitted to the content and the page, numbers
right-aligned, and for workbooks their own formats and styles. No spreadsheet program, no browser, no native
dependencies.

```csharp
using RaiCore.Pdf.Sheets;

var pdf = CsvToPdf.Convert("orders.csv", new CsvConverterOptions { Sheet = new SheetOptions { Landscape = true } });
pdf.Save("orders.pdf");

var report = XlsxToPdf.Convert("report.xlsx");
report.Save("report.pdf");

var archive = XlsToPdf.Convert("archive.xls");
archive.Save("archive.pdf");
```

- **CSV done properly:** RFC 4180 quoting (delimiters, line breaks and doubled quotes in fields), CRLF, LF or CR line
  ends, and Excel's `sep=` line. The delimiter (comma, semicolon, tab, vertical bar), the encoding (byte order mark,
  UTF-8, UTF-16, Windows-1252) and the decimal separator (`1,234.56` or `1.234,56`) are detected, or set in the options.
- **Columns fitted to the content:** each column as wide as its widest cell when they all fit; otherwise narrow columns
  keep their width and the wide ones share the rest and wrap. Text is never clipped: words wider than their column are
  broken, and columns that cannot all get a readable width are printed in bands, one after the other.
- **Pages:** the header row repeats at the top of every page; landscape pages, and fit-to-width that shrinks the text
  (down to a minimum size) so that the columns fit without wrapping.
- **Spreadsheet alignment:** numbers (with signs, thousands separators, currency and percent signs) and dates right-aligned,
  text aligned to its direction (Hebrew and Arabic to the right).
- **Excel workbooks:** every visible sheet (or the ones you name) from a new page; values in their number formats
  (thousands separators, decimals, percent, fractions, scientific, currency, dates and times, colors and conditions,
  the 1900 and 1904 date systems), formulas as their last calculated values; fonts, colors, fills, borders, alignment,
  wrapping and indents; merged cells; the sheet's column widths and row heights; hidden rows, columns and sheets left
  out; text running on over empty cells as in a spreadsheet; the print area, rows repeated on every page, landscape,
  print scale and fit to width from the page setup.
- **Excel 97-2003 workbooks** (`.xls`, BIFF8, also Excel 5.0/95) with the same model and layout as `.xlsx`: shared
  strings across CONTINUE records, every kind of cell record, formulas' last values, fonts, the workbook's palette,
  number formats, XF styles, merged cells, sizes, hidden rows, columns and sheets, print areas and titles, page setup.
- **Untrusted input:** malformed CSV is read the way spreadsheet programs read it (stray quotes, ragged rows, invalid
  bytes); workbooks are read with no DTDs and nothing external, binary ones with every length and offset checked, and
malformed ones throw `SpreadsheetFormatException`;
  cells, decompressed bytes, pages and time are bounded by `ResourceLimits`.

Not supported in workbooks: charts, images, conditional formatting, comments, headers and footers, pivot tables,
formula calculation, `.xlsb`, Excel 2.x-4.0 and password-protected files.

Fonts, gridlines, tagging (accessible tables) and the page are set in `SheetOptions`.

More: the [spreadsheet guide](https://raicevic.github.io/raicore-pdf/guides/sheets.html).

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)

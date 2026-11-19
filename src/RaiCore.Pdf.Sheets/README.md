# RaiCore.Pdf.Sheets

Converts CSV files to PDF with [RaiCore.Pdf](https://www.nuget.org/packages/RaiCore.Pdf)'s layout engine: a table
with the header row repeated on every page, columns fitted to the content and the page, and numbers right-aligned. No
spreadsheet program, no browser, no native dependencies.

```csharp
using RaiCore.Pdf.Sheets;

var pdf = CsvToPdf.Convert("orders.csv", new CsvConverterOptions { Sheet = new SheetOptions { Landscape = true } });
pdf.Save("orders.pdf");
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
- **Untrusted input:** malformed data is read the way spreadsheet programs read it (stray quotes, ragged rows,
  invalid bytes); cells, pages and time are bounded by `ResourceLimits`.

Fonts, gridlines, tagging (accessible tables) and the page are set in `SheetOptions`.

More: the [CSV guide](https://raicevic.github.io/raicore-pdf/guides/sheets.html).

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)

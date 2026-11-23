# Workbooks: open, edit and save

`Workbook` in `RaiCore.Pdf.Sheets` opens an Excel workbook (a template, for example), reads and changes its cells,
inserts and deletes rows and columns, and saves it as `.xlsx` or a sheet of it as delimited text (CSV), without going
through PDF. Excel 97-2003 workbooks (`.xls`) and CSV files open read only.

```sh
dotnet add package RaiCore.Pdf.Sheets
```

[!code-csharp[](../snippets/Workbooks.cs#open)]

`Workbook.Open` takes a path, a stream or bytes, and tells the format by the content: a ZIP package is an Excel
workbook (`.xlsx`, `.xlsm`, also strict Office Open XML), a compound file an Excel 97-2003 one, anything else
delimited text. `Workbook.Create()` starts a new workbook with one empty sheet, and `Worksheets.Add(name)` adds more.

## Saving keeps the template

Only the parts of the package that change are rewritten:

- the worksheets whose cells, rows or columns change, and the parts that refer to moved cells (other sheets' formulas,
  defined names, tables, drawings, charts, comments, pivot caches);
- the shared strings when text is written (an existing string with the same text is reused), and the styles when a
  value needs a new number format (a copy of the cell's style with the date format, for example);
- the workbook part and the content types, when the calculation chain goes (below).

Everything else (themes, styles, charts, pictures, macros in `.xlsm` files, printer settings, custom XML, parts this
library does not know) is written back with the same bytes, in the same order, and a workbook saved without changes is
written exactly as it was read. Saving is deterministic: the same workbook and changes give the same file.

**Formulas are not calculated.** Cells with formulas keep their last calculated values (`Value` is that value). When
cells change, the saved workbook has no calculation chain (`calcChain.xml`, which would name cells that moved) and asks
Excel to recalculate everything when it opens it (`fullCalcOnLoad`), so the values are up to date there. A formula
evaluator is a separate feature.

## Reading cells

`sheet["B3"]` and `sheet.Cell(3, 2)` (rows and columns from 1) are the same cell. A cell is a position: after rows or
columns are inserted or deleted, get it again at its new address.

| Member | What it is |
|---|---|
| `Type` | `Empty`, `Text`, `Number`, `Boolean`, `DateTime` or `Error` (for a formula, its last calculated value). |
| `Value` | A `string`, `double`, `bool`, `DateTime`, the text of an error (`#N/A`), or `null`. |
| `TextValue`, `NumberValue`, `BooleanValue`, `DateTimeValue`, `ErrorValue` | The value when it is of that type, else `null`. |
| `Formula` | The formula without `=` (`SUM(B2:B6)`), also for the cells of a shared formula, or `null`. |
| `NumberFormat` | The number format code (`#,##0.00`); setting it gives the cell a style with that format. |
| `GetDisplayText(culture)` | The value as a spreadsheet shows it, in its number format and a culture (the invariant one by default). |

Dates are numbers in a date or time number format, in the workbook's date system (`Date1904`): a cell is a
`DateTime` when its number format shows a date or time. `Worksheet.Cells` lists the cells with a value or formula, row
by row; `LastRow` and `LastColumn` are where they end, and `MergedRanges` lists the merged cells.

## Writing cells

`SetValue` writes text, numbers (`double`, `int`, `long`, `decimal`), booleans and dates; `SetFormula` writes a formula
(English function names, `,` between arguments, A1 references); `Clear` removes the value. Writing keeps the cell's
style (font, fill, borders, alignment) and replaces its formula; a new cell takes the style of its row or column, as in
Excel. A date written to a cell without a date format gets the short date format (`yyyy-mm-dd` as this library shows
it, the system's short date in Excel), or date and time when it has a time of day.

Text that starts with `=` is text, not a formula. Writing to a cell of an array formula throws
`InvalidOperationException`, as Excel refuses it; writing to the first cell of a shared formula gives its other cells
formulas of their own first.

## Finding cells

[!code-csharp[](../snippets/Workbooks.cs#find)]

`Find` on a workbook looks at every worksheet (hidden ones too), on a worksheet at that one, and returns the cells row
by row. `FindOptions`:

| Option | What it does |
|---|---|
| `Match` | `Contains` (the default), `WholeCell` (an exact match) or `Regex` (a .NET regular expression, with `RegexTimeout`). |
| `MatchCase` | Whether case must match (default `false`). |
| `LookIn` | `Values`: the value as displayed (Excel's "Look in: Values", the default); `Formulas`: a cell's formula with its `=`, else its value as stored (numbers in the invariant culture, dates as ISO 8601). |
| `Culture` | The culture displayed values are formatted in (default the invariant culture). |

## Inserting and deleting rows and columns

[!code-csharp[](../snippets/Workbooks.cs#insert)]

`InsertRows(row, count)` inserts before a row, `DeleteRows(row, count)` deletes from it, and `InsertColumns` and
`DeleteColumns` do the same for columns. Everything that refers to the moved cells moves with them, as in Excel:

- **Formulas on every sheet:** references (`A1`, `$A$1`), ranges, whole rows and columns (`3:5`, `B:D`), references
  with a sheet name (`Data!B2`, `'My data'!B2`), shared formulas and array formulas. References at or after an
  insertion move; a range an insertion falls inside grows. References to deleted cells become `#REF!`
  (`Data!#REF!`), and ranges shrink to what is left of them.
- **Defined names**, including print areas and print titles, and the autofilter's hidden name.
- **The sheet's ranges:** merged cells, conditional formatting (ranges and formulas, also Excel 2010 extensions),
  data validation, hyperlinks (and links to cells), the autofilter and its sort, page breaks, protected ranges and
  ignored errors. Ranges that are deleted entirely go.
- **Tables:** their range, autofilter and sort; columns inserted inside a table become table columns (`Column4`,
  with that header), deleted ones are removed, and a table whose cells are all deleted is removed.
- **Drawings and comments:** pictures, charts and shapes anchored to cells (as their anchor says: moving and sizing
  with cells, moving only, or not at all), the cells comments belong to (comments on deleted cells go), the ranges of
  chart series on any sheet, and the source of pivot caches.
- **Rows and columns:** heights, widths, styles, visibility and outline levels move with them; rows inserted inside an
  outline group join it.

Not moved: references to more than one sheet (`Sheet1:Sheet3!A1`, which name the same cells on every sheet);
structured references to tables (`Sales[Amount]`), which need no change; references to other workbooks; and the
view (selection, scroll position, frozen panes). As in Excel, splitting an array formula, deleting part of a table's
header or totals row (or all its data rows), and inserting so many rows that cells with values would leave the sheet
throw `InvalidOperationException`, and nothing changes.

## Rows, columns and sheets

[!code-csharp[](../snippets/Workbooks.cs#hidden)]

`Row(n)` and `Column(n)` have `IsHidden`, `OutlineLevel` (0 to 7, the grouping), `IsCollapsed`, and `Height` or
`Width`, to read and to set. A worksheet's `IsHidden` hides its tab (a workbook keeps at least one visible sheet).

## Saving as delimited text

[!code-csharp[](../snippets/Workbooks.cs#csv)]

`SaveAsCsv` writes one worksheet (the active one, or `SheetName`): its rows from the first to the last with a value,
each with as many fields (from column A to the last column with a value). Merged cells have their value in the first
cell.

| `CsvSaveOptions` | What it does |
|---|---|
| `SheetName` | The sheet to write; the active one by default. |
| `Separator` | Any character but a double quote, a line break or NUL: `,` (the default), `;`, `'\t'`, `\|`. |
| `Encoding`, `ByteOrderMark` | UTF-8 (the default), `Encoding.Unicode` (UTF-16) or Windows-1252 (`CodePagesEncodingProvider.Instance.GetEncoding(1252)`, characters it lacks become `?`); and whether the text starts with a byte order mark (default `false`; Excel needs one to read UTF-8). |
| `Quoting` | `Minimal` (the default, RFC 4180: fields with the separator, a quote, a line break or spaces at their ends) or `Always`. Quotes in fields are doubled. |
| `LineEnding` | CRLF by default (RFC 4180). |
| `FormattedValues`, `Culture` | Values as displayed, in their number formats and a culture (the default, as Excel writes CSV), or as stored, the same in every culture: numbers with all their digits, dates as ISO 8601, booleans as `TRUE` and `FALSE`. General numbers show up to 15 significant digits. |
| `IncludeHiddenRows`, `IncludeHiddenColumns` | Whether hidden rows and columns are written (default `true`, as Excel does). |

## Converting to PDF

[!code-csharp[](../snippets/Workbooks.cs#convert)]

`XlsxToPdf.Convert(workbook, options)` and `ConvertToBytes` convert a workbook with its changes, without saving it
first, with every option of [workbook conversion](sheets.md#excel-workbooks).

## Excel 97-2003 workbooks and CSV files

[!code-csharp[](../snippets/Workbooks.cs#readonly)]

They open read only (`IsReadOnly`): their values, number formats, merged cells, and hidden rows, columns and sheets
can be read, found and saved as delimited text, and they convert to PDF as `XlsToPdf` and `CsvToPdf` convert them.
Changing them or saving them as `.xlsx` throws `NotSupportedException`. Formulas of `.xls` workbooks are only their
calculated values; CSV values are text, as written (`WorkbookOptions.CsvDelimiter` and `CsvEncoding` set what is
otherwise detected).

## Untrusted workbooks

Workbooks are read with no DTDs and nothing external resolved. `WorkbookOptions.Limits` bounds them:
`MaxDecompressedBytes` (all parts read together), `MaxElements` (the XML elements of all parts read) and
`MaxNestingDepth`, `MaxTableCells` (the cells of each worksheet), and `Timeout` (each operation: opening, an insertion
or deletion, saving). Exceeding one throws `ResourceLimitExceededException` naming it. Malformed data throws
`SpreadsheetFormatException`; encrypted and binary (`.xlsb`) workbooks throw `NotSupportedException`. Opening reads
the workbook part, the shared strings, the styles and every worksheet; drawings, charts, tables and comments are read
when rows or columns move.

A `Workbook` is not thread-safe: use one from one thread at a time.

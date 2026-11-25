# Excel 97-2003 workbooks (Apache POI)

Real-world binary workbooks (`.xls`) from the test data of [Apache POI](https://poi.apache.org/), used to test the
Excel 97-2003 converter (`XlsToPdf`).

- Source: <https://github.com/apache/poi/tree/f66927954a29cf1f5b86614fdf1816e6321c8aea/test-data/spreadsheet>, unchanged:
  - `SampleSS.xls` (three sheets, colors, a summary title), `Formatting.xls` (date and number formats),
    `DateFormats.xls`, `FormatChoiceTests.xls` (conditional formats, the 1904 date system),
    `WrongFormulaRecordType.xls` (formula records of an older type, the 1904 date system);
  - `57925.xls` (Cyrillic, merged cells, fills, hidden rows, print titles), `12561-1.xls` (Japanese, merged cells,
    a print area), `25695.xls` (Thai), `duprich1.xls` (rich text strings);
  - `46368.xls` (shared strings split across CONTINUE records), `ContinueRecordProblem.xls` (CONTINUE records, print
    titles and a print area that is `#REF!`), `SimpleWithPrintArea.xls` (a print area; a stale `Book` stream beside
    the `Workbook` stream), `46250.xls` (hidden sheets, rows and columns, a print area), `TwoSheetsOneHidden.xls`,
    `WithChart.xls` (an embedded chart);
  - content the converter reports as not supported (`SheetOptions.Diagnostics`): `DrawingAndComments.xls` (comments,
    an oval and a polygon), `44010-SingleChart.xls` (a chart sheet, text boxes), `ole2-embedding.xls` (embedded
    objects), `WithConditionalFormatting.xls` (conditional formatting rules), `SimpleWithPageBreaks.xls` (page breaks
    set by hand), `ShrinkToFit.xls` (shrink to fit); `46250.xls`, `12561-1.xls` and `46368.xls` above also have a
    picture, text boxes, a comment, rich text and headers and footers;
  - `NewStyleConditionalFormattings.xls` (Excel 2007 conditional formatting in CF12 records: data bars, color scales,
    icon sets, and formula rules; the same workbook as `../Formatting/NewStyleConditionalFormattings.xlsx`),
    `SimpleWithComments.xls` (comments);
  - `testEXCEL_95.xls` (Excel 95, BIFF5, in a `Book` stream), `testEXCEL_4.xls` (Excel 4.0, BIFF4: not supported);
  - `password.xls` (RC4 encrypted), `xor-encryption-abc.xls` (XOR obfuscated): encrypted, not supported;
  - `clusterfuzz-testcase-minimized-POIHSSFFuzzer-6537773940867072.xls`: a damaged compound file.
- License: Apache License 2.0, as part of Apache POI. Copyright 2003-2026 The Apache Software Foundation. This product
  includes software developed at The Apache Software Foundation (<https://www.apache.org/>).

`pivot_row_header.xls` is from the test data of [LibreOffice](https://www.libreoffice.org/), unchanged, at commit
[86090b9bfc1b](https://github.com/LibreOffice/core/tree/86090b9bfc1b38bb09f9110b8acad27abdf373f0/sc/qa/unit/data/xls):
two pivot tables, one with the table style `PivotStyleLight16` (Excel 2007's SXAddl record) and one without. License:
Mozilla Public License 2.0 (<https://www.mozilla.org/MPL/2.0/>); the file is the LibreOffice project's, distributed
here unmodified. Its workbook stream is the fuzz seed `../../Fuzz/Seeds/xls/pivot-tables.biff`.

`CommentBoxes.xls` is Apache POI's `SimpleWithComments.xls` with its first sheet's SETUP record printing comments as
displayed (fNotes without fEndNotes), written by `../make-comment-boxes.py`; its comment on B3 is shown on the sheet.

`print-formatting.xls` is not from Apache POI: `make-print.py` writes it with [xlwt](https://pypi.org/project/xlwt/)
1.3 (rich text in shared strings, rotated and stacked text).

The fuzz seeds in `../../Fuzz/Seeds/xls` are the workbook streams (bare BIFF, without the compound file) of
`SampleSS.xls`, `Formatting.xls`, `duprich1.xls`, `WrongFormulaRecordType.xls` and `testEXCEL_95.xls`, extracted
unchanged.

`expected.json` records what [xlrd](https://pypi.org/project/xlrd/) 2.0, an independent reader, finds in them
(`make-expected.py`): the BIFF version, the date system, the number formats of the FORMAT records, and for each sheet
its name, visibility, merged ranges, hidden rows and columns, column widths and its cells (up to 400 a sheet) with
their type, value and XF index; for the files xlrd cannot read, its error. Under `content`, for each sheet, the
content the converter does not support by kind (the names of `SheetDiagnosticKind`), counted from the sheet's records
by the script's own BIFF walk (drawing objects, embedded charts, comments, pivot tables, chart sheets; with `olefile`) and from xlrd (cells with rotated text). Under `print`, the headers, footers and page breaks; under `conditional`, each
sheet's conditional formatting rules from the BIFF walk (record, type, comparison, template, priority and ranges); and
for each sheet under `runs`, the formatting runs of its rich text as xlrd reads them.

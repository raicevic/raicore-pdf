# Workbook API fixtures

- `template.xlsx`: a template with most of what refers to cells (formulas across sheets, shared and array formulas,
  defined names, merged cells, conditional formatting, data validation, hyperlinks, an autofilter, a table, a chart, a
  picture, a comment, hidden and grouped rows, a page break, a hidden sheet), written with XlsxWriter by
  `make-workbooks.py`.
- `expected.json`: what openpyxl, an independent reader, reads from `template.xlsx`, the workbooks in `Poi` and those
  in `../Sheets` (sheets, values, formulas, merged ranges, defined names, hidden rows and columns), also written by
  `make-workbooks.py`; `WorkbookOracleTests` compares the Workbook API's reading with it.
- `check-saved.py`: checks workbooks the Workbook API saved (written by the test `Writes_edited_workbooks_for_the_openpyxl_check`
  when `RAICORE_WORKBOOK_OUT` names a folder) with openpyxl and the package rules Excel applies before it repairs a file.
- `Poi`: real-world workbooks from Apache POI, see [Poi/README.md](Poi/README.md).

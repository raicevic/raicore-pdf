# Excel workbooks (Apache POI)

Real-world workbooks (`.xlsx`, `.xlsm`) from the test data of [Apache POI](https://poi.apache.org/), used to test the
Workbook API: saving them unchanged and edited keeps every other part byte for byte, and insertions and deletions
keep them valid.

- Source: <https://github.com/apache/poi/tree/f66927954a29cf1f5b86614fdf1816e6321c8aea/test-data/spreadsheet>, unchanged:
  `SampleSS.xlsx`, `SampleSS.strict.xlsx` (strict Office Open XML), `shared_formulas.xlsx`,
  `TestShiftRowSharedFormula.xlsx`, `MatrixFormulaEvalTestData.xlsx` (array formulas), `WithTable.xlsx`,
  `ExcelTables.xlsx`, `50867_with_table.xlsx` (tables), `WithChart.xlsx`, `chart_sheet.xlsx` (a chart sheet),
  `WithDrawing.xlsx` (pictures), `comments.xlsx`, `NewStyleConditionalFormattings.xlsx`, `DataValidations-49244.xlsx`,
  `GroupTest.xlsx` (outline groups), `ExcelPivotTableSample.xlsx` (a pivot table), `sharedhyperlink.xlsx`,
  `unicodeSheetName.xlsx`, `SimpleMacro.xlsm` and `testNames.xlsm` (macros, defined names).
- License: Apache License 2.0, as part of Apache POI. Copyright 2003-2026 The Apache Software Foundation. This product
  includes software developed at The Apache Software Foundation (<https://www.apache.org/>).

# Cell formatting workbooks (Apache POI)

Workbooks (`.xlsx`) from the test data of [Apache POI](https://poi.apache.org/), used to test conditional formatting,
comments and pivot tables of the Excel converter (`XlsxToPdf`).

- Source: <https://github.com/apache/poi/tree/f66927954a29cf1f5b86614fdf1816e6321c8aea/test-data/spreadsheet>, unchanged:
  - `NewStyleConditionalFormattings.xlsx`: conditional formatting of Excel 2007 and 2010 (cell rules, a data bar with
    its extension, color scales, icon sets including stars and custom icons, a formula rule); the same workbook as
    `../Xls/NewStyleConditionalFormattings.xls`;
  - `SimpleWithComments.xlsx`: comments;
  - `ExcelPivotTableSample.xlsx`: two pivot tables.
- License: Apache License 2.0, as part of Apache POI. Copyright 2003-2026 The Apache Software Foundation. This product
  includes software developed at The Apache Software Foundation (<https://www.apache.org/>).

# Workbooks with drawings (Apache POI)

Real-world workbooks with pictures, shapes, text boxes and charts from the test data of
[Apache POI](https://poi.apache.org/), used to test how the Sheets converters draw a worksheet's drawings.

- Source: <https://github.com/apache/poi/tree/f66927954a29cf1f5b86614fdf1816e6321c8aea/test-data/spreadsheet>, unchanged:
  - `picture-and-shape-same-size.xlsx` (a picture and a rectangle with the same two-cell anchor),
    `absolute-anchor-over-empty-sheet.xlsx` (a cropped picture with an absolute anchor, larger than a page, on a sheet
    without cells), `WithTextBox.xlsx` (a text box with colored, aligned paragraphs), `WithThreeCharts.xlsx` (line
    and pie charts on two sheets);
  - `SimpleWithImages.xls` (JPEG, PNG, WMF and EMF pictures in the BLIP store), `SheetWithDrawing.xls` (a picture on a
    sheet without cells).
- License: Apache License 2.0, as part of Apache POI. Copyright 2003-2026 The Apache Software Foundation. This product
  includes software developed at The Apache Software Foundation (<https://www.apache.org/>).

`expected.json` records each workbook's drawings as `make-expected.py` reads them, independently of the converter:
for `.xlsx` files, the anchors of each worksheet's drawing part (kind, object, from and to markers, extent, position,
and whether it is drawn), cross-checked with [openpyxl](https://pypi.org/project/openpyxl/) 3.1, whose pictures and
charts must be among them; for `.xls` files, the shapes of each worksheet's OfficeArt drawing (with
[olefile](https://pypi.org/project/olefile/)): the OBJ type, shape type, client anchor, and whether it is drawn. It
also covers `../../Workbooks/Poi/WithDrawing.xlsx`, `../content.xlsx` and, from `../Xls`, `DrawingAndComments.xls`,
`46250.xls` and `44010-SingleChart.xls`.

The fuzz seeds in `../../Fuzz/Seeds/sheetdrawing` are drawing parts of these workbooks (and `group.xml`, written by
hand), unchanged; those in `../../Fuzz/Seeds/xlsdrawing` the OfficeArt data of a worksheet's MSODRAWING records of
`SimpleWithImages.xls`, `SheetWithDrawing.xls`, `DrawingAndComments.xls`, `46250.xls` and `44010-SingleChart.xls`,
extracted unchanged.

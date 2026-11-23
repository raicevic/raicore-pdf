# Chart corpus

Charts with known data, written by libraries that are not ours, for the chart engine (`RaiCore.Pdf/Charts`) and the
converters that draw charts. `make-charts.py` writes them (python-pptx 1.0.2, python-docx 1.2.0, openpyxl 3.1.5):

- `charts.pptx`: one chart per slide (clustered, stacked and 100% stacked columns, bars, lines with markers and
  smoothed, pies, an exploded pie, a doughnut, areas, stacked areas, radar, scatter and bubble charts), as python-pptx
  writes them, with cached values.
- `charts.docx`: a python-docx document with three charts (columns, a pie, a line) whose parts python-pptx's chart XML
  writer made, stored as Word stores charts.
- `charts.xlsx`: openpyxl charts (bar, line, area, radar, pie, scatter, bubble) that refer to cells without cached
  values.
- `expected.json`: each chart's type, title, categories, series names and values (and for the workbook, the cells the
  references point to).

Generated for this project; under its Apache License 2.0. The real-world charts are in `../Poi` (Apache POI test data).

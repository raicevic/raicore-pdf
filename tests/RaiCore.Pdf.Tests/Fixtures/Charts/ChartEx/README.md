# Newer chart types (chartex)

Charts of the chart types of Office 2016 (`cx:chartSpace` parts: waterfall, histogram, pareto, box and whisker,
funnel, treemap, sunburst, region map), for the chart engine (`RaiCore.Pdf/Charts`) and the converters that draw them.

- From the test data of [LibreOffice](https://www.libreoffice.org/), unchanged, at commit
  [86090b9bfc1b](https://github.com/LibreOffice/core/tree/86090b9bfc1b38bb09f9110b8acad27abdf373f0/chart2/qa/extras/data):
  `xlsx/waterfall.xlsx`, `xlsx/waterfall2.xlsx` (connector lines, axis titles, its own colors), `xlsx/SimpleHistogram.xlsx`,
  `xlsx/paretoLine.xlsx`, `xlsx/boxWhisker.xlsx` (three series, exclusive quartiles), `xlsx/funnel1.xlsx`,
  `xlsx/treemap.xlsx`, `xlsx/sunburst.xlsx` (written by Microsoft Excel; their charts store no data and refer to cells
  through hidden names), `xlsx/color_funnel.xlsx` (written by Collabora Office) and `pptx/funnel-pp1.pptx` (written by
  PowerPoint, with cached data and hidden series). License: Mozilla Public License 2.0
  (<https://www.mozilla.org/MPL/2.0/>); the files are the LibreOffice project's, distributed here unmodified.
- `chartex.docx` and `chartex.pptx`: written by `make-chartex.py` (python-docx 1.2.0, python-pptx 1.0.2), one chart of
  each type with cached data, stored as Word and PowerPoint store them (the chart as the markup compatibility choice, a
  picture as the fallback). Generated for this project; under its Apache License 2.0.
- `expected.json`: what each chart shows, computed by `make-chartex.py` from the chart XML and (for the workbooks) the
  cells with openpyxl 3.1.5, independently of the converters: waterfall running totals, histogram bins, pareto shares,
  box and whisker statistics (with Python's `statistics.quantiles` by the chart's quartile method), funnel values, and
  the top levels of treemaps and sunbursts. It also covers Apache POI's `../../Poi/chartex.docx` (Word's box and whisker
  and sunburst charts).

The `chartex-*.xml` fuzz seeds in `../../Fuzz/Seeds/chart` are the chart parts of `chartex.docx` and of
`../../Poi/chartex.docx`, unchanged.

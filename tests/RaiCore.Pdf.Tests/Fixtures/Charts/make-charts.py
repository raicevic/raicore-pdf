"""Builds the chart corpus: charts with known data written by independent libraries.

- charts.pptx: one chart per slide, written by python-pptx (exact cached data, PowerPoint's markup).
- charts.docx: a Word document with charts; python-docx cannot write charts, so the chart parts are written by
  python-pptx's chart XML writer and added to a python-docx document as Word stores them (word/charts/chartN.xml,
  a w:drawing with c:chart).
- charts.xlsx: charts written by openpyxl (references to cells, no cached values), for the workbook converter and the
  chart reader's cell-reference fallback.
- expected.json: what each chart shows: type, title, categories, series names and values.

Run with: python3 make-charts.py (python-pptx 1.0.2, python-docx 1.2.0, openpyxl 3.1.5).
"""

import io
import json
import os
import zipfile

from docx import Document
from docx.shared import Inches
from openpyxl import Workbook
from openpyxl.chart import AreaChart, BarChart, BubbleChart, LineChart, PieChart, RadarChart, Reference, ScatterChart, Series
from pptx import Presentation
from pptx.chart.data import BubbleChartData, CategoryChartData, XyChartData
from pptx.chart.xmlwriter import ChartXmlWriter
from pptx.enum.chart import XL_CHART_TYPE, XL_LEGEND_POSITION, XL_LABEL_POSITION
from pptx.util import Inches as PptInches, Pt

HERE = os.path.dirname(os.path.abspath(__file__))

QUARTERS = ["Q1", "Q2", "Q3", "Q4"]

# name, chart type, categories, series (name, values), extra settings
CATEGORY_CHARTS = [
    ("column-clustered", XL_CHART_TYPE.COLUMN_CLUSTERED, QUARTERS, [("North", (10, 20, 30, 40)), ("South", (15, 25, 5, 35))]),
    ("column-stacked", XL_CHART_TYPE.COLUMN_STACKED, QUARTERS, [("North", (10, 20, 30, 40)), ("South", (15, 25, 5, 35))]),
    ("column-percent", XL_CHART_TYPE.COLUMN_STACKED_100, QUARTERS, [("North", (10, 20, 30, 40)), ("South", (30, 20, 10, 40))]),
    ("bar-clustered", XL_CHART_TYPE.BAR_CLUSTERED, QUARTERS, [("Sales", (8.2, 3.2, 1.4, 1.2))]),
    ("line-markers", XL_CHART_TYPE.LINE_MARKERS, ["Jan", "Feb", "Mar", "Apr", "May"], [("Visitors", (1, 2, 3, 4, 5)), ("Members", (2, 1, 4, 3, 5))]),
    ("line-smooth", XL_CHART_TYPE.LINE, ["Jan", "Feb", "Mar", "Apr", "May"], [("Trend", (3, 7, 2, 8, 4))]),
    ("pie", XL_CHART_TYPE.PIE, ["Apples", "Pears", "Plums", "Figs"], [("Fruit", (50, 25, 15, 10))]),
    ("pie-exploded", XL_CHART_TYPE.PIE_EXPLODED, ["Apples", "Pears", "Plums"], [("Fruit", (1, 1, 2))]),
    ("doughnut", XL_CHART_TYPE.DOUGHNUT, ["Apples", "Pears", "Plums", "Figs"], [("2025", (40, 30, 20, 10)), ("2026", (25, 25, 25, 25))]),
    ("area", XL_CHART_TYPE.AREA, QUARTERS, [("North", (4, 6, 5, 8))]),
    ("area-stacked", XL_CHART_TYPE.AREA_STACKED, QUARTERS, [("North", (4, 6, 5, 8)), ("South", (2, 3, 4, 1))]),
    ("radar", XL_CHART_TYPE.RADAR_MARKERS, ["Speed", "Power", "Range", "Comfort", "Price"], [("Model A", (4, 3, 5, 2, 4)), ("Model B", (2, 5, 3, 4, 3))]),
]


def category_data(categories, series, number_format=None):
    data = CategoryChartData(number_format=number_format) if number_format else CategoryChartData()
    data.categories = categories
    for name, values in series:
        data.add_series(name, values)
    return data


def build_pptx(expected):
    prs = Presentation()
    blank = prs.slide_layouts[6]
    for name, chart_type, categories, series in CATEGORY_CHARTS:
        slide = prs.slides.add_slide(blank)
        frame = slide.shapes.add_chart(chart_type, PptInches(0.5), PptInches(0.5), PptInches(9), PptInches(6.5), category_data(categories, series))
        chart = frame.chart
        chart.font.size = Pt(12)
        chart.has_title = True
        chart.chart_title.text_frame.text = name.replace("-", " ").title()
        chart.has_legend = True
        chart.legend.position = XL_LEGEND_POSITION.BOTTOM
        chart.legend.include_in_layout = False
        if name == "line-smooth":
            chart.plots[0].series[0].smooth = True
        if name in ("pie", "doughnut"):
            plot = chart.plots[0]
            plot.has_data_labels = True
            plot.data_labels.show_percentage = True
            plot.data_labels.show_value = False
            plot.data_labels.number_format = "0%"
            plot.data_labels.number_format_is_linked = False
        if name == "column-clustered":
            plot = chart.plots[0]
            plot.has_data_labels = True
            plot.data_labels.show_value = True
            plot.data_labels.position = XL_LABEL_POSITION.OUTSIDE_END
        expected["pptx"].append({
            "name": name, "type": str(chart_type).split(" ")[0], "title": name.replace("-", " ").title(),
            "categories": categories, "series": [{"name": n, "values": list(v)} for n, v in series],
        })

    # Scatter and bubble charts.
    slide = prs.slides.add_slide(blank)
    xy = XyChartData()
    s = xy.add_series("Measured")
    for x, y in [(1, 2.5), (2, 3.5), (4, 4.0), (8, 6.5)]:
        s.add_data_point(x, y)
    chart = slide.shapes.add_chart(XL_CHART_TYPE.XY_SCATTER_LINES, PptInches(0.5), PptInches(0.5), PptInches(9), PptInches(6.5), xy).chart
    chart.font.size = Pt(12)
    chart.has_title = True
    chart.chart_title.text_frame.text = "Scatter"
    expected["pptx"].append({"name": "scatter", "type": "XY_SCATTER_LINES", "title": "Scatter", "categories": [],
                             "series": [{"name": "Measured", "x": [1, 2, 4, 8], "values": [2.5, 3.5, 4.0, 6.5]}]})

    slide = prs.slides.add_slide(blank)
    bubbles = BubbleChartData()
    s = bubbles.add_series("Markets")
    for x, y, size in [(1, 4, 10), (2, 2, 40), (3, 5, 20)]:
        s.add_data_point(x, y, size)
    chart = slide.shapes.add_chart(XL_CHART_TYPE.BUBBLE, PptInches(0.5), PptInches(0.5), PptInches(9), PptInches(6.5), bubbles).chart
    chart.font.size = Pt(12)
    chart.has_title = True
    chart.chart_title.text_frame.text = "Bubbles"
    expected["pptx"].append({"name": "bubble", "type": "BUBBLE", "title": "Bubbles", "categories": [],
                             "series": [{"name": "Markets", "x": [1, 2, 3], "values": [4, 2, 5], "sizes": [10, 40, 20]}]})

    buffer = io.BytesIO()
    prs.save(buffer)
    return buffer.getvalue()


DOCX_CHARTS = [
    ("Quarterly sales", XL_CHART_TYPE.COLUMN_CLUSTERED, QUARTERS, [("North", (10, 20, 30, 40)), ("South", (15, 25, 5, 35))]),
    ("Share of fruit", XL_CHART_TYPE.PIE, ["Apples", "Pears", "Plums", "Figs"], [("Fruit", (50, 25, 15, 10))]),
    ("Monthly visitors", XL_CHART_TYPE.LINE_MARKERS, ["Jan", "Feb", "Mar", "Apr", "May"], [("Visitors", (1, 2, 3, 4, 5))]),
]

DRAWING = (
    '<w:r><w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0" '
    'xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing">'
    '<wp:extent cx="{cx}" cy="{cy}"/><wp:docPr id="{id}" name="Chart {id}" descr="{title}"/>'
    '<a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">'
    '<a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/chart">'
    '<c:chart xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" '
    'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:id="{rid}"/>'
    '</a:graphicData></a:graphic></wp:inline></w:drawing></w:r>'
)


def build_docx(expected):
    document = Document()
    document.add_heading("Charts", level=1)
    for index, (title, chart_type, categories, series) in enumerate(DOCX_CHARTS, start=1):
        document.add_paragraph(f"Chart {index}: {title}.")
        document.add_paragraph(f"@@CHART{index}@@")
        expected["docx"].append({"title": title, "type": str(chart_type).split(" ")[0], "categories": categories,
                                 "series": [{"name": n, "values": list(v)} for n, v in series]})
    buffer = io.BytesIO()
    document.save(buffer)

    # Add the chart parts, their relationships and content types, and the drawings in place of the markers.
    source = zipfile.ZipFile(io.BytesIO(buffer.getvalue()))
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as target:
        for item in source.infolist():
            data = source.read(item.filename)
            if item.filename == "word/document.xml":
                text = data.decode("utf-8")
                for index, (title, chart_type, categories, series) in enumerate(DOCX_CHARTS, start=1):
                    marker = f"<w:r><w:t>@@CHART{index}@@</w:t></w:r>"
                    assert marker in text, marker
                    text = text.replace(marker, DRAWING.format(cx=5486400, cy=3200400, id=100 + index, title=title, rid=f"rIdChart{index}"))
                data = text.encode("utf-8")
            elif item.filename == "word/_rels/document.xml.rels":
                text = data.decode("utf-8")
                rels = "".join(
                    f'<Relationship Id="rIdChart{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart" Target="charts/chart{i}.xml"/>'
                    for i in range(1, len(DOCX_CHARTS) + 1))
                data = text.replace("</Relationships>", rels + "</Relationships>").encode("utf-8")
            elif item.filename == "[Content_Types].xml":
                text = data.decode("utf-8")
                types = "".join(
                    f'<Override PartName="/word/charts/chart{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.drawingml.chart+xml"/>'
                    for i in range(1, len(DOCX_CHARTS) + 1))
                data = text.replace("</Types>", types + "</Types>").encode("utf-8")
            target.writestr(item, data)
        for index, (title, chart_type, categories, series) in enumerate(DOCX_CHARTS, start=1):
            xml = ChartXmlWriter(chart_type, category_data(categories, series)).xml
            # A title, as Word writes it (the writer leaves titles to the chart object).
            title_xml = ('<c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{}</a:t></a:r></a:p></c:rich></c:tx>'
                         '<c:overlay val="0"/></c:title>').format(title)
            xml = xml.replace('<c:chart>', '<c:chart>' + title_xml, 1)
            target.writestr(f"word/charts/chart{index}.xml", xml)
    return output.getvalue()


def build_xlsx(expected):
    wb = Workbook()
    ws = wb.active
    ws.title = "Data"
    rows = [("Quarter", "North", "South")] + [(q, n, s) for q, n, s in zip(QUARTERS, (10, 20, 30, 40), (15, 25, 5, 35))]
    for row in rows:
        ws.append(row)
    data = Reference(ws, min_col=2, min_row=1, max_col=3, max_row=5)
    categories = Reference(ws, min_col=1, min_row=2, max_row=5)
    charts = []
    for name, chart in [("bar", BarChart()), ("line", LineChart()), ("area", AreaChart()), ("radar", RadarChart())]:
        chart.title = name.title()
        chart.add_data(data, titles_from_data=True)
        chart.set_categories(categories)
        charts.append((name, chart))
    pie = PieChart()
    pie.title = "Pie"
    pie.add_data(Reference(ws, min_col=2, min_row=1, max_row=5), titles_from_data=True)
    pie.set_categories(categories)
    charts.append(("pie", pie))
    scatter = ScatterChart()
    scatter.title = "Scatter"
    scatter.series.append(Series(Reference(ws, min_col=2, min_row=2, max_row=5), Reference(ws, min_col=3, min_row=2, max_row=5), title="North"))
    charts.append(("scatter", scatter))
    bubble = BubbleChart()
    bubble.title = "Bubble"
    bubble.series.append(Series(values=Reference(ws, min_col=2, min_row=2, max_row=5), xvalues=Reference(ws, min_col=3, min_row=2, max_row=5),
                                zvalues=Reference(ws, min_col=2, min_row=2, max_row=5), title="North"))
    charts.append(("bubble", bubble))
    for index, (name, chart) in enumerate(charts):
        ws.add_chart(chart, f"E{1 + index * 16}")
        expected["xlsx"].append({"name": name, "title": name.title(), "anchor": f"E{1 + index * 16}"})
    # The cells the charts refer to, by reference without quotes and dollar signs.
    expected["cells"] = {
        "Data!A2:A5": QUARTERS, "Data!B1": ["North"], "Data!C1": ["South"],
        "Data!B2:B5": [10, 20, 30, 40], "Data!C2:C5": [15, 25, 5, 35],
    }
    buffer = io.BytesIO()
    wb.save(buffer)
    return buffer.getvalue()


def main():
    expected = {"pptx": [], "docx": [], "xlsx": []}
    with open(os.path.join(HERE, "charts.pptx"), "wb") as f:
        f.write(build_pptx(expected))
    with open(os.path.join(HERE, "charts.docx"), "wb") as f:
        f.write(build_docx(expected))
    with open(os.path.join(HERE, "charts.xlsx"), "wb") as f:
        f.write(build_xlsx(expected))
    with open(os.path.join(HERE, "expected.json"), "w") as f:
        json.dump(expected, f, indent=1)


if __name__ == "__main__":
    main()

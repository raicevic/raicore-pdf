"""Generates the workbook fixtures of the Workbook API tests with XlsxWriter, and records with openpyxl (an independent
reader) what the fixtures hold: expected.json, which WorkbookOracleTests compares the Workbook API's reading with.
Run with a venv that has both (pure Python): python -m venv venv && venv/bin/pip install xlsxwriter openpyxl pillow;
from this folder: venv/bin/python make-workbooks.py. The files are deterministic: fixed dates, no random data."""
import datetime
import io
import json
import os
import re
import zipfile

import openpyxl
import xlsxwriter
from PIL import Image

CREATED = datetime.datetime(2026, 11, 23, 12, 0, 0)

# The most cells recorded per sheet, row by row, so that the expectations stay small.
MAX_CELLS = 300


def normalize(path, edit=None):
    """Rewrites a package with fixed entry dates, editing parts with edit(name, text) -> text."""
    with zipfile.ZipFile(path) as z:
        entries = [(info.filename, z.read(info.filename)) for info in z.infolist()]
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in entries:
            if edit is not None and name.endswith(".xml"):
                data = edit(name, data.decode("utf-8")).encode("utf-8")
            info = zipfile.ZipInfo(name, date_time=(2026, 11, 23, 12, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)


def template():
    """A template with most of what references cells: formulas across sheets (shared, array, 3-D, whole rows and
    columns, names, a structured reference), defined names with the print area and titles, merged cells, conditional
    formatting, data validation, hyperlinks, an autofilter, a table, a chart, a picture, a comment, hidden and grouped
    rows, a hidden column, a page break and a hidden sheet."""
    wb = xlsxwriter.Workbook("template.xlsx")
    wb.set_properties({"title": "Template", "created": CREATED})
    bold = wb.add_format({"bold": True})
    money = wb.add_format({"num_format": "#,##0.00"})
    date = wb.add_format({"num_format": "yyyy-mm-dd"})
    data = wb.add_worksheet("Data")
    other = wb.add_worksheet("Other Sheet")
    tab = wb.add_worksheet("Tab")
    secret = wb.add_worksheet("Secret")
    secret.hide()

    data.write_row("A1", ["Name", "Amount", "Date", "Flag", "Double", "Array"], bold)
    names = ["Alpha", "Bravo", "Charlie", "Delta", "Echo"]
    for i, name in enumerate(names):
        row = i + 1
        data.write_string(row, 0, name)
        data.write_number(row, 1, (i + 1) * 10, money)
        data.write_datetime(row, 2, datetime.datetime(2026, 1, i + 1), date)
        data.write_boolean(row, 3, i % 2 == 0)
        data.write_formula(row, 4, "=B%d*2" % (row + 1), None, (i + 1) * 20)
    data.write_array_formula("F2:F3", "{=B2:B3*10}", None, 100)
    data.write_formula("B7", "=SUM(B2:B6)", money, 150)
    data.write_string("A8", "Hidden row")
    data.set_row(7, None, None, {"hidden": True})
    data.merge_range("A9:C9", "Merged", bold)
    for row in range(9, 12):
        data.write_string(row, 0, "Group %d" % (row - 8))
        data.set_row(row, None, None, {"level": 1})
    data.write_url("A13", "internal:'Other Sheet'!A1", string="To other")
    data.write_url("B13", "https://example.com/", string="Example")
    data.write_string("G1", "Hidden column")
    data.set_column("G:G", None, None, {"hidden": True})
    data.write_comment("C3", "A comment")
    data.conditional_format("B2:B6", {"type": "cell", "criteria": ">", "value": 25, "format": bold})
    data.conditional_format("A2:A6", {"type": "formula", "criteria": "=$D2", "format": bold})
    data.data_validation("D2:D6", {"validate": "list", "source": ["TRUE", "FALSE"]})
    data.data_validation("B2:B6", {"validate": "decimal", "criteria": "between", "minimum": 0, "maximum": 1000})
    data.autofilter("A1:F6")
    data.set_h_pagebreaks([7])
    data.print_area("A1:F13")
    data.repeat_rows(0)
    data.freeze_panes(1, 0)
    chart = wb.add_chart({"type": "column"})
    chart.add_series({"name": "=Data!$B$1", "categories": "=Data!$A$2:$A$6", "values": "=Data!$B$2:$B$6"})
    data.insert_chart("J2", chart)
    picture = io.BytesIO()
    Image.new("RGB", (8, 8), (31, 78, 121)).save(picture, format="PNG")
    data.insert_image("J20", "square.png", {"image_data": picture})

    other.write_string("A1", "Other")
    other.write_formula("A2", "=Data!B2", None, 10)
    other.write_formula("A3", "=SUM(Data!B2:B6)", None, 150)
    other.write_formula("A4", "='Other Sheet'!A1", None, "Other")
    other.write_formula("A5", "=SUM(Data!B:B)", None, 300)
    other.write_formula("A6", "=SUM(Data!2:3)", None, 30)
    other.write_formula("A7", "=SUM(Data:Tab!A1)", None, 0)
    other.write_formula("A8", "=Total", None, 150)
    other.write_formula("A9", "=SUM(Sales[Amount])", None, 10)

    tab.add_table("A1:C5", {"name": "Sales", "columns": [{"header": "Item"}, {"header": "Amount"}, {"header": "Note"}],
                            "data": [["Pen", 1, "a"], ["Ink", 2, "b"], ["Pad", 3, "c"], ["Cap", 4, "d"]]})
    secret.write_string("A1", "Secret")

    wb.define_name("Total", "=Data!$B$7")
    wb.define_name("Amounts", "=Data!$B$2:$B$6")
    wb.define_name("'Other Sheet'!Local", "='Other Sheet'!$A$1")
    wb.close()

    def edit(name, text):
        # E2:E6 as one shared formula, as Excel writes formulas filled down.
        if name == "xl/worksheets/sheet1.xml":
            text = text.replace("<f>B2*2</f>", '<f t="shared" ref="E2:E6" si="0">B2*2</f>')
            for row in range(3, 7):
                text = text.replace("<f>B%d*2</f>" % row, '<f t="shared" si="0"/>')
        if name == "docProps/core.xml":
            text = re.sub(r"<dcterms:modified[^>]*>[^<]*</dcterms:modified>",
                          '<dcterms:modified xsi:type="dcterms:W3CDTF">2026-11-23T12:00:00Z</dcterms:modified>', text)
        return text

    normalize("template.xlsx", edit)


def value(cell):
    """A cell's value as JSON: its type and value, as openpyxl reads it."""
    v = cell.value
    if v is None:
        return None
    if isinstance(v, bool):
        return ["b", v]
    if isinstance(v, (int, float)):
        return ["n", float(v)]
    if isinstance(v, datetime.datetime):
        return ["d", v.strftime("%Y-%m-%dT%H:%M:%S")]
    if isinstance(v, datetime.date):
        return ["d", v.strftime("%Y-%m-%dT00:00:00")]
    if isinstance(v, datetime.timedelta):
        return ["dur", v.total_seconds()]
    if isinstance(v, datetime.time):
        return ["t", v.strftime("%H:%M:%S")]
    if isinstance(v, str) and v.startswith("#") and cell.data_type == "e":
        return ["e", v]
    return ["s", str(v)]


def describe(path):
    """What openpyxl reads from a workbook: sheets, values (cached values of formulas), formulas, merged ranges,
    defined names, hidden rows and columns."""
    values = openpyxl.load_workbook(path, data_only=True)
    formulas = openpyxl.load_workbook(path, data_only=False)
    result = {"date1904": values.epoch.year == 1904, "sheets": [], "names": {}}
    for name, defined in sorted(formulas.defined_names.items()):
        result["names"][name] = defined.value
    for ws in values.worksheets:
        fs = formulas[ws.title]
        sheet = {"name": ws.title, "hidden": ws.sheet_state != "visible", "cells": {}, "formulas": {},
                 "merged": sorted(str(r) for r in ws.merged_cells.ranges),
                 "hiddenRows": sorted(r for r, d in ws.row_dimensions.items() if d.hidden),
                 "hiddenColumns": sorted(set(
                     openpyxl.utils.column_index_from_string(k) + i for k, d in ws.column_dimensions.items() if d.hidden
                     for i in range((d.max or openpyxl.utils.column_index_from_string(k)) - (d.min or openpyxl.utils.column_index_from_string(k)) + 1)))}
        for row in ws.iter_rows():
            for cell in row:
                if len(sheet["cells"]) >= MAX_CELLS:
                    break
                v = value(cell)
                if v is not None:
                    sheet["cells"][cell.coordinate] = v
                f = fs[cell.coordinate].value
                if isinstance(f, str) and f.startswith("="):
                    sheet["formulas"][cell.coordinate] = f[1:]
        result["sheets"].append(sheet)
    if not result["sheets"]:
        # openpyxl reads no sheets from strict Office Open XML.
        return {"error": "NoSheets"}
    return result


def expected():
    files = sorted(["template.xlsx"] + ["Poi/" + f for f in os.listdir("Poi") if f.endswith((".xlsx", ".xlsm"))]
                   + ["../Sheets/" + f for f in os.listdir("../Sheets") if f.endswith(".xlsx")])
    result = {}
    for f in files:
        try:
            result[f] = describe(f)
        except Exception as e:  # noqa: BLE001 - recorded, so the test skips what openpyxl cannot read
            result[f] = {"error": type(e).__name__}
    with open("expected.json", "w", encoding="utf-8") as out:
        # One line per workbook.
        out.write("{\n" + ",\n".join(json.dumps(k) + ": " + json.dumps(v, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
                                     for k, v in sorted(result.items())) + "\n}\n")


if __name__ == "__main__":
    template()
    expected()

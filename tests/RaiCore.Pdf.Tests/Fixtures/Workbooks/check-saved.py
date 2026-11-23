"""Checks workbooks the Workbook API saved with openpyxl, an independent reader, and the package rules Excel applies
before it would repair a file. Write them with the test that RAICORE_WORKBOOK_OUT turns on:
    RAICORE_WORKBOOK_OUT=/tmp/saved RaiCore.Pdf.Tests -method "*Writes_edited_workbooks_for_the_openpyxl_check"
then run (with a venv that has openpyxl): venv/bin/python check-saved.py /tmp/saved
Every package: well-formed XML parts, a content type for every part, no override or relationship to a missing part,
unique relationship ids, and openpyxl opens it. The template's edits: values, formulas, merged cells, defined names,
hidden rows and columns, conditional formatting, data validation, tables and autofilters where Excel puts them."""
import datetime
import os
import posixpath
import sys
import xml.etree.ElementTree as ET
import zipfile

import openpyxl

CT = "{http://schemas.openxmlformats.org/package/2006/content-types}"
REL = "{http://schemas.openxmlformats.org/package/2006/relationships}"
failures = []


def check(condition, message):
    if not condition:
        failures.append(message)


def package(path):
    """The package rules: XML well-formed, content types, relationships."""
    name = os.path.basename(path)
    with zipfile.ZipFile(path) as z:
        names = [n for n in z.namelist() if not n.endswith("/")]
        check(len(names) == len(set(names)), f"{name}: duplicate entries")
        parts = {n: z.read(n) for n in names}
    for part, data in parts.items():
        if part.endswith((".xml", ".rels", ".vml")):
            try:
                ET.fromstring(data)
            except ET.ParseError as e:
                check(part.endswith(".vml"), f"{name}: {part} is not well-formed: {e}")
    types = ET.fromstring(parts["[Content_Types].xml"])
    defaults = {d.get("Extension").lower() for d in types.iter(CT + "Default")}
    overrides = {o.get("PartName").lstrip("/") for o in types.iter(CT + "Override")}
    for part in parts:
        if part == "[Content_Types].xml":
            continue
        check(part in overrides or part.rsplit(".", 1)[-1].lower() in defaults, f"{name}: {part} has no content type")
    for override in overrides:
        check(override in parts, f"{name}: content type override for missing part {override}")
    for part in [p for p in parts if p.endswith(".rels")]:
        folder = posixpath.dirname(posixpath.dirname(part))
        ids = []
        for rel in ET.fromstring(parts[part]).iter(REL + "Relationship"):
            ids.append(rel.get("Id"))
            if rel.get("TargetMode") == "External":
                continue
            target = rel.get("Target")
            resolved = target.lstrip("/") if target.startswith("/") else posixpath.normpath(posixpath.join(folder, target))
            check(resolved in parts, f"{name}: {part} targets missing part {resolved}")
        check(len(ids) == len(set(ids)), f"{name}: {part} has duplicate relationship ids")


def formulas(path):
    return openpyxl.load_workbook(path)


def values(path):
    return openpyxl.load_workbook(path, data_only=True)


def names(wb):
    result = {n: d.value for n, d in wb.defined_names.items()}
    for ws in wb.worksheets:
        for n, d in ws.defined_names.items():
            result[ws.title + "!" + n] = d.value
        if ws.print_area:
            result[ws.title + "!Print_Area"] = ws.print_area
        if ws.print_title_rows:
            result[ws.title + "!Print_Titles"] = ws.print_title_rows
    return result


def template_checks(folder):
    wb = formulas(os.path.join(folder, "insert-rows.xlsx"))
    data, other = wb["Data"], wb["Other Sheet"]
    check(data["A3"].value == "Alpha" and data["A2"].value is None, "insert-rows: Alpha moved to A3")
    check(data["E7"].value == "=B7*2", f"insert-rows: E7 {data['E7'].value}")
    check(data["B8"].value == "=SUM(B3:B7)", f"insert-rows: B8 {data['B8'].value}")
    check(other["A2"].value == "=Data!B3", f"insert-rows: Other A2 {other['A2'].value}")
    check(other["A3"].value == "=SUM(Data!B3:B7)", f"insert-rows: Other A3 {other['A3'].value}")
    check(other["A6"].value == "=SUM(Data!3:4)", f"insert-rows: Other A6 {other['A6'].value}")
    check(other["A7"].value == "=SUM(Data:Tab!A1)", f"insert-rows: Other A7 {other['A7'].value}")
    n = names(wb)
    check(n.get("Total") == "Data!$B$8", f"insert-rows: Total {n.get('Total')}")
    check(n.get("Amounts") == "Data!$B$3:$B$7", f"insert-rows: Amounts {n.get('Amounts')}")
    check(n.get("Data!Print_Area") == "'Data'!$A$1:$F$14", f"insert-rows: print area {n.get('Data!Print_Area')}")
    check(n.get("Data!Print_Titles") == "$1:$1", f"insert-rows: print titles {n.get('Data!Print_Titles')}")
    check([str(r) for r in data.merged_cells.ranges] == ["A10:C10"], f"insert-rows: merged {data.merged_cells.ranges}")
    check(data.row_dimensions[9].hidden and not data.row_dimensions[8].hidden, "insert-rows: hidden row 9")
    check([data.row_dimensions[r].outline_level for r in range(10, 15)] == [0, 1, 1, 1, 0], "insert-rows: outline 11-13")
    check(sorted(str(r.sqref) for r in data.conditional_formatting) == ["A3:A7", "B3:B7"], "insert-rows: conditional formatting")
    check(sorted(str(v.sqref) for v in data.data_validations.dataValidation) == ["B3:B7", "D3:D7"], "insert-rows: data validation")
    check(data.auto_filter.ref == "A1:F7", f"insert-rows: autofilter {data.auto_filter.ref}")
    links = sorted(c.coordinate for row in data.iter_rows() for c in row if c.hyperlink)
    check(links == ["A14", "B14"], f"insert-rows: hyperlinks {links}")
    check(data["A14"].hyperlink.location == "'Other Sheet'!A1", "insert-rows: internal hyperlink")
    v = values(os.path.join(folder, "insert-rows.xlsx"))["Data"]
    check(v["C3"].value == datetime.datetime(2026, 1, 1) and v["B3"].value == 10, "insert-rows: values")

    wb = formulas(os.path.join(folder, "delete-rows.xlsx"))
    data, other = wb["Data"], wb["Other Sheet"]
    check(data["A4"].value == "Echo" and data["B5"].value == "=SUM(B2:B4)", "delete-rows: cells")
    check(other["A3"].value == "=SUM(Data!B2:B4)", f"delete-rows: Other A3 {other['A3'].value}")
    check([str(r) for r in data.merged_cells.ranges] == ["A7:C7"], "delete-rows: merged")
    check(names(wb).get("Amounts") == "Data!$B$2:$B$4", "delete-rows: Amounts")

    wb = formulas(os.path.join(folder, "insert-columns.xlsx"))
    data, other, tab = wb["Data"], wb["Other Sheet"], wb["Tab"]
    check(data["C7"].value == "=SUM(C2:C6)" and other["A2"].value == "=Data!C2", "insert-columns: formulas")
    check(other["A5"].value == "=SUM(Data!C:C)", "insert-columns: whole column")
    check(data.column_dimensions["H"].hidden, "insert-columns: hidden column H")
    table = tab.tables["Sales"]
    check(table.ref == "A1:D5" and [c.name for c in table.tableColumns] == ["Item", "Column4", "Amount", "Note"], "insert-columns: table")
    check(tab["B1"].value == "Column4", "insert-columns: table header cell")

    wb = formulas(os.path.join(folder, "delete-columns.xlsx"))
    data, other = wb["Data"], wb["Other Sheet"]
    check(data["A1"].value == "Date" and data["A2"].value is not None, "delete-columns: cells")
    check(other["A2"].value == "=Data!#REF!", f"delete-columns: Other A2 {other['A2'].value}")
    check(data["C2"].value == "=#REF!*2", f"delete-columns: C2 {data['C2'].value}")

    v = values(os.path.join(folder, "values.xlsx"))
    data = v["Data"]
    check(data["A2"].value == "Zulu" and data["B2"].value == 12.5 and data["D2"].value is False, "values: written")
    check(data["C2"].value == datetime.datetime(2027, 2, 3), f"values: date {data['C2'].value}")
    check(data["H2"].value == datetime.datetime(2026, 11, 23, 14, 15, 16), f"values: date time {data['H2'].value}")
    check(data["H4"].value == "  spaced & <escaped> _x0041_ ", f"values: text {data['H4'].value!r}")
    check(data.row_dimensions[3].hidden and not data.row_dimensions[8].hidden, "values: hidden rows")
    check(data.column_dimensions["B"].hidden and data.column_dimensions["D"].width == 20, "values: columns")
    check(v["Secret"].sheet_state == "visible" and v["Added"]["A1"].value == "New sheet", "values: sheets")
    check(formulas(os.path.join(folder, "values.xlsx"))["Data"]["H3"].value == "=SUM(B2:B6)", "values: formula")

    v = values(os.path.join(folder, "created.xlsx"))
    sheet = v.worksheets[0]
    check([sheet["A1"].value, sheet["B1"].value, sheet["C1"].value, sheet["D1"].value] == ["Text", 1.5, True, datetime.datetime(2026, 11, 23)], "created: values")
    check(sheet.row_dimensions[2].hidden and v["Second"]["A1"].value == "Second", "created: rows and sheets")


def main(folder):
    files = sorted(f for f in os.listdir(folder) if f.endswith((".xlsx", ".xlsm")))
    for f in files:
        path = os.path.join(folder, f)
        package(path)
        try:
            formulas(path)
            values(path)
        except Exception as e:  # noqa: BLE001 - what openpyxl cannot read is a failure, unless the original fails too
            # openpyxl cannot read strict files, nor minimal.xlsx (it cannot read the original either).
            check(f in ("corpus-SampleSS.strict.xlsx", "corpus-minimal.xlsx"), f"{f}: openpyxl cannot open it: {type(e).__name__}: {e}")
    template_checks(folder)
    for failure in failures:
        print("FAIL", failure)
    print(f"{len(files)} workbooks, {len(failures)} failures")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))

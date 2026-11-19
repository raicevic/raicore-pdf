"""Generates the XLSX fixtures of the Sheets tests with XlsxWriter and openpyxl (pure Python; run with a venv that has
both: python -m venv venv && venv/bin/pip install xlsxwriter openpyxl; from this folder: venv/bin/python make-sheets.py).
The files are deterministic: fixed document dates, no random data."""
import datetime
import zipfile

import openpyxl
import xlsxwriter
from openpyxl.styles import Alignment, Font
from openpyxl.worksheet.properties import PageSetupProperties

CREATED = datetime.datetime(2026, 11, 19, 12, 0, 0)


def normalize(path):
    """Rewrites a package with fixed entry dates and document dates, so that openpyxl's output is the same every time."""
    with zipfile.ZipFile(path) as z:
        entries = [(info.filename, z.read(info.filename)) for info in z.infolist()]
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in entries:
            if name == "docProps/core.xml":
                text = data.decode("utf-8")
                start = text.find("<dcterms:modified")
                end = text.find("</dcterms:modified>")
                if start >= 0 and end > start:
                    text = text[:start] + '<dcterms:modified xsi:type="dcterms:W3CDTF">2026-11-19T12:00:00Z' + text[end:]
                data = text.encode("utf-8")
            info = zipfile.ZipInfo(name, date_time=(2026, 11, 19, 12, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)


def styled():
    """Fonts, colors, fills, borders, alignment, wrapping, a merged title, widths and heights, number formats."""
    wb = xlsxwriter.Workbook("styled.xlsx")
    wb.set_properties({"title": "Styled workbook", "created": CREATED})
    ws = wb.add_worksheet("Styled")
    title = wb.add_format({"bold": True, "font_size": 16, "align": "center", "bg_color": "#1F4E79", "font_color": "#FFFFFF", "border": 2})
    ws.merge_range("A1:E1", "Quarterly report", title)
    ws.set_row(0, 30)
    ws.set_column("A:A", 18)
    ws.set_column("B:E", 12)
    head = wb.add_format({"bold": True, "bottom": 5, "bg_color": "#D9E1F2"})
    for col, text in enumerate(["Item", "Amount", "Share", "Date", "Note"]):
        ws.write(1, col, text, head)
    money = wb.add_format({"num_format": "#,##0.00", "border": 1})
    pct = wb.add_format({"num_format": "0.0%", "border": 1})
    date = wb.add_format({"num_format": "yyyy-mm-dd", "border": 1})
    wrap = wb.add_format({"text_wrap": True, "valign": "top", "border": 1})
    red = wb.add_format({"num_format": "#,##0.00;[Red]-#,##0.00", "border": 1})
    rows = [
        ("Coffee", 1234.5, 0.125, datetime.date(2026, 1, 15), "Bought in bulk for the whole office, delivered weekly"),
        ("Tea", -99.95, 0.5, datetime.date(2026, 2, 28), "Refund"),
        ("Water", 1000000, 1, datetime.date(2026, 12, 31), ""),
    ]
    for r, (item, amount, share, day, note) in enumerate(rows, start=2):
        ws.write(r, 0, item, wb.add_format({"italic": r == 3, "underline": 1 if r == 4 else 0, "border": 1}))
        ws.write_number(r, 1, amount, red if amount < 0 else money)
        ws.write_number(r, 2, share, pct)
        ws.write_datetime(r, 3, datetime.datetime.combine(day, datetime.time()), date)
        ws.write(r, 4, note, wrap)
    ws.write(5, 0, "Centered", wb.add_format({"align": "center", "font_color": "#C00000", "font_strikeout": True}))
    ws.write(5, 1, "Right", wb.add_format({"align": "right", "bg_color": "#FFFF00"}))
    ws.write(6, 0, "Dashed and double borders", wb.add_format({"left": 3, "right": 6, "top": 4, "bottom": 6, "border_color": "#00B050"}))
    ws.write(7, 0, "A long text that runs on over the empty cells to its right, as in a spreadsheet")
    ws.write_formula(8, 0, "=B3+B4", wb.add_format({"num_format": "#,##0.00", "bold": True}), 1134.55)
    ws.write_boolean(8, 1, True)
    ws.write_formula(8, 2, "=1/0", None, "#DIV/0!")
    ws.write_formula(8, 3, '="text"&"result"', None, "textresult")
    ws.write_rich_string(9, 0, "plain ", wb.add_format({"bold": True}), "bold", " and ", wb.add_format({"italic": True}), "italic")
    ws.write(10, 0, "Indented", wb.add_format({"indent": 2}))
    wb.close()


def formats():
    """One value per row in many number formats: the value in column A, the formatted one in column B."""
    wb = xlsxwriter.Workbook("formats.xlsx")
    wb.set_properties({"created": CREATED})
    ws = wb.add_worksheet("Formats")
    cases = [
        (1234.5678, "General"), (1234.5678, "0"), (1234.5678, "0.00"), (1234567.891, "#,##0"), (1234567.891, "#,##0.00"),
        (0.4567, "0%"), (0.4567, "0.00%"), (12345.678, "0.00E+00"), (1.5, "# ?/?"), (0.3125, "# ??/??"),
        (46345.75, "yyyy-mm-dd"), (46345.75, "dd/mm/yyyy"), (46345.75, "d mmmm yyyy"), (46345.75, "h:mm"),
        (1.5, "[h]:mm:ss"), (46345.75, "h:mm AM/PM"), (-5, "0.00;(0.00)"), (0, "0;-0;\"zero\""),
        (1234.5, "\"$\"#,##0.00"), (1234.5, "[$\u20ac-407] #,##0.00"), (-1234.5, "#,##0.00;[Red]-#,##0.00"),
        (123456789, "000-00-0000"), (0.000123, "General"), (123456789012, "General"), (46345.5, "ddd, mmm d"),
        (0.0000123, "0.0##"), (5, "0 \"items\""), (1500000, "#,##0.0,,\"M\""), (0.5, "mm:ss.0"), (45, "[>=100]\"big\";\"small\""),
    ]
    for r, (value, code) in enumerate(cases):
        ws.write_string(r, 0, code)
        ws.write_number(r, 1, value, wb.add_format({"num_format": code}))
    ws.set_column("A:B", 24)
    wb.close()


def wide():
    """30 columns and 120 rows without set widths: fitted to the content, printed in bands."""
    wb = xlsxwriter.Workbook("wide.xlsx")
    wb.set_properties({"created": CREATED})
    ws = wb.add_worksheet("Wide")
    for c in range(30):
        ws.write_string(0, c, f"Column {c + 1}")
    for r in range(1, 121):
        for c in range(30):
            ws.write_number(r, c, r * 1000 + c)
    wb.close()


def multi():
    """Several sheets: print titles, a print area, landscape and fit to width, hidden rows, columns and sheets."""
    wb = openpyxl.Workbook()
    wb.properties.created = CREATED
    wb.properties.modified = CREATED
    summary = wb.active
    summary.title = "Summary"
    summary["A1"] = "Summary sheet"
    summary["A1"].font = Font(bold=True, size=14)
    summary["A2"] = "Rows on the data sheet"
    summary["B2"] = 200

    data = wb.create_sheet("Data")
    data.append(["Report title (printed once)"])
    data.append(["Id", "Name", "Hidden column", "Value"])
    data.append(["", "", "", "(units)"])
    for i in range(1, 201):
        data.append([i, f"Name {i}", "secret", i * 1.5])
    data.column_dimensions["C"].hidden = True
    data.row_dimensions[10].hidden = True
    data.print_title_rows = "2:3"
    data.print_area = "A1:D150"
    data.page_setup.orientation = "landscape"
    data.column_dimensions["A"].width = 8
    data.column_dimensions["B"].width = 20
    data.column_dimensions["D"].width = 12
    for cell in data[2]:
        cell.font = Font(bold=True)

    fit = wb.create_sheet("Fit")
    fit.sheet_properties.pageSetUpPr = PageSetupProperties(fitToPage=True)
    fit.page_setup.fitToWidth = 1
    fit.page_setup.fitToHeight = 0
    for c in range(1, 16):
        fit.cell(row=1, column=c, value=f"Fit {c}")
        fit.column_dimensions[openpyxl.utils.get_column_letter(c)].width = 9
    fit["A2"] = "Wrapped text in a cell"
    fit["A2"].alignment = Alignment(wrap_text=True)

    secret = wb.create_sheet("Secret")
    secret["A1"] = "Hidden sheet"
    secret.sheet_state = "hidden"
    wb.save("multi.xlsx")
    normalize("multi.xlsx")


def date1904():
    """A workbook in the 1904 date system."""
    wb = xlsxwriter.Workbook("date1904.xlsx", {"date_1904": True})
    wb.set_properties({"created": CREATED})
    ws = wb.add_worksheet()
    ws.write_datetime(0, 0, datetime.datetime(2026, 11, 19), wb.add_format({"num_format": "yyyy-mm-dd"}))
    ws.write_number(1, 0, 0, wb.add_format({"num_format": "yyyy-mm-dd"}))
    wb.close()


def inline():
    """Inline strings (XlsxWriter's constant memory mode writes them instead of shared strings)."""
    wb = xlsxwriter.Workbook("inline.xlsx", {"constant_memory": True})
    wb.set_properties({"created": CREATED})
    ws = wb.add_worksheet()
    ws.write_string(0, 0, "Inline text")
    ws.write_string(0, 1, "with \x07 an escape")  # A control character: written as _x0007_.
    ws.write_number(1, 0, 42)
    wb.close()


def minimal():
    """A minimal hand-made package as other producers write it: no styles, no r attributes, a strict date cell."""
    files = {
        "[Content_Types].xml": '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
        '<Default Extension="xml" ContentType="application/xml"/></Types>',
        "_rels/.rels": '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rId1" Type="http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument" Target="/xl/workbook.xml"/></Relationships>',
        "xl/workbook.xml": '<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://purl.oclc.org/ooxml/spreadsheetml/main" '
        'xmlns:r="http://purl.oclc.org/ooxml/officeDocument/relationships"><sheets><sheet name="Minimal" sheetId="1" r:id="rId1"/></sheets></workbook>',
        "xl/_rels/workbook.xml.rels": '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rId1" Type="http://purl.oclc.org/ooxml/officeDocument/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
        "xl/worksheets/sheet1.xml": '<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://purl.oclc.org/ooxml/spreadsheetml/main"><sheetData>'
        '<row><c t="inlineStr"><is><t>First</t></is></c><c><v>1.5</v></c><c t="d"><v>2026-11-19</v></c></row>'
        '<row><c t="inlineStr"><is><r><t>Rich </t></r><r><rPr><b/></rPr><t>runs</t></r><rPh><t>phonetic</t></rPh></is></c><c t="b"><v>0</v></c></row>'
        "</sheetData></worksheet>",
    }
    with zipfile.ZipFile("minimal.xlsx", "w", zipfile.ZIP_DEFLATED) as z:
        for name, text in files.items():
            info = zipfile.ZipInfo(name, date_time=(2026, 11, 19, 12, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, text)


def seeds():
    """Uncompressed copies of the small fixtures as fuzzing seeds: mutations then reach the XML (ZIP readers do not
    check the CRC), where compressed data would mostly fail to inflate. Only one keeps its theme part."""
    for name in ["minimal", "styled", "formats", "inline", "date1904", "multi"]:
        with zipfile.ZipFile(f"{name}.xlsx") as z:
            entries = [(info.filename, z.read(info.filename)) for info in z.infolist()
                       if not info.filename.startswith("docProps/") and (name == "styled" or not info.filename.startswith("xl/theme/"))]
        with zipfile.ZipFile(f"../Fuzz/Seeds/xlsx/{name}.xlsx", "w", zipfile.ZIP_STORED) as z:
            for entry, data in entries:
                z.writestr(zipfile.ZipInfo(entry, date_time=(2026, 11, 19, 12, 0, 0)), data)


if __name__ == "__main__":
    styled()
    formats()
    wide()
    multi()
    date1904()
    inline()
    minimal()
    seeds()

"""Writes PDFs from producers other than RaiCore.Pdf for the extraction tests, and expected.json with what MuPDF
(PyMuPDF) reads from them: for each page, its displayed width and height and its characters with their origin in
display space (top-left of the rotated crop box, y down) and their font size. MuPDF is an independent reader, so the tests compare RaiCore's interpreter with it.

Run from this folder with:
  python3 -m venv venv && venv/bin/pip install pymupdf "reportlab<4.1" fpdf2
  venv/bin/python make-fixtures.py ../../../../src/RaiCore.Pdf.Fonts/Liberation
The fonts embedded are Liberation Sans and Serif (SIL Open Font License 1.1).
"""
import datetime
import json
import sys
from pathlib import Path

import fitz
from fpdf import FPDF
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas

FONTS = Path(sys.argv[1])
SANS = str(FONTS / "LiberationSans-Regular.ttf")
SERIF = str(FONTS / "LiberationSerif-Regular.ttf")
LATIN = "Gr\u00fc\u00dfe \u2013 1,50 \u20ac \u201cquoted\u201d"
GREEK = "\u03a9mega \u03b1\u03b2\u03b3"


def reportlab_standard(path):
    c = canvas.Canvas(path, pagesize=(595, 842), invariant=1)
    c.setFont("Helvetica", 12)
    c.drawString(72, 770, "Hello, World! " + LATIN)
    c.setFont("Times-Bold", 20)
    c.drawString(72, 700, "Times Bold")
    t = c.beginText(300, 700)
    t.setFont("Courier", 9)
    t.setCharSpace(1)
    t.setWordSpace(5)
    t.textOut("spaced out")
    c.drawText(t)
    c.showPage()
    c.setPageRotation(90)
    c.setFont("Helvetica", 14)
    c.drawString(100, 500, "Rotated page")
    c.showPage()
    c.save()


def reportlab_truetype(path):
    pdfmetrics.registerFont(TTFont("LiberationSerif", SERIF))
    c = canvas.Canvas(path, pagesize=(595, 842), invariant=1)
    c.setFont("LiberationSerif", 16)
    c.drawString(72, 770, LATIN)
    c.drawString(72, 740, GREEK)
    t = c.beginText(72, 700)
    t.setFont("LiberationSerif", 11)
    t.setHorizScale(80)
    t.textLine("Scaled 80 percent")
    t.setRise(4)
    t.textLine("Raised")
    c.drawText(t)
    c.saveState()
    c.translate(300, 400)
    c.rotate(30)
    c.drawString(0, 0, "Turned 30 degrees")
    c.restoreState()
    c.showPage()
    c.save()


def pymupdf(path):
    doc = fitz.open()
    page = doc.new_page(width=595, height=842)
    page.insert_text((72, 72), "Base 14 Helvetica: " + LATIN, fontname="helv", fontsize=11)
    page.insert_font(fontname="lib", fontfile=SANS)
    page.insert_text((72, 120), "Embedded: " + LATIN + " " + GREEK, fontname="lib", fontsize=13)
    page = doc.new_page(width=595, height=842)
    page.set_rotation(270)
    page.insert_text((72, 100), "Rotated 270", fontname="helv", fontsize=12)
    doc.subset_fonts()
    doc.save(path, garbage=3, deflate=True, no_new_id=True)


def fpdf2(path):
    pdf = FPDF(unit="pt", format=(595, 842))
    pdf.set_creation_date(datetime.datetime(2026, 1, 1, tzinfo=datetime.timezone.utc))
    pdf.add_page()
    pdf.add_font("Liberation", fname=SANS)
    pdf.set_font("Liberation", size=12)
    pdf.text(72, 100, LATIN)
    pdf.text(72, 130, GREEK)
    pdf.set_font("Liberation", size=24)
    pdf.text(72, 200, "Large")
    pdf.output(path)


def expected(path):
    doc = fitz.open(path)
    pages = []
    for page in doc:
        chars = []
        for block in page.get_text("rawdict")["blocks"]:
            for line in block.get("lines", []):
                for span in line["spans"]:
                    for char in span["chars"]:
                        origin = fitz.Point(char["origin"]) * page.rotation_matrix
                        chars.append([char["c"], round(origin.x, 3), round(origin.y, 3), round(span["size"], 3)])
        pages.append({"width": page.rect.width, "height": page.rect.height, "chars": chars})
    return pages


def main():
    makers = {"reportlab-standard.pdf": reportlab_standard, "reportlab-truetype.pdf": reportlab_truetype,
              "pymupdf.pdf": pymupdf, "fpdf2.pdf": fpdf2}
    result = {}
    for name, make in makers.items():
        make(name)
        result[name] = expected(name)
        print(name, [len(p["chars"]) for p in result[name]])
    # One line per page: [width, height, [[char, x, y, size], ...]].
    lines = [f'  "{name}": [\n' + ",\n".join(
        "    " + json.dumps([page["width"], page["height"], page["chars"]], ensure_ascii=True) for page in pages) + "\n  ]"
        for name, pages in result.items()]
    Path("expected.json").write_text("{\n" + ",\n".join(lines) + "\n}\n")


if __name__ == "__main__":
    main()

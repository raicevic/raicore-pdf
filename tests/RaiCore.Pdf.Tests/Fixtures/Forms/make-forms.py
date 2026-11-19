"""Generates the form fixtures: the same form made by PyMuPDF and by reportlab (run with a venv that has pymupdf and
reportlab<4.1; from this folder: python make-forms.py)."""
import fitz
from reportlab.lib.pagesizes import A4
from reportlab.pdfgen import canvas

# PyMuPDF: two pages; a text field, a check box (on), a combo box and a list box on page 1 (its radio groups are not
# reliable to make, reportlab's file has one), and a
# required read-only text field on page 2.
doc = fitz.open()
doc.new_page()
doc.new_page()
p1, p2 = doc[0], doc[1]

def widget(page, kind, name, rect, value=None, **extra):
    w = fitz.Widget()
    w.field_type = kind
    w.field_name = name
    w.rect = fitz.Rect(*rect)
    if value is not None:
        w.field_value = value
    for k, v in extra.items():
        setattr(w, k, v)
    page.add_widget(w)

widget(p1, fitz.PDF_WIDGET_TYPE_TEXT, "name", (72, 72, 300, 92), "Ada Lovelace")
widget(p1, fitz.PDF_WIDGET_TYPE_CHECKBOX, "subscribe", (72, 110, 90, 128), True)
widget(p1, fitz.PDF_WIDGET_TYPE_COMBOBOX, "country", (72, 170, 200, 190), "Denmark", choice_values=["Denmark", "Norway", "Sweden"])
widget(p1, fitz.PDF_WIDGET_TYPE_LISTBOX, "colour", (72, 200, 200, 260), "Blue", choice_values=["Red", "Blue"])
widget(p2, fitz.PDF_WIDGET_TYPE_TEXT, "id", (72, 72, 300, 92), "A-17", field_flags=fitz.PDF_FIELD_IS_READ_ONLY | fitz.PDF_FIELD_IS_REQUIRED)
doc.save("pymupdf-form.pdf")

# reportlab: one page; a text field, a check box (off), a radio group and a choice field.
c = canvas.Canvas("reportlab-form.pdf", pagesize=A4)
form = c.acroForm
form.textfield(name="name", value="Grace Hopper", x=72, y=700, width=200, height=20)
form.checkbox(name="subscribe", checked=False, x=72, y=660, size=18)
form.radio(name="size", value="small", selected=False, x=72, y=620, size=18)
form.radio(name="size", value="large", selected=True, x=100, y=620, size=18)
form.choice(name="country", value="Norway", options=["Denmark", "Norway", "Sweden"], x=72, y=580, width=150, height=20)
c.showPage()
c.save()

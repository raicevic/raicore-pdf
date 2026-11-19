"""Generates the annotation fixture with PyMuPDF, which writes an appearance stream for each annotation (run with a venv
that has pymupdf; from this folder: python make-annotations.py)."""
import fitz

doc = fitz.open()
page = doc.new_page()  # A4 portrait
page.insert_text((72, 80), "Highlighted words in a line of text.", fontsize=14)
page.insert_text((72, 110), "Underlined, struck out and squiggly words.", fontsize=14)
page.insert_text((72, 520), "A link to the second page.", fontsize=12)

words = page.search_for("Highlighted words")
page.add_highlight_annot(words[0])
page.add_underline_annot(page.search_for("Underlined")[0])
page.add_strikeout_annot(page.search_for("struck out")[0])
page.add_squiggly_annot(page.search_for("squiggly")[0])

ink = page.add_ink_annot([[(80, 160), (120, 140), (160, 180), (200, 150)]])
ink.set_colors(stroke=(0, 0, 1)); ink.set_border(width=3); ink.update()

square = page.add_rect_annot(fitz.Rect(80, 200, 200, 260))
square.set_colors(stroke=(1, 0, 0), fill=(1, 1, 0)); square.set_border(width=2); square.update()
circle = page.add_circle_annot(fitz.Rect(240, 200, 320, 260))
circle.set_colors(stroke=(0, 0.5, 0)); circle.update()
line = page.add_line_annot((360, 200), (500, 260))
line.set_colors(stroke=(0.5, 0, 0.5)); line.set_border(width=2); line.update()
polygon = page.add_polygon_annot([(80, 300), (160, 280), (200, 340), (100, 360)])
polygon.set_colors(stroke=(0, 0, 0), fill=(0.6, 0.8, 1)); polygon.update()

free = page.add_freetext_annot(fitz.Rect(240, 290, 500, 330), "Free text, burnt in.", fontsize=12, text_color=(0, 0, 0), fill_color=(1, 0.9, 0.8))
free.update()
page.add_stamp_annot(fitz.Rect(80, 380, 240, 430), stamp=fitz.STAMP_Approved)

# A sticky note with its pop-up window.
note = page.add_text_annot((300, 400), "A note with a pop-up.")
note.set_popup(fitz.Rect(320, 380, 480, 460))

# Hidden and not printed: removed without being drawn.
hidden = page.add_rect_annot(fitz.Rect(80, 460, 140, 490))
hidden.set_colors(stroke=(1, 0, 0), fill=(1, 0, 0)); hidden.update()
hidden.set_flags(fitz.PDF_ANNOT_IS_HIDDEN | fitz.PDF_ANNOT_IS_PRINT)
screen = page.add_rect_annot(fitz.Rect(160, 460, 220, 490))
screen.set_colors(stroke=(0, 0, 1), fill=(0, 0, 1)); screen.update()
screen.set_flags(0)

link = page.search_for("A link to the second page.")[0]
doc.new_page()
page = doc[0]
page.insert_link({"kind": fitz.LINK_GOTO, "page": 1, "from": link})

doc.save("pymupdf-annotations.pdf", garbage=3, deflate=True)

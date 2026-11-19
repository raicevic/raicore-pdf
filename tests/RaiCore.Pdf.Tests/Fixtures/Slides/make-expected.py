"""Records what python-pptx, an independent reader, finds in the Slides fixtures and the Apache POI presentations
(../Poi/*.pptx): the slide size, and for each slide whether it is hidden and the text of its shapes with their boxes
in points (placeholders with the position they inherit). The tests check the PDFs against it (expected.json).
Run with a venv that has python-pptx, from this folder, isolated: venv/bin/python -I make-expected.py"""
import glob
import json
import os

from pptx import Presentation
from pptx.util import Emu

HERE = os.path.dirname(os.path.abspath(__file__))


def points(emu):
    return None if emu is None else round(Emu(emu).pt, 2)


def shapes(collection, grouped=False):
    for shape in collection:
        if shape.shape_type == 6:  # group
            yield from shapes(shape.shapes, True)
            continue
        text = ""
        wrap = None
        if shape.has_text_frame:
            text = "\n".join(p.text for p in shape.text_frame.paragraphs)
            wrap = shape.text_frame.word_wrap
        elif getattr(shape, "has_table", False) and shape.has_table:
            text = "\n".join(cell.text for row in shape.table.rows for cell in row.cells)
        if not text.strip():
            continue
        yield {
            "text": text,
            "left": points(shape.left),
            "top": points(shape.top),
            "width": points(shape.width),
            "height": points(shape.height),
            "rotation": shape.rotation if hasattr(shape, "rotation") else 0,
            "grouped": grouped,
            "wrap": wrap,
            "table": bool(getattr(shape, "has_table", False) and shape.has_table),
        }


def describe(path):
    prs = Presentation(path)
    slides = []
    for slide in prs.slides:
        slides.append({
            "hidden": slide._element.get("show") == "0",
            "shapes": list(shapes(slide.shapes)),
        })
    return {"width": points(prs.slide_width), "height": points(prs.slide_height), "slides": slides}


if __name__ == "__main__":
    files = sorted(glob.glob(os.path.join(HERE, "*.pptx"))) + sorted(glob.glob(os.path.join(HERE, "..", "Poi", "*.pptx")))
    result = {}
    for path in files:
        name = os.path.relpath(path, os.path.join(HERE, "..")).replace(os.sep, "/")
        result[name] = describe(path)
    with open(os.path.join(HERE, "expected.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(result, f, indent=1, ensure_ascii=True, sort_keys=True)
        f.write("\n")
    print(len(result), "presentations")

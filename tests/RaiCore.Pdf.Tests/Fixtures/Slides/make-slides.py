"""Generates the PPTX fixtures of the Slides tests with python-pptx (pure Python; run with a venv that has it and
Pillow: python -m venv venv && venv/bin/pip install python-pptx pillow; from this folder: venv/bin/python make-slides.py).
The files are deterministic: fixed document dates and ZIP entry dates, no random data. Also writes uncompressed copies as
the seeds of the pptx fuzz target."""
import datetime
import io
import zipfile

from PIL import Image, ImageDraw
from pptx import Presentation
from pptx.dml.color import RGBColor
from pptx.enum.dml import MSO_LINE_DASH_STYLE, MSO_THEME_COLOR
from pptx.enum.shapes import MSO_CONNECTOR, MSO_SHAPE
from pptx.enum.text import MSO_ANCHOR, MSO_AUTO_SIZE, PP_ALIGN
from pptx.oxml.ns import qn
from pptx.util import Emu, Inches, Pt

CREATED = datetime.datetime(2026, 11, 19, 12, 0, 0)


def normalize(path):
    """Rewrites a package with fixed entry dates, so that the output is the same every time."""
    with zipfile.ZipFile(path) as z:
        entries = [(info.filename, z.read(info.filename)) for info in z.infolist()]
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in entries:
            info = zipfile.ZipInfo(name, date_time=(2026, 11, 19, 12, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)


def properties(prs, title):
    core = prs.core_properties
    core.title = title
    core.author = "RaiCore tests"
    core.created = CREATED
    core.modified = CREATED
    core.last_modified_by = "RaiCore tests"
    core.revision = 1


def png(width, height, colors):
    """A PNG of vertical color bands with a black diagonal, so that cropping and flipping show."""
    image = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(image)
    band = width // len(colors)
    for i, color in enumerate(colors):
        draw.rectangle([i * band, 0, (i + 1) * band - 1, height - 1], fill=color)
    draw.line([0, 0, width - 1, height - 1], fill="black", width=max(1, width // 40))
    buffer = io.BytesIO()
    image.save(buffer, "PNG")
    buffer.seek(0)
    return buffer


def set_hidden(slide):
    slide._element.set("show", "0")


def basic():
    """Title slide, bullets with levels, shapes, pictures, a table, a group, a hidden slide, autofit and numbering."""
    prs = Presentation()
    properties(prs, "Basic deck")

    # 1. Title slide: centered title and subtitle placeholders inherit position and style from the layout and master.
    slide = prs.slides.add_slide(prs.slide_layouts[0])
    slide.shapes.title.text = "Quarterly Review"
    slide.placeholders[1].text = "Converted with RaiCore"

    # 2. Title and content: bullets on three levels, a bold run, a link.
    slide = prs.slides.add_slide(prs.slide_layouts[1])
    slide.shapes.title.text = "Agenda"
    body = slide.placeholders[1].text_frame
    body.text = "First level point"
    for text, level in [("Second level detail", 1), ("Third level note", 2), ("Back to the first level", 0)]:
        p = body.add_paragraph()
        p.text = text
        p.level = level
    p = body.add_paragraph()
    run = p.add_run()
    run.text = "Bold words"
    run.font.bold = True
    run = p.add_run()
    run.text = " and a link"
    run.hyperlink.address = "https://example.com/slides"

    # 3. Shapes: fills (RGB, theme colors with brightness), lines (dashed, thick), rotation, presets, a connector.
    slide = prs.slides.add_slide(prs.slide_layouts[6])
    rect = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(0.5), Inches(0.5), Inches(2), Inches(1.2))
    rect.fill.solid()
    rect.fill.fore_color.rgb = RGBColor(0xC0, 0x20, 0x20)
    rect.line.color.rgb = RGBColor(0, 0, 0)
    rect.line.width = Pt(3)
    rect.text_frame.text = "Red box"
    oval = slide.shapes.add_shape(MSO_SHAPE.OVAL, Inches(3), Inches(0.5), Inches(2), Inches(1.2))
    oval.fill.solid()
    oval.fill.fore_color.theme_color = MSO_THEME_COLOR.ACCENT_2
    oval.fill.fore_color.brightness = -0.25
    oval.text_frame.text = "Oval"
    rounded = slide.shapes.add_shape(MSO_SHAPE.ROUNDED_RECTANGLE, Inches(6), Inches(0.5), Inches(2.5), Inches(1.2))
    rounded.rotation = 30
    rounded.text_frame.text = "Turned"
    triangle = slide.shapes.add_shape(MSO_SHAPE.ISOSCELES_TRIANGLE, Inches(0.5), Inches(2.5), Inches(1.5), Inches(1.5))
    triangle.fill.solid()
    triangle.fill.fore_color.rgb = RGBColor(0x20, 0xA0, 0x40)
    arrow = slide.shapes.add_shape(MSO_SHAPE.RIGHT_ARROW, Inches(2.5), Inches(2.7), Inches(2.5), Inches(1))
    arrow.text_frame.text = "Next"
    star = slide.shapes.add_shape(MSO_SHAPE.STAR_5_POINT, Inches(5.5), Inches(2.5), Inches(1.5), Inches(1.5))
    star.fill.solid()
    star.fill.fore_color.rgb = RGBColor(0xFF, 0xC0, 0x00)
    dashed = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(7.3), Inches(2.5), Inches(2), Inches(1.5))
    dashed.fill.background()
    dashed.line.color.rgb = RGBColor(0x30, 0x30, 0xC0)
    dashed.line.width = Pt(2)
    dashed.line.dash_style = MSO_LINE_DASH_STYLE.DASH
    dashed.text_frame.text = "Dashed"
    dashed.text_frame.paragraphs[0].runs[0].font.color.rgb = RGBColor(0x30, 0x30, 0xC0)
    line = slide.shapes.add_connector(MSO_CONNECTOR.STRAIGHT, Inches(0.5), Inches(5), Inches(4), Inches(6.5))
    line.line.color.rgb = RGBColor(0, 0, 0)
    line.line.width = Pt(2)
    tail = line.line._get_or_add_ln()
    end = tail.makeelement(qn("a:tailEnd"), {"type": "triangle", "w": "med", "len": "med"})
    tail.append(end)
    box = slide.shapes.add_textbox(Inches(5), Inches(5), Inches(4), Inches(1))
    box.text_frame.text = "A plain text box"

    # 4. Pictures: whole, and cropped (left half cut off) and flipped.
    slide = prs.slides.add_slide(prs.slide_layouts[5])
    slide.shapes.title.text = "Pictures"
    picture = slide.shapes.add_picture(png(200, 100, ["#ff0000", "#00c000", "#0000ff", "#ffff00"]), Inches(0.5), Inches(2), Inches(4), Inches(2))
    picture._element.nvPicPr.cNvPr.set("descr", "Four color bands")
    cropped = slide.shapes.add_picture(png(200, 100, ["#ff0000", "#00c000", "#0000ff", "#ffff00"]), Inches(5), Inches(2), Inches(2), Inches(2))
    cropped.crop_left = 0.5
    cropped.line.color.rgb = RGBColor(0, 0, 0)
    cropped.line.width = Pt(2)

    # 5. A table in the default table style, with a merged header cell.
    slide = prs.slides.add_slide(prs.slide_layouts[5])
    slide.shapes.title.text = "Results"
    table = slide.shapes.add_table(4, 3, Inches(1), Inches(2), Inches(8), Inches(2.4)).table
    data = [["Region", "Q3", "Q4"], ["North", "120", "135"], ["South", "98", "110"], ["Total", "218", "245"]]
    for r, row in enumerate(data):
        for c, value in enumerate(row):
            table.cell(r, c).text = value
    table.cell(1, 0).merge(table.cell(2, 0))
    table.cell(1, 0).text = "North and South"

    # 6. A group (scaled child coordinates) and a flipped shape.
    slide = prs.slides.add_slide(prs.slide_layouts[6])
    group = slide.shapes.add_group_shape()
    a = group.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(1), Inches(1), Inches(2), Inches(1))
    a.text_frame.text = "Grouped A"
    b = group.shapes.add_shape(MSO_SHAPE.OVAL, Inches(3.5), Inches(1), Inches(2), Inches(1))
    b.text_frame.text = "Grouped B"
    flipped = slide.shapes.add_shape(MSO_SHAPE.RIGHT_TRIANGLE, Inches(6), Inches(1), Inches(2), Inches(2))
    flipped._element.spPr.find(qn("a:xfrm")).set("flipH", "1")
    flipped.text_frame.text = "Flipped"

    # 7. A hidden slide.
    slide = prs.slides.add_slide(prs.slide_layouts[5])
    slide.shapes.title.text = "Hidden slide"
    set_hidden(slide)

    # 8. Text frames: anchoring, alignment, a numbered list, shrink on overflow, a gradient.
    slide = prs.slides.add_slide(prs.slide_layouts[6])
    bottom = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(0.5), Inches(0.5), Inches(4), Inches(2))
    bottom.text_frame.vertical_anchor = MSO_ANCHOR.BOTTOM
    bottom.text_frame.text = "Bottom right"
    bottom.text_frame.paragraphs[0].alignment = PP_ALIGN.RIGHT
    numbered = slide.shapes.add_textbox(Inches(5), Inches(0.5), Inches(4), Inches(2))
    frame = numbered.text_frame
    for i, text in enumerate(["Plan", "Build", "Ship"]):
        p = frame.paragraphs[0] if i == 0 else frame.add_paragraph()
        p.text = text
        pPr = p._p.get_or_add_pPr()
        pPr.set("marL", "342900")
        pPr.set("indent", "-342900")
        pPr.append(pPr.makeelement(qn("a:buFont"), {"typeface": "+mj-lt"}))
        pPr.append(pPr.makeelement(qn("a:buAutoNum"), {"type": "arabicPeriod"}))
    shrink = slide.shapes.add_textbox(Inches(0.5), Inches(3), Inches(3), Inches(1))
    shrink.text_frame.word_wrap = True
    shrink.text_frame.text = "This text is far too long for its small box, so it shrinks to fit inside the frame."
    shrink.text_frame.auto_size = MSO_AUTO_SIZE.TEXT_TO_FIT_SHAPE
    for run in shrink.text_frame.paragraphs[0].runs:
        run.font.size = Pt(28)
    gradient = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(5), Inches(3), Inches(4), Inches(2))
    gradient.fill.gradient()
    gradient.fill.gradient_angle = 0
    gradient.fill.gradient_stops[0].color.rgb = RGBColor(0xFF, 0xFF, 0xFF)
    gradient.fill.gradient_stops[1].color.rgb = RGBColor(0x00, 0x40, 0xA0)
    gradient.text_frame.text = "Gradient"

    prs.save("basic.pptx")
    normalize("basic.pptx")


def widescreen():
    """A 16:9 deck with a background color and a slide number field."""
    prs = Presentation()
    properties(prs, "Widescreen")
    prs.slide_width = Inches(13.333)
    prs.slide_height = Inches(7.5)
    slide = prs.slides.add_slide(prs.slide_layouts[5])
    slide.shapes.title.text = "Wide slide"
    background = slide.background.fill
    background.solid()
    background.fore_color.rgb = RGBColor(0x10, 0x20, 0x40)
    slide.shapes.title.text_frame.paragraphs[0].runs[0].font.color.rgb = RGBColor(0xFF, 0xFF, 0xFF)
    box = slide.shapes.add_textbox(Inches(11), Inches(6.5), Inches(2), Inches(0.6))
    p = box.text_frame.paragraphs[0]
    run = p.add_run()
    run.text = "Slide "
    run.font.color.rgb = RGBColor(0xFF, 0xFF, 0xFF)
    field = p._p.makeelement(qn("a:fld"), {"id": "{B6F15528-21DE-4FAA-801E-634DDDAF4B2B}", "type": "slidenum"})
    rpr = field.makeelement(qn("a:rPr"), {"lang": "en-US"})
    fill = rpr.makeelement(qn("a:solidFill"), {})
    fill.append(fill.makeelement(qn("a:srgbClr"), {"val": "FFFFFF"}))
    rpr.append(fill)
    field.append(rpr)
    t = field.makeelement(qn("a:t"), {})
    t.text = "9"
    field.append(t)
    p._p.append(field)
    prs.save("widescreen.pptx")
    normalize("widescreen.pptx")


def seeds():
    """Uncompressed copies of the fixtures as fuzzing seeds: mutations then reach the XML (ZIP readers do not check
    the CRC), where compressed data would mostly fail to inflate. Thumbnails and document properties are left out."""
    for name in ["basic", "widescreen"]:
        with zipfile.ZipFile(f"{name}.pptx") as z:
            entries = [(info.filename, z.read(info.filename)) for info in z.infolist()
                       if not info.filename.startswith("docProps/")]
        with zipfile.ZipFile(f"../Fuzz/Seeds/pptx/{name}.pptx", "w", zipfile.ZIP_STORED) as z:
            for entry, data in entries:
                z.writestr(zipfile.ZipInfo(entry, date_time=(2026, 11, 19, 12, 0, 0)), data)


if __name__ == "__main__":
    basic()
    widescreen()
    seeds()

"""Generates the DOCX fixtures of the Documents tests with python-docx (pure Python), adding WordprocessingML that
python-docx cannot write (fields, numbering levels, footnotes, tracked changes, content controls, shading) as XML.
Run with a venv that has python-docx and Pillow: python -m venv venv && venv/bin/pip install python-docx pillow; from this
folder: venv/bin/python make-documents.py. The files are deterministic: fixed dates, no random data.

Also writes expected.json, the paragraph texts python-docx (an independent reader) finds in each fixture, in order, and
small uncompressed copies of hand-written documents as fuzzing seeds (../Fuzz/Seeds/docx), so that mutations reach the
XML."""
import copy
import io
import json
import zipfile

import docx
from docx.enum.section import WD_ORIENT, WD_SECTION
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.enum.text import WD_ALIGN_PARAGRAPH, WD_BREAK, WD_COLOR_INDEX
from docx.opc.constants import RELATIONSHIP_TYPE as RT
from docx.opc.packuri import PackURI
from docx.opc.part import Part
from docx.oxml import parse_xml
from docx.oxml.ns import nsdecls, qn
from docx.shared import Cm, Pt, RGBColor
from PIL import Image

W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"


def png(width, height, color):
    image = Image.new("RGB", (width, height), color)
    for x in range(width):
        image.putpixel((x, x * height // width), (0, 0, 0))
    buffer = io.BytesIO()
    image.save(buffer, "PNG")
    return buffer.getvalue()


def jpeg(width, height, color):
    image = Image.new("RGB", (width, height), color)
    buffer = io.BytesIO()
    image.save(buffer, "JPEG", quality=80)
    return buffer.getvalue()


def field(paragraph, instruction, result):
    """Appends a complex field (begin, instruction, separate, result, end) to a paragraph."""
    p = paragraph._p
    p.append(parse_xml(f'<w:r {nsdecls("w")}><w:fldChar w:fldCharType="begin"/></w:r>'))
    p.append(parse_xml(f'<w:r {nsdecls("w")}><w:instrText xml:space="preserve"> {instruction} </w:instrText></w:r>'))
    p.append(parse_xml(f'<w:r {nsdecls("w")}><w:fldChar w:fldCharType="separate"/></w:r>'))
    p.append(parse_xml(f'<w:r {nsdecls("w")}><w:t>{result}</w:t></w:r>'))
    p.append(parse_xml(f'<w:r {nsdecls("w")}><w:fldChar w:fldCharType="end"/></w:r>'))


def hyperlink(paragraph, url, text):
    """Appends an external hyperlink (a relationship and a w:hyperlink with the Hyperlink character style)."""
    rid = paragraph.part.relate_to(url, RT.HYPERLINK, is_external=True)
    paragraph._p.append(parse_xml(
        f'<w:hyperlink {nsdecls("w", "r")} r:id="{rid}"><w:r><w:rPr><w:rStyle w:val="Hyperlink"/>'
        f'<w:color w:val="0563C1"/><w:u w:val="single"/></w:rPr><w:t>{text}</w:t></w:r></w:hyperlink>'))


def shade(cell, fill):
    cell._tc.get_or_add_tcPr().append(parse_xml(f'<w:shd {nsdecls("w")} w:val="clear" w:color="auto" w:fill="{fill}"/>'))


def save(document, name):
    document.core_properties.author = "RaiCore.Pdf tests"
    document.core_properties.title = name.replace(".docx", "").capitalize() + " fixture"
    document.save(name)
    normalize(name)


def normalize(path):
    """Rewrites a package with fixed entry dates and document dates, so that the output is the same every time."""
    with zipfile.ZipFile(path) as z:
        entries = [(info.filename, z.read(info.filename)) for info in z.infolist()]
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in entries:
            if name == "docProps/core.xml":
                text = data.decode("utf-8")
                for tag in ("dcterms:created", "dcterms:modified"):
                    start = text.find("<" + tag)
                    end = text.find("</" + tag + ">")
                    if start >= 0 and end > start:
                        text = text[:start] + f'<{tag} xsi:type="dcterms:W3CDTF">2026-11-19T12:00:00Z' + text[end:]
                data = text.encode("utf-8")
            info = zipfile.ZipInfo(name, date_time=(2026, 11, 19, 12, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)


def styles():
    """Headings, run formatting, alignment, spacing, line spacing, borders, shading, hyperlinks and bookmarks."""
    d = docx.Document()
    d.add_heading("Document styles", level=0)
    d.add_heading("Heading one", level=1)
    p = d.add_paragraph("Plain text, ")
    p.add_run("bold").bold = True
    p.add_run(", ")
    p.add_run("italic").italic = True
    p.add_run(", ")
    p.add_run("underlined").underline = True
    p.add_run(", ")
    r = p.add_run("struck")
    r.font.strike = True
    p.add_run(", ")
    r = p.add_run("red")
    r.font.color.rgb = RGBColor(0xC0, 0x00, 0x00)
    p.add_run(", ")
    r = p.add_run("highlighted")
    r.font.highlight_color = WD_COLOR_INDEX.YELLOW
    p.add_run(", E=mc")
    r = p.add_run("2")
    r.font.superscript = True
    p.add_run(" and H")
    r = p.add_run("2")
    r.font.subscript = True
    p.add_run("O, ")
    r = p.add_run("large")
    r.font.size = Pt(18)
    p.add_run(" and ")
    r = p.add_run("Courier New")
    r.font.name = "Courier New"
    p.add_run(" and ")
    r = p.add_run("small caps")
    r.font.small_caps = True
    p.add_run(" and ")
    r = p.add_run("all caps")
    r.font.all_caps = True
    p.add_run(".")
    hidden = p.add_run(" Hidden text.")
    hidden.font.hidden = True
    d.add_heading("Heading two", level=2)
    d.add_paragraph("Centered paragraph.").alignment = WD_ALIGN_PARAGRAPH.CENTER
    d.add_paragraph("Right-aligned paragraph.").alignment = WD_ALIGN_PARAGRAPH.RIGHT
    j = d.add_paragraph("Justified paragraph: " + " ".join(["words that wrap across several lines"] * 6) + ".")
    j.alignment = WD_ALIGN_PARAGRAPH.JUSTIFY
    i = d.add_paragraph("Indented paragraph with a hanging indent: " + " ".join(["more text"] * 12) + ".")
    i.paragraph_format.left_indent = Cm(2)
    i.paragraph_format.first_line_indent = Cm(-1)
    s = d.add_paragraph("Double spaced paragraph: " + " ".join(["line spacing"] * 12) + ".")
    s.paragraph_format.line_spacing = 2.0
    s.paragraph_format.space_before = Pt(12)
    s.paragraph_format.space_after = Pt(12)
    b = d.add_paragraph("A paragraph with a border and shading.")
    b._p.get_or_add_pPr().append(parse_xml(
        f'<w:pBdr {nsdecls("w")}><w:top w:val="single" w:sz="8" w:space="1" w:color="2E74B5"/>'
        '<w:left w:val="single" w:sz="8" w:space="4" w:color="2E74B5"/><w:bottom w:val="single" w:sz="8" w:space="1" w:color="2E74B5"/>'
        '<w:right w:val="single" w:sz="8" w:space="4" w:color="2E74B5"/></w:pBdr>'))
    b._p.get_or_add_pPr().append(parse_xml(f'<w:shd {nsdecls("w")} w:val="clear" w:color="auto" w:fill="DEEAF6"/>'))
    h = d.add_paragraph("Visit ")
    hyperlink(h, "https://example.com/docs", "the example site")
    h.add_run(" or jump to ")
    h._p.append(parse_xml(
        f'<w:hyperlink {nsdecls("w")} w:anchor="target"><w:r><w:rPr><w:rStyle w:val="Hyperlink"/></w:rPr><w:t>the target</w:t></w:r></w:hyperlink>'))
    h.add_run(".")
    d.add_heading("Heading three", level=3)
    t = d.add_paragraph()
    t._p.append(parse_xml(f'<w:bookmarkStart {nsdecls("w")} w:id="1" w:name="target"/>'))
    t.add_run("The bookmarked target paragraph.")
    t._p.append(parse_xml(f'<w:bookmarkEnd {nsdecls("w")} w:id="1"/>'))
    tabs = d.add_paragraph("Name:\tValue\tRight")
    tabs._p.get_or_add_pPr().append(parse_xml(
        f'<w:tabs {nsdecls("w")}><w:tab w:val="left" w:pos="2880"/><w:tab w:val="right" w:leader="dot" w:pos="9000"/></w:tabs>'))
    d.add_paragraph("Quote style paragraph.", style="Quote")
    save(d, "styles.docx")


NUMBERING_LEVELS = """
<w:abstractNum {ns} w:abstractNumId="90">
  <w:multiLevelType w:val="multilevel"/>
  <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:lvlJc w:val="left"/>
    <w:pPr><w:ind w:left="360" w:hanging="360"/></w:pPr></w:lvl>
  <w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1.%2."/><w:lvlJc w:val="left"/>
    <w:pPr><w:ind w:left="792" w:hanging="432"/></w:pPr></w:lvl>
  <w:lvl w:ilvl="2"><w:start w:val="1"/><w:numFmt w:val="lowerLetter"/><w:lvlText w:val="%3)"/><w:lvlJc w:val="left"/>
    <w:pPr><w:ind w:left="1224" w:hanging="432"/></w:pPr></w:lvl>
  <w:lvl w:ilvl="3"><w:start w:val="1"/><w:numFmt w:val="upperRoman"/><w:lvlText w:val="%4."/><w:lvlJc w:val="left"/>
    <w:pPr><w:ind w:left="1728" w:hanging="504"/></w:pPr></w:lvl>
</w:abstractNum>
<w:abstractNum {ns} w:abstractNumId="91">
  <w:multiLevelType w:val="hybridMultilevel"/>
  <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="\uf0b7"/><w:lvlJc w:val="left"/>
    <w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr><w:rPr><w:rFonts w:ascii="Symbol" w:hAnsi="Symbol" w:hint="default"/></w:rPr></w:lvl>
  <w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="o"/><w:lvlJc w:val="left"/>
    <w:pPr><w:ind w:left="1440" w:hanging="360"/></w:pPr><w:rPr><w:rFonts w:ascii="Courier New" w:hAnsi="Courier New"/></w:rPr></w:lvl>
  <w:lvl w:ilvl="2"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="\uf0a7"/><w:lvlJc w:val="left"/>
    <w:pPr><w:ind w:left="2160" w:hanging="360"/></w:pPr><w:rPr><w:rFonts w:ascii="Wingdings" w:hAnsi="Wingdings"/></w:rPr></w:lvl>
</w:abstractNum>
<w:abstractNum {ns} w:abstractNumId="92">
  <w:lvl w:ilvl="0"><w:start w:val="5"/><w:numFmt w:val="lowerRoman"/><w:lvlText w:val="(%1)"/><w:lvlJc w:val="left"/>
    <w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl>
</w:abstractNum>
"""


def lists():
    """Multi-level numbering (1. / 1.1. / a) / I.), bullets in three levels, a restart, a start value, list styles."""
    d = docx.Document()
    numbering = d.part.numbering_part.element
    ns = nsdecls("w")
    first_num = numbering.find(qn("w:num"))
    for xml in NUMBERING_LEVELS.strip().split("</w:abstractNum>")[:-1]:
        element = parse_xml(xml.replace("{ns}", ns) + "</w:abstractNum>")
        if first_num is not None:
            first_num.addprevious(element)
        else:
            numbering.append(element)
    for num_id, abstract, start in ((90, 90, None), (91, 91, None), (92, 92, None), (93, 90, 1)):
        override = f'<w:lvlOverride w:ilvl="0"><w:startOverride w:val="{start}"/></w:lvlOverride>' if start else ""
        numbering.append(parse_xml(f'<w:num {ns} w:numId="{num_id}"><w:abstractNumId w:val="{abstract}"/>{override}</w:num>'))

    def item(text, num_id, level):
        p = d.add_paragraph(text)
        p._p.get_or_add_pPr().append(parse_xml(f'<w:numPr {ns}><w:ilvl w:val="{level}"/><w:numId w:val="{num_id}"/></w:numPr>'))
        return p

    d.add_heading("Lists", level=1)
    item("First item", 90, 0)
    item("Second item", 90, 0)
    item("Nested item", 90, 1)
    item("Another nested item", 90, 1)
    item("Lettered item", 90, 2)
    item("Second lettered item", 90, 2)
    item("Roman item", 90, 3)
    item("Back to the top level", 90, 0)
    item("Nested again restarts", 90, 1)
    d.add_paragraph("A paragraph between lists.")
    item("Bullet one", 91, 0)
    item("Bullet two", 91, 0)
    item("Circle bullet", 91, 1)
    item("Square bullet", 91, 2)
    item("Roman from five", 92, 0)
    item("Roman six", 92, 0)
    item("Restarted list", 93, 0)
    item("Restarted second", 93, 0)
    d.add_paragraph("List Bullet style item", style="List Bullet")
    d.add_paragraph("List Number style item", style="List Number")
    d.add_paragraph("List Number style item two", style="List Number")
    save(d, "lists.docx")


def tables():
    """A table with a header row repeated on every page, merged cells, borders, shading, alignment and a nested table."""
    d = docx.Document()
    d.add_heading("Tables", level=1)
    t = d.add_table(rows=3, cols=4)
    t.style = "Table Grid"
    t.alignment = WD_TABLE_ALIGNMENT.CENTER
    a = t.cell(0, 0).merge(t.cell(0, 1))
    a.text = "Merged across"
    b = t.cell(1, 0).merge(t.cell(2, 0))
    b.text = "Merged down"
    t.cell(0, 2).text = "C"
    t.cell(0, 3).text = "D"
    t.cell(1, 1).text = "Shaded"
    shade(t.cell(1, 1), "FFF2CC")
    t.cell(1, 2).text = "Right"
    t.cell(1, 2).paragraphs[0].alignment = WD_ALIGN_PARAGRAPH.RIGHT
    t.cell(1, 3).text = "Nested:"
    inner = t.cell(1, 3).add_table(rows=2, cols=2)
    inner.style = "Table Grid"
    for r in range(2):
        for c in range(2):
            inner.cell(r, c).text = f"n{r}{c}"
    t.cell(2, 1).text = "Bottom"
    t.cell(2, 2).text = "Cells"
    t.cell(2, 3).text = "End"
    d.add_paragraph("A long table follows; its header row repeats on every page.")
    long = d.add_table(rows=1, cols=3)
    long.style = "Table Grid"
    header = long.rows[0]
    header._tr.get_or_add_trPr().append(parse_xml(f'<w:tblHeader {nsdecls("w")}/>'))
    for c, text in enumerate(("Number", "Description", "Amount")):
        header.cells[c].text = text
        header.cells[c].paragraphs[0].runs[0].bold = True
        shade(header.cells[c], "D9E2F3")
    for i in range(1, 81):
        row = long.add_row()
        row.cells[0].text = f"Row {i}"
        row.cells[1].text = f"Item number {i} of the long table"
        row.cells[2].text = f"{i * 12.5:.2f}"
        row.cells[2].paragraphs[0].alignment = WD_ALIGN_PARAGRAPH.RIGHT
    d.add_paragraph("After the long table.")
    styled = d.add_table(rows=4, cols=3)
    styled.style = "Light Grid Accent 1"
    for r in range(4):
        for c in range(3):
            styled.cell(r, c).text = f"S{r}{c}"
    save(d, "tables.docx")


def sections():
    """Three sections: portrait with a title page, landscape, portrait again with roman page numbers; headers and
    footers for the first, even and odd pages with PAGE, NUMPAGES and SECTIONPAGES fields."""
    d = docx.Document()
    d.settings.odd_and_even_pages_header_footer = True
    s1 = d.sections[0]
    s1.different_first_page_header_footer = True
    s1.first_page_header.paragraphs[0].text = "First page header"
    s1.header.paragraphs[0].text = "Odd page header"
    s1.even_page_header.paragraphs[0].text = "Even page header"
    f = s1.footer.paragraphs[0]
    f.text = "Page "
    field(f, "PAGE", "1")
    f.add_run(" of ")
    field(f, "NUMPAGES", "1")
    f.alignment = WD_ALIGN_PARAGRAPH.CENTER
    e = s1.even_page_footer.paragraphs[0]
    e.text = "Even page "
    field(e, "PAGE", "2")
    d.add_heading("Title page", level=1)
    d.add_paragraph("The first section starts with a title page.")
    for i in range(1, 4):
        p = d.add_paragraph(f"Page break {i} follows.")
        p.add_run().add_break(WD_BREAK.PAGE)
    d.add_paragraph("Last page of the first section.")
    s2 = d.add_section(WD_SECTION.NEW_PAGE)
    s2.orientation = WD_ORIENT.LANDSCAPE
    s2.page_width, s2.page_height = s2.page_height, s2.page_width
    s2.different_first_page_header_footer = False
    s2.header.is_linked_to_previous = False
    s2.header.paragraphs[0].text = "Landscape header"
    d.add_heading("Landscape section", level=1)
    d.add_paragraph("This section is in landscape orientation. " * 8)
    s3 = d.add_section(WD_SECTION.NEW_PAGE)
    s3.orientation = WD_ORIENT.PORTRAIT
    s3.page_width, s3.page_height = s3.page_height, s3.page_width
    s3.different_first_page_header_footer = False
    s3.footer.is_linked_to_previous = False
    g = s3.footer.paragraphs[0]
    g.text = "Section page "
    field(g, "PAGE \\* roman", "i")
    g.add_run(" of ")
    field(g, "SECTIONPAGES", "1")
    sect = s3._sectPr
    sect.append(parse_xml(f'<w:pgNumType {nsdecls("w")} w:fmt="lowerRoman" w:start="1"/>'))
    d.add_heading("Third section", level=1)
    d.add_paragraph("Page numbers restart in roman numerals.")
    save(d, "sections.docx")


def images():
    """Inline PNG and JPEG images, an image floating at the right with text wrapping, and an image in the header."""
    d = docx.Document()
    with open("_red.png", "wb") as file:
        file.write(png(120, 80, (220, 60, 60)))
    with open("_blue.jpg", "wb") as file:
        file.write(jpeg(160, 90, (60, 90, 220)))
    d.add_heading("Images", level=1)
    d.add_paragraph("An inline PNG image follows.")
    d.add_picture("_red.png", width=Cm(4))
    p = d.add_paragraph("A JPEG in the line: ")
    p.add_run().add_picture("_blue.jpg", width=Cm(2))
    p.add_run(" after the image.")
    # A floating image: an inline drawing rewritten as an anchor with square wrapping, aligned right.
    f = d.add_paragraph()
    run = f.add_run()
    run.add_picture("_red.png", width=Cm(3))
    inline = run._r.find(".//" + qn("wp:inline"))
    extent = inline.find(qn("wp:extent"))
    graphic = inline.find(qn("a:graphic"))
    docpr = inline.find(qn("wp:docPr"))
    docpr.set("descr", "A red square")
    anchor = parse_xml(
        f'<wp:anchor {nsdecls("wp")} distT="0" distB="0" distL="114300" distR="114300" simplePos="0" relativeHeight="1" '
        'behindDoc="0" locked="0" layoutInCell="1" allowOverlap="1"><wp:simplePos x="0" y="0"/>'
        '<wp:positionH relativeFrom="margin"><wp:align>right</wp:align></wp:positionH>'
        '<wp:positionV relativeFrom="paragraph"><wp:posOffset>0</wp:posOffset></wp:positionV></wp:anchor>')
    anchor.append(copy.deepcopy(extent))
    anchor.append(parse_xml(f'<wp:wrapSquare {nsdecls("wp")} wrapText="bothSides"/>'))
    anchor.append(copy.deepcopy(docpr))
    anchor.append(copy.deepcopy(graphic))
    inline.getparent().replace(inline, anchor)
    f.add_run("This paragraph wraps around a floating image at the right margin. " * 6)
    header = d.sections[0].header.paragraphs[0]
    header.add_run().add_picture("_blue.jpg", height=Cm(0.8))
    header.add_run(" Header with a logo")
    save(d, "images.docx")


def fields():
    """A table of contents (TOC with hyperlinked PAGEREF fields), a HYPERLINK field, a simple field, tracked changes,
    content controls, footnotes and endnotes."""
    d = docx.Document()
    toc = d.add_paragraph()
    p = toc._p
    ns = nsdecls("w")
    p.append(parse_xml(f'<w:r {ns}><w:fldChar w:fldCharType="begin"/></w:r>'))
    p.append(parse_xml(f'<w:r {ns}><w:instrText xml:space="preserve"> TOC \\o "1-3" \\h \\z \\u </w:instrText></w:r>'))
    p.append(parse_xml(f'<w:r {ns}><w:fldChar w:fldCharType="separate"/></w:r>'))
    for number, (title, bookmark) in enumerate((("Introduction", "_Toc1"), ("Details", "_Toc2")), start=1):
        entry = toc if number == 1 else d.add_paragraph()
        entry._p.get_or_add_pPr().append(parse_xml(f'<w:tabs {ns}><w:tab w:val="right" w:leader="dot" w:pos="9350"/></w:tabs>'))
        entry._p.append(parse_xml(
            f'<w:hyperlink {ns} w:anchor="{bookmark}" w:history="1"><w:r><w:t>{title}</w:t></w:r><w:r><w:tab/></w:r>'
            f'<w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText xml:space="preserve"> PAGEREF {bookmark} \\h </w:instrText></w:r>'
            f'<w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>99</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r></w:hyperlink>'))
        if number == 2:
            entry._p.append(parse_xml(f'<w:r {ns}><w:fldChar w:fldCharType="end"/></w:r>'))
    for title, bookmark in (("Introduction", "_Toc1"), ("Details", "_Toc2")):
        h = d.add_paragraph(style="Heading 1")
        h._p.append(parse_xml(f'<w:bookmarkStart {ns} w:id="{bookmark[-1]}" w:name="{bookmark}"/>'))
        h.add_run(title)
        h._p.append(parse_xml(f'<w:bookmarkEnd {ns} w:id="{bookmark[-1]}"/>'))
        body = d.add_paragraph(f"Text of the {title.lower()} section.")
        if title == "Introduction":
            body.add_run().add_break(WD_BREAK.PAGE)
    link = d.add_paragraph("A field link: ")
    link._p.append(parse_xml(f'<w:r {ns}><w:fldChar w:fldCharType="begin"/></w:r>'))
    link._p.append(parse_xml(f'<w:r {ns}><w:instrText xml:space="preserve"> HYPERLINK "https://example.org/field" </w:instrText></w:r>'))
    link._p.append(parse_xml(f'<w:r {ns}><w:fldChar w:fldCharType="separate"/></w:r>'))
    link._p.append(parse_xml(f'<w:r {ns}><w:t>example.org</w:t></w:r>'))
    link._p.append(parse_xml(f'<w:r {ns}><w:fldChar w:fldCharType="end"/></w:r>'))
    simple = d.add_paragraph("Author: ")
    simple._p.append(parse_xml(f'<w:fldSimple {ns} w:instr=" AUTHOR "><w:r><w:t>Jane Writer</w:t></w:r></w:fldSimple>'))
    changes = d.add_paragraph("Tracked: ")
    changes._p.append(parse_xml(f'<w:ins {ns} w:id="10" w:author="A" w:date="2026-11-19T00:00:00Z"><w:r><w:t>inserted text</w:t></w:r></w:ins>'))
    changes._p.append(parse_xml(f'<w:del {ns} w:id="11" w:author="A" w:date="2026-11-19T00:00:00Z"><w:r><w:delText>deleted text</w:delText></w:r></w:del>'))
    changes.add_run(" kept.")
    sdt = d.add_paragraph("Control: ")
    sdt._p.append(parse_xml(
        f'<w:sdt {ns}><w:sdtPr><w:alias w:val="Name"/></w:sdtPr><w:sdtContent><w:r><w:t>content control text</w:t></w:r></w:sdtContent></w:sdt>'))
    block = parse_xml(
        f'<w:sdt {ns}><w:sdtPr><w:alias w:val="Block"/></w:sdtPr><w:sdtContent><w:p><w:r><w:t>Block content control paragraph.</w:t></w:r></w:p></w:sdtContent></w:sdt>')
    d.element.body.insert(len(d.element.body) - 1, block)
    note = d.add_paragraph("A sentence with a footnote")
    note._p.append(parse_xml(
        f'<w:r {ns}><w:rPr><w:vertAlign w:val="superscript"/></w:rPr><w:footnoteReference w:id="1"/></w:r>'))
    note.add_run(" and an endnote")
    note._p.append(parse_xml(
        f'<w:r {ns}><w:rPr><w:vertAlign w:val="superscript"/></w:rPr><w:endnoteReference w:id="1"/></w:r>'))
    note.add_run(".")
    footnotes = (f'<w:footnotes {nsdecls("w")}><w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>'
                 '<w:footnote w:type="continuationSeparator" w:id="0"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:footnote>'
                 '<w:footnote w:id="1"><w:p><w:r><w:rPr><w:vertAlign w:val="superscript"/></w:rPr><w:footnoteRef/></w:r>'
                 '<w:r><w:t xml:space="preserve"> The footnote text.</w:t></w:r></w:p></w:footnote></w:footnotes>')
    endnotes = (f'<w:endnotes {nsdecls("w")}><w:endnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:endnote>'
                '<w:endnote w:id="1"><w:p><w:r><w:rPr><w:vertAlign w:val="superscript"/></w:rPr><w:endnoteRef/></w:r>'
                '<w:r><w:t xml:space="preserve"> The endnote text.</w:t></w:r></w:p></w:endnote></w:endnotes>')
    for name, xml, rel, content_type in (
        ("/word/footnotes.xml", footnotes, RT.FOOTNOTES, "application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml"),
        ("/word/endnotes.xml", endnotes, RT.ENDNOTES, "application/vnd.openxmlformats-officedocument.wordprocessingml.endnotes+xml"),
    ):
        part = Part(PackURI(name), content_type, xml.encode("utf-8"), d.part.package)
        d.part.relate_to(part, rel)
    save(d, "fields.docx")


# Hand-written documents: small packages with exactly the parts they need (fuzzing seeds and edge cases).

CONTENT_TYPES = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                 '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                 '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                 '<Default Extension="xml" ContentType="application/xml"/><Default Extension="png" ContentType="image/png"/>'
                 '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
                 '</Types>')
PACKAGE_RELS = ('<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                f'<Relationship Id="rId1" Type="{R}/officeDocument" Target="word/document.xml"/></Relationships>')


def package(path, document, parts=None, relationships=None, compression=zipfile.ZIP_DEFLATED, main=W, rel_ns=R):
    rels = "".join(f'<Relationship Id="{rid}" Type="{rel_ns}/{kind}" Target="{target}"{mode}/>'
                   for rid, kind, target, mode in (relationships or []))
    with zipfile.ZipFile(path, "w", compression) as z:
        def write(name, data):
            info = zipfile.ZipInfo(name, date_time=(2026, 11, 19, 12, 0, 0))
            info.compress_type = compression
            z.writestr(info, data)
        write("[Content_Types].xml", CONTENT_TYPES)
        write("_rels/.rels", PACKAGE_RELS.replace(R, rel_ns))
        write("word/document.xml", f'<w:document xmlns:w="{main}" xmlns:r="{rel_ns}"><w:body>{document}</w:body></w:document>')
        write("word/_rels/document.xml.rels",
              f'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{rels}</Relationships>')
        for name, data in (parts or {}).items():
            write(name, data)


MINIMAL = '<w:p><w:r><w:t>Hello, minimal document.</w:t></w:r></w:p>'

FEATURES = (
    '<w:p><w:pPr><w:pStyle w:val="Title"/></w:pPr><w:r><w:t>Seed</w:t></w:r></w:p>'
    '<w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>Item</w:t></w:r></w:p>'
    '<w:p><w:pPr><w:numPr><w:ilvl w:val="1"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>Sub</w:t></w:r></w:p>'
    '<w:p><w:r><w:rPr><w:b/><w:color w:val="FF0000"/><w:sz w:val="28"/></w:rPr><w:t xml:space="preserve">Bold </w:t></w:r>'
    '<w:r><w:tab/><w:t>tab</w:t><w:br/><w:t>line</w:t></w:r>'
    '<w:hyperlink w:anchor="b"><w:r><w:t>link</w:t></w:r></w:hyperlink><w:bookmarkStart w:id="0" w:name="b"/>'
    '<w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText> PAGEREF b </w:instrText></w:r>'
    '<w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r>'
    '<w:r><w:footnoteReference w:id="1"/></w:r><w:r><w:drawing><wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing">'
    '<wp:extent cx="381000" cy="381000"/><wp:docPr id="1" name="p"/><a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">'
    '<a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">'
    '<pic:blipFill><a:blip r:embed="rId9"/></pic:blipFill></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>'
    '<w:tbl><w:tblPr><w:tblBorders><w:top w:val="single" w:sz="4"/><w:insideH w:val="single" w:sz="4"/></w:tblBorders></w:tblPr>'
    '<w:tblGrid><w:gridCol w:w="2000"/><w:gridCol w:w="2000"/></w:tblGrid>'
    '<w:tr><w:trPr><w:tblHeader/></w:trPr><w:tc><w:tcPr><w:gridSpan w:val="2"/></w:tcPr><w:p><w:r><w:t>H</w:t></w:r></w:p></w:tc></w:tr>'
    '<w:tr><w:tc><w:tcPr><w:vMerge w:val="restart"/><w:shd w:fill="FFFF00"/></w:tcPr><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc>'
    '<w:tc><w:p><w:r><w:t>B</w:t></w:r></w:p></w:tc></w:tr>'
    '<w:tr><w:tc><w:tcPr><w:vMerge/></w:tcPr><w:p/></w:tc><w:tc><w:p><w:r><w:t>C</w:t></w:r></w:p></w:tc></w:tr></w:tbl>'
    '<w:p><w:pPr><w:sectPr><w:headerReference w:type="default" r:id="rId3"/><w:pgSz w:w="12240" w:h="15840"/>'
    '<w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="720" w:footer="720"/></w:sectPr></w:pPr></w:p>'
    '<w:p><w:r><w:t>Landscape</w:t></w:r></w:p>'
    '<w:sectPr><w:pgSz w:w="15840" w:h="12240" w:orient="landscape"/><w:pgMar w:top="720" w:right="720" w:bottom="720" w:left="720"/></w:sectPr>'
)

MORE = (
    '<w:p><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText> TOC \\o "1-3" \\h </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r>'
    '<w:hyperlink w:anchor="_Toc1"><w:r><w:t>Entry</w:t></w:r><w:r><w:tab/></w:r><w:r><w:fldChar w:fldCharType="begin"/></w:r>'
    '<w:r><w:instrText> PAGEREF _Toc1 \\h </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>9</w:t></w:r>'
    '<w:r><w:fldChar w:fldCharType="end"/></w:r></w:hyperlink></w:p>'
    '<w:p><w:r><w:fldChar w:fldCharType="end"/></w:r></w:p>'
    '<w:p><w:pPr><w:pStyle w:val="Heading1"/><w:pageBreakBefore/><w:keepNext/></w:pPr><w:bookmarkStart w:id="1" w:name="_Toc1"/><w:r><w:t>Heading</w:t></w:r></w:p>'
    '<w:p><w:pPr><w:pBdr><w:top w:val="double" w:sz="6"/><w:bottom w:val="dotted" w:sz="4" w:color="FF0000"/></w:pBdr><w:shd w:val="pct25" w:fill="FFFFFF"/>'
    '<w:tabs><w:tab w:val="center" w:pos="4680"/><w:tab w:val="decimal" w:pos="8000"/></w:tabs><w:spacing w:line="360" w:lineRule="exact"/></w:pPr>'
    '<w:r><w:t>a</w:t></w:r><w:r><w:tab/><w:t>b</w:t><w:tab/><w:t>12.50</w:t></w:r>'
    '<w:ins w:id="2" w:author="x"><w:r><w:t>ins</w:t></w:r></w:ins><w:del w:id="3" w:author="x"><w:r><w:delText>del</w:delText></w:r></w:del>'
    '<w:sdt><w:sdtContent><w:r><w:rPr><w:caps/><w:smallCaps/><w:vertAlign w:val="superscript"/><w:highlight w:val="cyan"/></w:rPr><w:t>sdt</w:t></w:r></w:sdtContent></w:sdt>'
    '<w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText>HYPERLINK "https://example.com" \\o "tip"</w:instrText></w:r>'
    '<w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>web</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r>'
    '<w:r><w:sym w:font="Wingdings" w:char="F0FC"/><w:noBreakHyphen/><w:softHyphen/><w:ptab w:alignment="right" w:relativeTo="margin" w:leader="none"/></w:r>'
    '<w:r><w:pict><v:shape xmlns:v="urn:schemas-microsoft-com:vml" style="width:20pt;height:10pt"><v:textbox><w:txbxContent><w:p><w:r><w:t>box</w:t></w:r></w:p>'
    '</w:txbxContent></v:textbox></v:shape></w:pict></w:r>'
    '<m:oMath xmlns:m="http://schemas.openxmlformats.org/officeDocument/2006/math"><m:r><m:t>x=1</m:t></m:r></m:oMath></w:p>'
    '<w:tbl><w:tblPr><w:tblStyle w:val="Grid"/><w:tblW w:w="2500" w:type="pct"/><w:jc w:val="center"/><w:tblLook w:val="04A0"/></w:tblPr>'
    '<w:tblGrid><w:gridCol w:w="1000"/><w:gridCol w:w="1000"/><w:gridCol w:w="1000"/></w:tblGrid>'
    '<w:tr><w:trPr><w:tblHeader/><w:cantSplit/><w:trHeight w:val="400" w:hRule="exact"/></w:trPr><w:tc><w:p><w:r><w:t>1</w:t></w:r></w:p></w:tc>'
    '<w:tc><w:tcPr><w:gridSpan w:val="2"/><w:vAlign w:val="center"/><w:tcMar><w:left w:w="200" w:type="dxa"/></w:tcMar></w:tcPr><w:p/></w:tc></w:tr>'
    '<w:tr><w:trPr><w:gridBefore w:val="1"/></w:trPr><w:tc><w:tcPr><w:tcBorders><w:top w:val="nil"/><w:left w:val="thick" w:sz="12"/></w:tcBorders></w:tcPr>'
    '<w:tbl><w:tblGrid><w:gridCol w:w="500"/></w:tblGrid><w:tr><w:tc><w:p><w:r><w:t>in</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:p/></w:tc>'
    '<w:tc><w:tcPr><w:hMerge w:val="restart"/></w:tcPr><w:p/></w:tc></w:tr></w:tbl>'
    '<w:p><w:pPr><w:sectPr><w:titlePg/><w:pgNumType w:start="3" w:fmt="upperRoman"/><w:cols w:num="2"/></w:sectPr></w:pPr></w:p>'
    '<w:p><w:r><w:br w:type="page"/><w:t>end</w:t></w:r></w:p>'
    '<w:sectPr><w:type w:val="oddPage"/><w:pgSz w:w="11906" w:h="16838"/></w:sectPr>'
)

MORE_STYLES = (f'<w:styles xmlns:w="{W}"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:asciiTheme="minorHAnsi"/><w:sz w:val="22"/><w:lang w:val="en-US"/></w:rPr>'
               '</w:rPrDefault></w:docDefaults>'
               '<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:pPr><w:spacing w:after="120" w:line="259" w:lineRule="auto"/><w:contextualSpacing/></w:pPr></w:style>'
               '<w:style w:type="paragraph" w:styleId="Heading1"><w:basedOn w:val="Normal"/><w:pPr><w:outlineLvl w:val="0"/></w:pPr>'
               '<w:rPr><w:rFonts w:asciiTheme="majorHAnsi"/><w:b/><w:color w:val="2F5496" w:themeColor="accent1"/><w:sz w:val="32"/></w:rPr></w:style>'
               '<w:style w:type="table" w:default="1" w:styleId="Grid"><w:tblPr><w:tblBorders><w:top w:val="single" w:sz="4"/><w:left w:val="single" w:sz="4"/>'
               '<w:bottom w:val="single" w:sz="4"/><w:right w:val="single" w:sz="4"/><w:insideH w:val="single" w:sz="4"/><w:insideV w:val="single" w:sz="4"/>'
               '</w:tblBorders><w:tblCellMar><w:left w:w="108" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr>'
               '<w:tblStylePr w:type="firstRow"><w:rPr><w:b/></w:rPr><w:tcPr><w:shd w:val="clear" w:fill="4472C4" w:themeFill="accent1"/></w:tcPr></w:tblStylePr>'
               '<w:tblStylePr w:type="band1Horz"><w:tcPr><w:shd w:val="clear" w:fill="D9E2F3"/></w:tcPr></w:tblStylePr></w:style></w:styles>')
MORE_THEME = ('<a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="T"><a:themeElements><a:clrScheme name="C">'
              '<a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:accent1><a:srgbClr val="4472C4"/></a:accent1></a:clrScheme>'
              '<a:fontScheme name="F"><a:majorFont><a:latin typeface="Times New Roman"/></a:majorFont><a:minorFont><a:latin typeface="Courier New"/></a:minorFont>'
              '</a:fontScheme></a:themeElements></a:theme>')
MORE_SETTINGS = (f'<w:settings xmlns:w="{W}"><w:evenAndOddHeaders/><w:defaultTabStop w:val="708"/><w:footnotePr><w:numFmt w:val="lowerLetter"/></w:footnotePr>'
                 '</w:settings>')


SEED_STYLES = (f'<w:styles xmlns:w="{W}"><w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val="22"/></w:rPr></w:rPrDefault>'
               '<w:pPrDefault><w:pPr><w:spacing w:after="160"/></w:pPr></w:pPrDefault></w:docDefaults>'
               '<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>'
               '<w:style w:type="paragraph" w:styleId="Title"><w:basedOn w:val="Normal"/><w:pPr><w:jc w:val="center"/><w:outlineLvl w:val="0"/></w:pPr>'
               '<w:rPr><w:sz w:val="48"/></w:rPr></w:style></w:styles>')
SEED_NUMBERING = (f'<w:numbering xmlns:w="{W}"><w:abstractNum w:abstractNumId="0">'
                  '<w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl>'
                  '<w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="lowerLetter"/><w:lvlText w:val="%1.%2"/><w:pPr><w:ind w:left="1440" w:hanging="360"/></w:pPr></w:lvl>'
                  '</w:abstractNum><w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num></w:numbering>')
SEED_HEADER = (f'<w:hdr xmlns:w="{W}"><w:p><w:r><w:t xml:space="preserve">Page </w:t></w:r><w:fldSimple w:instr="PAGE"><w:r><w:t>1</w:t></w:r></w:fldSimple>'
               '<w:r><w:t xml:space="preserve"> of </w:t></w:r><w:fldSimple w:instr="NUMPAGES"><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p></w:hdr>')
SEED_FOOTNOTES = f'<w:footnotes xmlns:w="{W}"><w:footnote w:id="1"><w:p><w:r><w:footnoteRef/><w:t xml:space="preserve"> Note.</w:t></w:r></w:p></w:footnote></w:footnotes>'
SEED_RELATIONSHIPS = [
    ("rId1", "styles", "styles.xml", ""),
    ("rId2", "numbering", "numbering.xml", ""),
    ("rId3", "header", "header1.xml", ""),
    ("rId4", "footnotes", "footnotes.xml", ""),
    ("rId9", "image", "media/dot.png", ""),
]


def seed_parts():
    return {
        "word/styles.xml": SEED_STYLES,
        "word/numbering.xml": SEED_NUMBERING,
        "word/header1.xml": SEED_HEADER,
        "word/footnotes.xml": SEED_FOOTNOTES,
        "word/media/dot.png": png(4, 4, (0, 128, 0)),
    }


def hand_written(folder, compression):
    package(f"{folder}/minimal.docx", MINIMAL, compression=compression)
    package(f"{folder}/more.docx", MORE, {
        "word/styles.xml": MORE_STYLES, "word/theme/theme1.xml": MORE_THEME, "word/settings.xml": MORE_SETTINGS,
        "word/header1.xml": SEED_HEADER,
    }, [("rId1", "styles", "styles.xml", ""), ("rId2", "theme", "theme/theme1.xml", ""), ("rId3", "settings", "settings.xml", ""),
        ("rId4", "header", "header1.xml", "")], compression)
    package(f"{folder}/features.docx", FEATURES, seed_parts(), SEED_RELATIONSHIPS, compression)
    strict = "http://purl.oclc.org/ooxml/wordprocessingml/main"
    strict_rels = "http://purl.oclc.org/ooxml/officeDocument/relationships"
    package(f"{folder}/strict.docx", MINIMAL.replace("minimal", "strict") + '<w:p><w:r><w:rPr><w:i/></w:rPr><w:t>Italic</w:t></w:r></w:p>',
            compression=compression, main=strict, rel_ns=strict_rels)


# Apache POI samples (see ../Poi/README.md) that python-docx reads: their texts are expected too.
POI = ["ComplexNumberedLists.docx", "ThreeColHeadFoot.docx", "DiffFirstPageHeadFoot.docx", "footnotes.docx", "table-alignment.docx",
       "IllustrativeCases.docx"]


def expected(names):
    """The paragraph texts python-docx finds in the body, in order: a string per paragraph, and a list per table row
    with the texts of its cells (nested tables included), whose lines a PDF text extractor may interleave."""
    result = {}
    for name in names:
        d = docx.Document(name)

        def text_of(paragraph):
            parts = []
            for item in paragraph.iter_inner_content():
                if isinstance(item, docx.text.run.Run):
                    if not item.font.hidden:
                        parts.append(item.text)
                else:
                    parts.append(item.text)
            return "".join(parts).strip()

        seen = set()

        def walk(container, out):
            for child in container.iter_inner_content():
                if isinstance(child, docx.text.paragraph.Paragraph):
                    if text_of(child):
                        out.append(text_of(child))
                else:
                    for row in child.rows:
                        cells = []
                        for cell in row.cells:
                            if cell._tc in seen:
                                continue
                            seen.add(cell._tc)
                            walk(cell, cells)
                        flat = []
                        for item in cells:
                            flat.extend(item if isinstance(item, list) else [item])
                        if flat:
                            out.append(flat)

        texts = []
        walk(d, texts)
        result[name] = texts
    return result


if __name__ == "__main__":
    import os

    styles()
    lists()
    tables()
    sections()
    images()
    fields()
    for temporary in ("_red.png", "_blue.jpg"):
        os.remove(temporary)
    hand_written(".", zipfile.ZIP_DEFLATED)
    os.makedirs("../Fuzz/Seeds/docx", exist_ok=True)
    hand_written("../Fuzz/Seeds/docx", zipfile.ZIP_STORED)
    with open("expected.json", "w", encoding="utf-8") as file:
        names = ["styles.docx", "lists.docx", "tables.docx", "sections.docx", "images.docx", "fields.docx"]
        names += ["../Poi/" + name for name in POI]
        json.dump(expected(names), file, indent=1, ensure_ascii=True)
        file.write("\n")

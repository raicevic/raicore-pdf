"""Generates the OpenDocument text fixtures of the Documents tests: hand-written ODF XML in ZIP packages (mimetype first
and stored, as ODF requires), a flat XML document (.fodt) and a template (.ott), covering styles, lists, tables, page
styles, fields, notes and images. Run from this folder with a Python that has Pillow (for the JPEG, GIF and BMP
images): python -m venv venv && venv/bin/pip install pillow && venv/bin/python make-odt.py. The files are
deterministic: fixed dates, no random data.

Also writes small uncompressed (stored) copies of the documents as fuzzing seeds (../../Fuzz/Seeds/odt), so that
mutations reach the XML. The paragraph texts an independent reader (odfpy) finds are in expected.json
(make-expected.py)."""
import base64
import io
import os
import struct
import zipfile
import zlib

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SEEDS = os.path.join(HERE, "..", "..", "Fuzz", "Seeds", "odt")
DATE = (2026, 1, 1, 0, 0, 0)
TEXT = "application/vnd.oasis.opendocument.text"

NAMESPACES = (
    'xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" '
    'xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" '
    'xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" '
    'xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" '
    'xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" '
    'xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" '
    'xmlns:xlink="http://www.w3.org/1999/xlink" '
    'xmlns:dc="http://purl.org/dc/elements/1.1/" '
    'xmlns:meta="urn:oasis:names:tc:opendocument:xmlns:meta:1.0" '
    'xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0" '
    'xmlns:config="urn:oasis:names:tc:opendocument:xmlns:config:1.0" '
    'xmlns:loext="urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0"'
)


def png(width, height, color):
    """A PNG written by hand (deterministic): a solid color with a black diagonal."""
    rows = b""
    for y in range(height):
        row = bytearray()
        for x in range(width):
            row += bytes((0, 0, 0)) if x * height // width == y else bytes(color)
        rows += b"\x00" + bytes(row)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def pillow(width, height, color, kind):
    image = Image.new("RGB", (width, height), color)
    buffer = io.BytesIO()
    image.save(buffer, kind, **({"quality": 80} if kind == "JPEG" else {}))
    return buffer.getvalue()


SVG = (b'<svg xmlns="http://www.w3.org/2000/svg" width="40" height="20" viewBox="0 0 40 20">'
       b'<rect width="40" height="20" fill="#00a0ff"/></svg>')
WMF = bytes([0xD7, 0xCD, 0xC6, 0x9A]) + bytes(40)


def manifest(files, extra=""):
    entries = "".join(f'<manifest:file-entry manifest:full-path="{name}" manifest:media-type=""/>' for name in files)
    return ('<?xml version="1.0" encoding="UTF-8"?><manifest:manifest '
            'xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.3">'
            f'<manifest:file-entry manifest:full-path="/" manifest:media-type="{TEXT}"/>{entries}{extra}</manifest:manifest>')


def content(body, automatic="", fonts=""):
    return (f'<?xml version="1.0" encoding="UTF-8"?><office:document-content {NAMESPACES} office:version="1.3">'
            f'<office:font-face-decls>{fonts}</office:font-face-decls>'
            f'<office:automatic-styles>{automatic}</office:automatic-styles>'
            f'<office:body><office:text>{body}</office:text></office:body></office:document-content>')


def styles(common="", automatic="", masters="", fonts=""):
    return (f'<?xml version="1.0" encoding="UTF-8"?><office:document-styles {NAMESPACES} office:version="1.3">'
            f'<office:font-face-decls>{fonts}</office:font-face-decls>'
            f'<office:styles>{common}</office:styles><office:automatic-styles>{automatic}</office:automatic-styles>'
            f'<office:master-styles>{masters}</office:master-styles></office:document-styles>')


def meta(title=None, creator=None):
    parts = (f"<dc:title>{title}</dc:title>" if title else "") + (f"<dc:creator>{creator}</dc:creator>" if creator else "")
    return (f'<?xml version="1.0" encoding="UTF-8"?><office:document-meta {NAMESPACES} office:version="1.3">'
            f'<office:meta><meta:generator>make-odt.py</meta:generator>{parts}</office:meta></office:document-meta>')


def write(name, parts, mimetype=TEXT, stored=False, folder=HERE, extra_manifest=""):
    """Writes a package: mimetype first and stored, then the parts in order, with fixed dates."""
    files = [n for n, _ in parts]
    path = os.path.join(folder, name)
    with zipfile.ZipFile(path, "w") as z:
        info = zipfile.ZipInfo("mimetype", DATE)
        info.compress_type = zipfile.ZIP_STORED
        z.writestr(info, mimetype)
        for part, data in list(parts) + [("META-INF/manifest.xml", manifest(files, extra_manifest))]:
            info = zipfile.ZipInfo(part, DATE)
            info.compress_type = zipfile.ZIP_STORED if stored else zipfile.ZIP_DEFLATED
            z.writestr(info, data.encode("utf-8") if isinstance(data, str) else data)


FONTS = ('<style:font-face style:name="Serif Face" svg:font-family="&apos;Liberation Serif&apos;"/>'
         '<style:font-face style:name="Sans Face" svg:font-family="&apos;Liberation Sans&apos;, sans-serif"/>'
         '<style:font-face style:name="Mono Face" svg:font-family="&apos;Liberation Mono&apos;"/>'
         '<style:font-face style:name="OpenSymbol" svg:font-family="OpenSymbol"/>')

COMMON = (
    '<style:default-style style:family="paragraph"><style:paragraph-properties style:tab-stop-distance="1.25cm" fo:orphans="2" fo:widows="2"/>'
    '<style:text-properties style:font-name="Serif Face" fo:font-size="12pt" fo:language="en" fo:country="GB"/></style:default-style>'
    '<style:default-style style:family="graphic"><style:graphic-properties style:wrap="none"/></style:default-style>'
    '<style:style style:name="Standard" style:family="paragraph"/>'
    '<style:style style:name="Text_20_body" style:display-name="Text body" style:family="paragraph" style:parent-style-name="Standard">'
    '<style:paragraph-properties fo:margin-top="0cm" fo:margin-bottom="0.25cm"/></style:style>'
    '<style:style style:name="Heading" style:family="paragraph" style:parent-style-name="Standard">'
    '<style:paragraph-properties fo:margin-top="0.42cm" fo:margin-bottom="0.21cm" fo:keep-with-next="always"/>'
    '<style:text-properties style:font-name="Sans Face" fo:font-size="14pt"/></style:style>'
    '<style:style style:name="Heading_20_1" style:display-name="Heading 1" style:family="paragraph" style:parent-style-name="Heading" style:default-outline-level="1">'
    '<style:text-properties fo:font-size="130%" fo:font-weight="bold"/></style:style>'
    '<style:style style:name="Heading_20_2" style:display-name="Heading 2" style:family="paragraph" style:parent-style-name="Heading" style:default-outline-level="2">'
    '<style:text-properties fo:font-size="115%" fo:font-weight="bold" fo:font-style="italic"/></style:style>'
    '<style:style style:name="Quote" style:family="paragraph" style:parent-style-name="Text_20_body">'
    '<style:paragraph-properties fo:margin-left="1cm" fo:margin-right="1cm"/><style:text-properties fo:font-style="italic"/></style:style>'
    '<style:style style:name="Header" style:family="paragraph" style:parent-style-name="Standard"><style:text-properties fo:font-size="10pt"/></style:style>'
    '<style:style style:name="Footer" style:family="paragraph" style:parent-style-name="Standard">'
    '<style:paragraph-properties fo:text-align="center"/><style:text-properties fo:font-size="10pt"/></style:style>'
    '<style:style style:name="Footnote" style:family="paragraph" style:parent-style-name="Standard"><style:text-properties fo:font-size="10pt"/></style:style>'
    '<style:style style:name="Strong" style:family="text"><style:text-properties fo:font-weight="bold"/></style:style>'
    '<style:style style:name="Big_20_Strong" style:family="text" style:parent-style-name="Strong"><style:text-properties fo:font-size="18pt"/></style:style>'
    '<style:style style:name="Footnote_20_anchor" style:family="text"><style:text-properties style:text-position="super 58%"/></style:style>'
    '<style:style style:name="Internet_20_link" style:family="text"><style:text-properties fo:color="#000080" style:text-underline-style="solid"/></style:style>'
    '<text:outline-style style:name="Outline">'
    '<text:outline-level-style text:level="1" style:num-format=""/>'
    '<text:outline-level-style text:level="2" style:num-format=""/>'
    '</text:outline-style>'
    '<text:notes-configuration text:note-class="footnote" style:num-format="1" text:citation-style-name="Footnote_20_anchor" text:start-value="0"/>'
    '<text:notes-configuration text:note-class="endnote" style:num-format="i" text:citation-style-name="Footnote_20_anchor" text:start-value="0"/>'
)

LAYOUT = ('<style:page-layout style:name="pm1"><style:page-layout-properties fo:page-width="21cm" fo:page-height="29.7cm" '
          'style:num-format="1" style:print-orientation="portrait" fo:margin-top="2cm" fo:margin-bottom="2cm" fo:margin-left="2cm" fo:margin-right="2cm"/>'
          '<style:header-style/><style:footer-style/></style:page-layout>')
STANDARD = '<style:master-page style:name="Standard" style:page-layout-name="pm1"/>'


def p(text, style="Standard"):
    return f'<text:p text:style-name="{style}">{text}</text:p>'


def h(text, level=1, style=None):
    return f'<text:h text:style-name="{style or f"Heading_20_{level}"}" text:outline-level="{level}">{text}</text:h>'


def span(text, style):
    return f'<text:span text:style-name="{style}">{text}</text:span>'


def text_style(name, properties):
    return f'<style:style style:name="{name}" style:family="text"><style:text-properties {properties}/></style:style>'


def paragraph_style(name, properties="", text="", parent="Standard", extra="", children=""):
    text_part = f"<style:text-properties {text}/>" if text else ""
    return (f'<style:style style:name="{name}" style:family="paragraph" style:parent-style-name="{parent}" {extra}>'
            f'<style:paragraph-properties {properties}>{children}</style:paragraph-properties>{text_part}</style:style>')


LONG = "words that wrap across several lines " * 6


def minimal():
    write("minimal.odt", [("content.xml", content(p("Hello, OpenDocument.")))])


def template():
    write("template.ott", [("content.xml", content(p("Template text."))), ("styles.xml", styles(COMMON, LAYOUT, STANDARD, FONTS))],
          mimetype=TEXT + "-template")


def document_styles():
    automatic = "".join([
        text_style("T1", 'fo:font-weight="bold"'),
        text_style("T2", 'fo:font-style="italic"'),
        text_style("T3", 'style:text-underline-style="solid" style:text-underline-width="auto" style:text-underline-color="font-color"'),
        text_style("T4", 'style:text-underline-style="solid" style:text-underline-type="double"'),
        text_style("T5", 'style:text-underline-style="dotted"'),
        text_style("T6", 'style:text-underline-style="wave"'),
        text_style("T7", 'style:text-line-through-style="solid"'),
        text_style("T8", 'fo:color="#ff0000"'),
        text_style("T9", 'fo:background-color="#00ff00"'),
        text_style("T10", 'style:text-position="super 58%"'),
        text_style("T11", 'style:text-position="sub 58%"'),
        text_style("T12", 'fo:font-variant="small-caps"'),
        text_style("T13", 'fo:text-transform="uppercase"'),
        text_style("T14", 'fo:text-transform="lowercase"'),
        text_style("T15", 'fo:text-transform="capitalize"'),
        text_style("T16", 'text:display="none"'),
        text_style("T17", 'fo:letter-spacing="2pt"'),
        text_style("T18", 'style:font-name="Mono Face"'),
        text_style("T19", 'fo:font-size="24pt"'),
        text_style("T20", 'fo:font-size="50%"'),
        text_style("T21", 'style:text-position="33% 100%"'),
        paragraph_style("P1", 'fo:text-align="center"', parent="Text_20_body"),
        paragraph_style("P2", 'fo:text-align="end"', parent="Text_20_body"),
        paragraph_style("P3", 'fo:text-align="justify"', parent="Text_20_body"),
        paragraph_style("P4", 'fo:margin-left="2cm" fo:text-indent="-1cm"', parent="Text_20_body"),
        paragraph_style("P5", 'fo:line-height="200%"', parent="Text_20_body"),
        paragraph_style("P6", 'fo:line-height="30pt"', parent="Text_20_body"),
        paragraph_style("P7", 'style:line-height-at-least="24pt"', parent="Text_20_body"),
        paragraph_style("P8", 'style:line-spacing="12pt"', parent="Text_20_body"),
        paragraph_style("P9", 'fo:border="0.5pt solid #ff0000" fo:padding="0.1cm" fo:background-color="#ffff00"', parent="Text_20_body"),
        paragraph_style("P10", 'fo:margin-left="1cm"', parent="Text_20_body", children=(
            '<style:tab-stops><style:tab-stop style:position="4cm"/>'
            '<style:tab-stop style:position="12cm" style:type="right" style:leader-style="dotted" style:leader-text="."/>'
            '<style:tab-stop style:position="14cm" style:type="char" style:char=","/></style:tab-stops>')),
        paragraph_style("P11", 'fo:margin-top="0.5cm" fo:margin-bottom="0.5cm" fo:margin-left="1.5cm" fo:margin-right="1.5cm"', parent="Text_20_body"),
    ])
    body = "".join([
        h("Document styles"),
        p("Plain text, " + span("bold", "T1") + ", " + span("italic", "T2") + ", " + span("underlined", "T3") + ", "
          + span("double underlined", "T4") + ", " + span("dotted", "T5") + ", " + span("wavy", "T6") + ", " + span("struck", "T7")
          + ", " + span("red", "T8") + ", " + span("highlighted", "T9") + ", E=mc" + span("2", "T10") + " and H" + span("2", "T11")
          + "O, " + span("small caps", "T12") + ", " + span("all caps", "T13") + ", " + span("LOWER CASE", "T14") + ", "
          + span("capitalized words", "T15") + ", " + span("spaced", "T17") + ", " + span("Liberation Mono", "T18") + ", "
          + span("large", "T19") + ", " + span("half size", "T20") + " and " + span("raised", "T21") + ".", "Text_20_body"),
        p("Strong " + span("common style", "Strong") + " and " + span("inherited big strong", "Big_20_Strong") + ".", "Text_20_body"),
        p("Visible text" + span(" Hidden text", "T16") + " continues.", "Text_20_body"),
        h("Paragraph formats", 2),
        p("Centered paragraph.", "P1"),
        p("Right-aligned paragraph.", "P2"),
        p("Justified paragraph: " + LONG, "P3"),
        p("Indented paragraph with a hanging indent: " + LONG, "P4"),
        p("Double spaced paragraph: " + LONG, "P5"),
        p("Fixed line height paragraph: " + LONG, "P6"),
        p("At least line height paragraph: " + LONG, "P7"),
        p("Leading paragraph: " + LONG, "P8"),
        p("A paragraph with a border and a background.", "P9"),
        p("Margins on all sides of this paragraph.", "P11"),
        p('Visit <text:a xlink:type="simple" xlink:href="https://example.com/" text:style-name="Internet_20_link">the example site</text:a>'
          ' or jump to <text:a xlink:type="simple" xlink:href="#target">the target</text:a>.', "Text_20_body"),
        h("Tabs and spaces", 2),
        p("Name:<text:tab/>Value<text:tab/>Right<text:tab/>3,25", "P10"),
        p("Several<text:s text:c=\"4\"/>spaces and   collapsed\n   white space<text:line-break/>Second line", "Text_20_body"),
        p("Quote style paragraph.", "Quote"),
        h('<text:bookmark-start text:name="target"/>Targets<text:bookmark-end text:name="target"/>'),
        p('<text:bookmark text:name="paragraph"/>The bookmarked target paragraph.', "Text_20_body"),
    ])
    write("styles.odt", [
        ("content.xml", content(body, automatic)),
        ("styles.xml", styles(COMMON, LAYOUT, STANDARD, FONTS)),
        ("meta.xml", meta("Styles fixture", "Fixture Author")),
    ])


def number_level(level, fmt, suffix=".", prefix="", margin=None, display=None, start=None):
    margin = margin if margin is not None else 1.27 * level
    attributes = f'text:level="{level}" style:num-format="{fmt}"'
    attributes += f' style:num-suffix="{suffix}"' if suffix else ""
    attributes += f' style:num-prefix="{prefix}"' if prefix else ""
    attributes += f' text:display-levels="{display}"' if display else ""
    attributes += f' text:start-value="{start}"' if start else ""
    return (f'<text:list-level-style-number {attributes}><style:list-level-properties text:list-level-position-and-space-mode="label-alignment">'
            f'<style:list-level-label-alignment text:label-followed-by="listtab" text:list-tab-stop-position="{margin:.3f}cm" '
            f'fo:text-indent="-0.635cm" fo:margin-left="{margin:.3f}cm"/></style:list-level-properties></text:list-level-style-number>')


def bullet_level(level, char, relative="45%"):
    return (f'<text:list-level-style-bullet text:level="{level}" text:style-name="Bullet_20_Symbols" text:bullet-char="{char}" text:bullet-relative-size="{relative}">'
            f'<style:list-level-properties text:list-level-position-and-space-mode="label-alignment">'
            f'<style:list-level-label-alignment text:label-followed-by="listtab" text:list-tab-stop-position="{1.27 * level:.3f}cm" '
            f'fo:text-indent="-0.635cm" fo:margin-left="{1.27 * level:.3f}cm"/></style:list-level-properties>'
            f'<style:text-properties style:font-name="OpenSymbol"/></text:list-level-style-bullet>')


def old_level(level, fmt, start=None):
    start_value = f' text:start-value="{start}"' if start else ""
    return (f'<text:list-level-style-number text:level="{level}" style:num-format="{fmt}" style:num-suffix="."{start_value}>'
            f'<style:list-level-properties text:space-before="{0.5 * (level - 1)}cm" text:min-label-width="1cm"/></text:list-level-style-number>')


def items(*texts):
    return "".join(f"<text:list-item>{t if t.startswith('<') else p(t)}</text:list-item>" for t in texts)


def lists():
    automatic = "".join([
        '<text:list-style style:name="L1">' + number_level(1, "1") + number_level(2, "a", ")") + number_level(3, "i", ")", "(") + '</text:list-style>',
        '<text:list-style style:name="L2">' + number_level(1, "1") + number_level(2, "1", display=2) + number_level(3, "1", display=3) + '</text:list-style>',
        '<text:list-style style:name="L3">' + bullet_level(1, "\u2022") + bullet_level(2, "\u25E6") + bullet_level(3, "\u25AA") + '</text:list-style>',
        '<text:list-style style:name="L4">' + old_level(1, "A", 3) + '</text:list-style>',
        '<text:list-style style:name="L5">' + number_level(1, "I", start=5) + '</text:list-style>',
        '<text:list-style style:name="L6">' + bullet_level(1, "\uE00A", "100%") + '</text:list-style>',
        paragraph_style("P1", parent="Text_20_body", extra='style:list-style-name="L1"'),
        paragraph_style("P2", 'fo:margin-left="3cm" fo:text-indent="0cm"', parent="Text_20_body"),
    ])
    common = COMMON + '<style:style style:name="Bullet_20_Symbols" style:family="text"><style:text-properties style:font-name="OpenSymbol"/></style:style>'
    nested_deeper = '<text:list>' + items("Roman item") + '</text:list>'
    nested = '<text:list>' + items("Nested item", p("Another nested item") + nested_deeper) + '</text:list>'
    body = "".join([
        h("Lists"),
        '<text:list text:style-name="L1">' + items("First item", p("Second item") + nested, "Back to the top level") + '</text:list>',
        p("A paragraph between lists.", "Text_20_body"),
        '<text:list text:style-name="L1" text:continue-numbering="true">' + items("Continued item") + '</text:list>',
        '<text:list text:style-name="L2">' + items(p("Level one") + '<text:list>' + items(p("Level two") + '<text:list>' + items("Level three") + '</text:list>') + '</text:list>') + '</text:list>',
        '<text:list text:style-name="L3">' + items(p("Bullet one") + '<text:list>' + items(p("Circle bullet") + '<text:list>' + items("Square bullet") + '</text:list>') + '</text:list>') + '</text:list>',
        '<text:list text:style-name="L4">' + items("Letter C item", "Letter D item") + '</text:list>',
        '<text:list text:style-name="L5" xml:id="roman">' + items("Roman five", "Roman six") + '</text:list>',
        p("Another paragraph.", "Text_20_body"),
        '<text:list text:style-name="L5" text:continue-list="roman">' + items("Roman seven") + '</text:list>',
        '<text:list text:style-name="L1"><text:list-item text:start-value="10">' + p("Restarted at ten") + '</text:list-item>' + items("Eleven") + '</text:list>',
        '<text:list text:style-name="L1"><text:list-header>' + p("A list header without a label") + '</text:list-header>' + items("Item after the header") + '</text:list>',
        '<text:list>' + items(p("Paragraph style list", "P1")) + '</text:list>',
        '<text:list text:style-name="L1">' + items(p("Own indent item", "P2")) + '</text:list>',
        '<text:numbered-paragraph text:list-id="np1" text:level="2" text:style-name="L1"><text:number>a)</text:number>' + p("Numbered paragraph") + '</text:numbered-paragraph>',
        '<text:list text:style-name="L6">' + items("Private use bullet") + '</text:list>',
    ])
    write("lists.odt", [("content.xml", content(body, automatic)), ("styles.xml", styles(common, LAYOUT, STANDARD, FONTS))])


def cell(text, style=None, attributes=""):
    style_attribute = f' table:style-name="{style}"' if style else ""
    inner = text if text.startswith("<") else p(text)
    return f'<table:table-cell{style_attribute} office:value-type="string"{attributes}>{inner}</table:table-cell>'


def row(*cells, style=None, attributes=""):
    style_attribute = f' table:style-name="{style}"' if style else ""
    return f'<table:table-row{style_attribute}{attributes}>{"".join(cells)}</table:table-row>'


COVERED = "<table:covered-table-cell/>"


def tables():
    def cell_style(name, properties):
        return f'<style:style style:name="{name}" style:family="table-cell"><style:table-cell-properties {properties}/></style:style>'

    def column_style(name, properties):
        return f'<style:style style:name="{name}" style:family="table-column"><style:table-column-properties {properties}/></style:style>'

    automatic = "".join([
        '<style:style style:name="Table1" style:family="table"><style:table-properties style:width="16cm" table:align="left"/></style:style>',
        column_style("Table1.A", 'style:column-width="4cm"'),
        column_style("Table1.B", 'style:column-width="3cm"'),
        column_style("Table1.D", 'style:column-width="6cm"'),
        cell_style("Head", 'fo:background-color="#cccccc" fo:padding="0.1cm" fo:border="0.5pt solid #000000"'),
        cell_style("Cell", 'fo:padding="0.1cm" fo:border-left="0.5pt solid #000000" fo:border-right="0.5pt solid #000000" fo:border-top="none" fo:border-bottom="0.5pt solid #000000"'),
        cell_style("Middle", 'style:vertical-align="middle" fo:padding="0.3cm" fo:background-color="#ffeeaa" fo:border="1.5pt double #0000ff"'),
        cell_style("Bottom", 'style:vertical-align="bottom" fo:padding="0.1cm" fo:border="1pt dotted #00aa00"'),
        cell_style("Dashed", 'fo:padding-left="0.5cm" fo:padding-top="0.1cm" fo:border="1pt dashed #aa0000"'),
        '<style:style style:name="Tall" style:family="table-row"><style:table-row-properties style:min-row-height="2cm"/></style:style>',
        '<style:style style:name="Exact" style:family="table-row"><style:table-row-properties style:row-height="1.5cm" fo:keep-together="always"/></style:style>',
        '<style:style style:name="Bold" style:family="paragraph" style:parent-style-name="Standard"><style:text-properties fo:font-weight="bold"/></style:style>',
        '<style:style style:name="Table2" style:family="table"><style:table-properties style:width="17cm" table:align="margins"/></style:style>',
        column_style("Table2.A", 'style:rel-column-width="1000*"'),
        column_style("Table2.B", 'style:rel-column-width="3000*"'),
        '<style:style style:name="Table3" style:family="table"><style:table-properties style:rel-width="50%" table:align="center" fo:background-color="#e0e0ff"/></style:style>',
        column_style("Table3.A", 'style:rel-column-width="1*"'),
        column_style("Table3.B", 'style:rel-column-width="3*"'),
        '<style:style style:name="Table4" style:family="table"><style:table-properties style:width="8cm" fo:break-before="page" table:align="right"/></style:style>',
        '<style:style style:name="Shaded" style:family="table-row"><style:table-row-properties fo:background-color="#ddffdd"/></style:style>',
    ])
    nested = ('<table:table table:name="Nested"><table:table-column/>' + row(cell("Nested one", "Cell")) + row(cell("Nested two", "Cell")) + '</table:table>')
    table1 = ('<table:table table:name="Table1" table:style-name="Table1">'
              '<table:table-column table:style-name="Table1.A" table:default-cell-style-name="Cell"/>'
              '<table:table-column table:style-name="Table1.B" table:number-columns-repeated="2" table:default-cell-style-name="Cell"/>'
              '<table:table-column table:style-name="Table1.D" table:default-cell-style-name="Cell"/>'
              '<table:table-header-rows>' + row(cell(p("Header A", "Bold"), "Head"), cell(p("Header B", "Bold"), "Head"), cell(p("Header C", "Bold"), "Head"), cell(p("Header D", "Bold"), "Head")) + '</table:table-header-rows>'
              + row(cell("Spans two columns", attributes=' table:number-columns-spanned="2"'), COVERED, cell("C2"), cell("D2"))
              + row(cell("Spans two rows", attributes=' table:number-rows-spanned="2"'), cell("B3"), cell("C3"), cell("D3"))
              + row(COVERED, cell("B4"), cell("C4"), cell("D4"))
              + row(cell("Middle cell", "Middle"), cell("Bottom cell", "Bottom"), cell("Dashed cell", "Dashed"), cell("Plain cell"), style="Tall")
              + row(cell("Outer"), cell("B6"), cell("C6"), cell(nested))
              + row(cell("Repeated row"), cell("x"), cell("y"), cell("z"), attributes=' table:number-rows-repeated="3"')
              + row(cell("Repeated cells"), cell("r", attributes=' table:number-columns-repeated="3"'), style="Shaded")
              + row(cell("Exact height row"), cell("e"), cell("f"), cell("g"), style="Exact")
              + '</table:table>')
    long_rows = "".join(row(cell(f"Row {i}"), cell(f"Value {i} " + "filler " * 3)) for i in range(1, 61))
    table2 = ('<table:table table:name="Table2" table:style-name="Table2"><table:table-columns><table:table-column table:style-name="Table2.A"/>'
              '<table:table-column table:style-name="Table2.B"/></table:table-columns>'
              '<table:table-header-rows>' + row(cell(p("Long header", "Bold"), "Head"), cell(p("Second header", "Bold"), "Head")) + '</table:table-header-rows>'
              '<table:table-rows>' + long_rows + '</table:table-rows></table:table>')
    table3 = ('<table:table table:name="Table3" table:style-name="Table3"><table:table-column table:style-name="Table3.A"/><table:table-column table:style-name="Table3.B"/>'
              + row(cell("Narrow"), cell("Wide relative column")) + '</table:table>')
    table4 = ('<table:table table:name="Table4" table:style-name="Table4"><table:table-column/>' + row(cell("Table on a new page")) + '</table:table>')
    body = "".join([h("Tables"), table1, p("Between tables.", "Text_20_body"), table2, p("After the long table.", "Text_20_body"), table3, table4, p("The end.")])
    write("tables.odt", [("content.xml", content(body, automatic)), ("styles.xml", styles(COMMON, LAYOUT, STANDARD, FONTS))])


def pages():
    def layout(name, width, height, margin, number_format="1", orientation="portrait", header=True):
        hf = ('<style:header-style><style:header-footer-properties fo:min-height="1cm" fo:margin-bottom="0.5cm"/></style:header-style>'
              '<style:footer-style><style:header-footer-properties fo:min-height="1cm" fo:margin-top="0.5cm"/></style:footer-style>') if header else "<style:header-style/><style:footer-style/>"
        return (f'<style:page-layout style:name="{name}"><style:page-layout-properties fo:page-width="{width}" fo:page-height="{height}" '
                f'style:num-format="{number_format}" style:print-orientation="{orientation}" fo:margin-top="{margin}" fo:margin-bottom="{margin}" '
                f'fo:margin-left="{margin}" fo:margin-right="{margin}"/>{hf}</style:page-layout>')

    page_layouts = (layout("pm1", "21cm", "29.7cm", "2cm") + layout("pm2", "29.7cm", "21cm", "1.5cm", "i", "landscape")
                    + layout("pm3", "8.5in", "11in", "1in") + layout("pm4", "21cm", "29.7cm", "2cm", header=False))
    page = '<text:page-number text:select-page="current">1</text:page-number>'
    masters = "".join([
        '<style:master-page style:name="Standard" style:page-layout-name="pm1">'
        f'<style:header>{p("Default header " + page, "Header")}</style:header>'
        f'<style:header-left>{p("Left header " + page, "Header")}</style:header-left>'
        f'<style:footer>{p("Page " + page + " of <text:page-count>1</text:page-count>", "Footer")}</style:footer></style:master-page>',
        '<style:master-page style:name="Landscape" style:page-layout-name="pm2">'
        f'<style:header>{p("Landscape header", "Header")}</style:header>'
        f'<style:footer>{p("Roman page " + page, "Footer")}</style:footer></style:master-page>',
        '<style:master-page style:name="Title" style:page-layout-name="pm3" style:next-style-name="Following">'
        f'<style:header>{p("Title page header", "Header")}</style:header></style:master-page>',
        '<style:master-page style:name="Following" style:page-layout-name="pm3">'
        f'<style:header>{p("Following header", "Header")}</style:header></style:master-page>',
        '<style:master-page style:name="Odf13" style:page-layout-name="pm1">'
        f'<style:header>{p("ODF 1.3 header", "Header")}</style:header>'
        f'<style:header-first>{p("ODF 1.3 first header", "Header")}</style:header-first>'
        f'<style:footer style:display="false">{p("Hidden footer", "Footer")}</style:footer></style:master-page>',
        '<style:master-page style:name="Plain" style:page-layout-name="pm4"/>',
    ])
    automatic = "".join([
        paragraph_style("P1", 'fo:break-before="page"'),
        paragraph_style("P2", 'style:page-number="1"', extra='style:master-page-name="Landscape"'),
        paragraph_style("P3", extra='style:master-page-name="Title"'),
        paragraph_style("P4", extra='style:master-page-name="Odf13"'),
        paragraph_style("P5", 'fo:break-after="page"'),
        paragraph_style("P6", 'style:page-number="7"', extra='style:master-page-name="Plain"'),
        '<style:style style:name="Table1" style:family="table" style:master-page-name="Standard"><style:table-properties style:width="10cm"/></style:style>',
    ])
    body = "".join([
        p("Page one text."),
        p("Page two text.", "P1"),
        p("Landscape page.", "P2"),
        p("Title page text.", "P3"),
        p("After the title page.", "P1"),
        p("ODF 1.3 page.", "P4"),
        p("Break after this paragraph.", "P5"),
        p("ODF 1.3 second page."),
        '<table:table table:name="Table1" table:style-name="Table1"><table:table-column/>' + row(cell("Table starts the standard pages")) + '</table:table>',
        p("Plain page seven.", "P6"),
    ])
    write("pages.odt", [("content.xml", content(body, automatic)), ("styles.xml", styles(COMMON, page_layouts, masters, FONTS))])


def fields():
    automatic = "".join([
        paragraph_style("P1", 'fo:break-before="page"'),
        '<style:style style:name="Sect1" style:family="section"><style:section-properties><style:columns fo:column-count="2" fo:column-gap="0.5cm"/></style:section-properties></style:style>',
    ])
    note = ('<text:note text:id="ftn1" text:note-class="footnote"><text:note-citation>1</text:note-citation><text:note-body>'
            + p("The first footnote text.", "Footnote") + '</text:note-body></text:note>')
    endnote = ('<text:note text:id="edn1" text:note-class="endnote"><text:note-citation>i</text:note-citation><text:note-body>'
               + p("The endnote text.", "Footnote") + '</text:note-body></text:note>')
    custom = ('<text:note text:id="ftn2" text:note-class="footnote"><text:note-citation text:label="*">*</text:note-citation><text:note-body>'
              + p("Custom mark note.", "Footnote") + '</text:note-body></text:note>')
    toc = ('<text:table-of-content text:name="Contents"><text:table-of-content-source text:outline-level="2">'
           '<text:index-title-template>Template title</text:index-title-template></text:table-of-content-source>'
           '<text:index-body><text:index-title text:name="Contents_Head">' + p("Contents") + '</text:index-title>'
           + p('<text:a xlink:type="simple" xlink:href="#later">Later section<text:tab/>2</text:a>') + '</text:index-body></text:table-of-content>')
    body = "".join([
        '<text:tracked-changes><text:changed-region text:id="ct1"><text:deletion><office:change-info><dc:creator>Editor</dc:creator>'
        '<dc:date>2026-01-01T00:00:00</dc:date></office:change-info>' + p("deleted words") + '</text:deletion></text:changed-region>'
        '<text:changed-region text:id="ct2"><text:insertion><office:change-info><dc:creator>Editor</dc:creator>'
        '<dc:date>2026-01-01T00:00:00</dc:date></office:change-info></text:insertion></text:changed-region></text:tracked-changes>',
        '<text:sequence-decls><text:sequence-decl text:display-outline-level="0" text:name="Table"/></text:sequence-decls>',
        h("Fields and notes"),
        toc,
        p('Title: <text:title>Fixture title</text:title>, author: <text:author-name>Ann Author</text:author-name>, date: '
          '<text:date style:data-style-name="N1" text:date-value="2026-01-01">2026-01-01</text:date>, time: <text:time text:time-value="12:00:00">12:00:00</text:time>, '
          'chapter: <text:chapter text:display="name" text:outline-level="1">Fields and notes</text:chapter>, sequence: Table '
          '<text:sequence text:ref-name="refTable0" text:name="Table" text:formula="ooow:Table+1" style:num-format="1">1</text:sequence>.', "Text_20_body"),
        p('See page <text:bookmark-ref text:reference-format="page" text:ref-name="later">1</text:bookmark-ref> for the later bookmark.', "Text_20_body"),
        p("Footnote here" + note + " and endnote" + endnote + " and a custom mark" + custom + ".", "Text_20_body"),
        p('Inserted <text:change-start text:change-id="ct2"/>new<text:change-end text:change-id="ct2"/> text<text:change text:change-id="ct1"/> stays.', "Text_20_body"),
        p('Commented<office:annotation><dc:creator>Reviewer</dc:creator><dc:date>2026-01-01T00:00:00</dc:date>' + p("A comment")
          + '</office:annotation> text.', "Text_20_body"),
        '<text:section text:name="Visible">' + p("Visible section text.") + '</text:section>',
        '<text:section text:name="Hidden" text:display="none">' + p("Hidden section text.") + '</text:section>',
        '<text:section text:name="Columns" text:style-name="Sect1">' + p("Two column section text.") + '</text:section>',
        p('Ruby <text:ruby><text:ruby-base>base</text:ruby-base><text:ruby-text>annotation</text:ruby-text></text:ruby> text.'),
        p('Hidden field<text:hidden-text text:condition="ooow:1" text:string-value="hidden by a field" text:is-hidden="true"/> and a '
          '<text:placeholder text:placeholder-type="text">placeholder</text:placeholder>.'),
        p('Soft<text:soft-page-break/> page break.'),
        p('<text:bookmark text:name="later"/>The later bookmark on page two.', "P1"),
        p('Page <text:page-number text:select-page="current">2</text:page-number> of <text:page-count>2</text:page-count> in the body.'),
    ])
    write("fields.odt", [
        ("content.xml", content(body, automatic)),
        ("styles.xml", styles(COMMON, LAYOUT, STANDARD, FONTS)),
        ("meta.xml", meta("Fields fixture")),
    ])


def frame(inner, width="2cm", height="1cm", anchor="as-char", style=None, extra=""):
    style_attribute = f' draw:style-name="{style}"' if style else ""
    return (f'<draw:frame{style_attribute} text:anchor-type="{anchor}" svg:width="{width}" svg:height="{height}" draw:z-index="0"{extra}>'
            f'{inner}</draw:frame>')


def image(href):
    return f'<draw:image xlink:href="{href}" xlink:type="simple" xlink:show="embed" xlink:actuate="onLoad"/>'


def images():
    automatic = "".join([
        '<style:style style:name="fr1" style:family="graphic"><style:graphic-properties style:wrap="dynamic" style:horizontal-pos="right" style:horizontal-rel="paragraph"/></style:style>',
        '<style:style style:name="fr2" style:family="graphic"><style:graphic-properties style:wrap="none" style:horizontal-pos="center" style:horizontal-rel="paragraph"/></style:style>',
        '<style:style style:name="fr3" style:family="graphic"><style:graphic-properties style:wrap="parallel" style:horizontal-pos="left"/></style:style>',
    ])
    body = "".join([
        h("Images"),
        p("Inline image: " + frame(image("Pictures/red.png"), extra=' draw:name="Red"') + " after the image.", "Text_20_body"),
        p("Floating right: " + frame(image("Pictures/blue.jpg"), "3cm", "3cm", "paragraph", "fr1") + LONG, "Text_20_body"),
        p(frame(image("Pictures/green.gif"), "4cm", "2cm", "paragraph", "fr2") + "Centered block image paragraph.", "Text_20_body"),
        p("Floating left: " + frame(image("Pictures/bitmap.bmp"), "2cm", "2cm", "char", "fr3") + LONG, "Text_20_body"),
        p("Alternatives: " + frame(image("Pictures/vector.svg") + image("Pictures/fallback.png"), "2cm", "1cm") + " picks the bitmap.", "Text_20_body"),
        p("Vector only: " + frame(image("Pictures/vector.svg"), "2cm", "1cm") + " is drawn.", "Text_20_body"),
        p("Chart: " + frame('<draw:object xlink:href="./Object 1" xlink:type="simple" xlink:show="embed" xlink:actuate="onLoad"/>'
                            + image("./ObjectReplacements/Object 1"), "3cm", "2cm") + " with its replacement.", "Text_20_body"),
        p("Text box anchor." + frame('<draw:text-box>' + p("Text box content.") + '</draw:text-box>', "5cm", "2cm", "paragraph"), "Text_20_body"),
        p("Linked image: " + frame(image("https://example.com/linked.png")) + " is left out.", "Text_20_body"),
        p("Missing image: " + frame(image("Pictures/missing.png")) + " is left out.", "Text_20_body"),
        p("Metafile: " + frame(image("Pictures/old.wmf")) + " cannot be drawn.", "Text_20_body"),
        p("Shape: " + '<draw:custom-shape text:anchor-type="paragraph" svg:width="4cm" svg:height="2cm">' + p("Shape text.")
          + '<draw:enhanced-geometry draw:type="rectangle"/></draw:custom-shape>'
          + '<draw:rect text:anchor-type="paragraph" svg:width="1cm" svg:height="1cm"/> end.', "Text_20_body"),
        p("Described: " + frame(image("Pictures/red.png") + "<svg:title>Red title</svg:title><svg:desc>A red rectangle</svg:desc>") + ".", "Text_20_body"),
        p('Linked picture: <draw:a xlink:type="simple" xlink:href="https://example.com/">' + frame(image("Pictures/red.png")) + '</draw:a>.', "Text_20_body"),
        frame(image("Pictures/red.png"), "3cm", "1.5cm", "page", "fr2"),
        p("After the page anchored image.", "Text_20_body"),
    ])
    write("images.odt", [
        ("content.xml", content(body, automatic)),
        ("styles.xml", styles(COMMON, LAYOUT, STANDARD, FONTS)),
        ("Pictures/red.png", png(64, 32, (220, 30, 30))),
        ("Pictures/blue.jpg", pillow(48, 48, (40, 60, 200), "JPEG")),
        ("Pictures/green.gif", pillow(40, 20, (30, 180, 60), "GIF")),
        ("Pictures/bitmap.bmp", pillow(16, 16, (200, 0, 200), "BMP")),
        ("Pictures/vector.svg", SVG),
        ("Pictures/fallback.png", png(40, 20, (0, 160, 255))),
        ("Pictures/old.wmf", WMF),
        ("Object 1/content.xml", content("")),
        ("ObjectReplacements/Object 1", png(60, 40, (250, 200, 0))),
    ])


def flat():
    picture = base64.encodebytes(png(32, 16, (10, 120, 10))).decode("ascii")
    automatic = ('<style:style style:name="T1" style:family="text"><style:text-properties fo:font-weight="bold"/></style:style>'
                 '<text:list-style style:name="L1">' + number_level(1, "1") + '</text:list-style>' + LAYOUT)
    masters = f'<style:master-page style:name="Standard" style:page-layout-name="pm1"><style:header>{p("Flat header", "Header")}</style:header></style:master-page>'
    body = "".join([
        h("Flat heading"),
        p("A flat document with " + span("bold", "T1") + " text.", "Text_20_body"),
        '<text:list text:style-name="L1">' + items("Flat item one", "Flat item two") + '</text:list>',
        '<table:table table:name="T"><table:table-column table:number-columns-repeated="2"/>' + row(cell("Flat cell A"), cell("Flat cell B")) + '</table:table>',
        p("Embedded image: " + frame(f'<draw:image><office:binary-data>{picture}</office:binary-data></draw:image>') + ".", "Text_20_body"),
    ])
    document = (f'<?xml version="1.0" encoding="UTF-8"?>\n<office:document {NAMESPACES} office:version="1.3" office:mimetype="{TEXT}">'
                f'<office:meta><dc:title>Flat document</dc:title><dc:creator>Flat Author</dc:creator></office:meta>'
                f'<office:font-face-decls>{FONTS}</office:font-face-decls><office:styles>{COMMON}</office:styles>'
                f'<office:automatic-styles>{automatic}</office:automatic-styles><office:master-styles>{masters}</office:master-styles>'
                f'<office:body><office:text>{body}</office:text></office:body></office:document>\n')
    with open(os.path.join(HERE, "flat.fodt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(document)
    return document


def flatten(content_xml, styles_xml):
    """A flat document of a package's content and styles (the automatic styles of both together), as a fuzzing seed:
    XML that ZIP checksums do not guard, so that mutations reach the reader."""
    def part(xml, element):
        start, end = xml.find(f"<office:{element}>"), xml.find(f"</office:{element}>")
        return xml[start + len(element) + 9:end] if start >= 0 and end >= 0 else ""

    automatic = part(styles_xml, "automatic-styles") + part(content_xml, "automatic-styles")
    return (f'<?xml version="1.0" encoding="UTF-8"?>\n<office:document {NAMESPACES} office:version="1.3" office:mimetype="{TEXT}">'
            f'<office:font-face-decls>{part(styles_xml, "font-face-decls")}</office:font-face-decls>'
            f'<office:styles>{part(styles_xml, "styles")}</office:styles><office:automatic-styles>{automatic}</office:automatic-styles>'
            f'<office:master-styles>{part(styles_xml, "master-styles")}</office:master-styles>'
            f'<office:body>{part(content_xml, "body")}</office:body></office:document>\n')


def seeds(flat_document):
    """Small stored copies, so that the fuzzer's mutations reach the XML."""
    os.makedirs(SEEDS, exist_ok=True)
    for name in os.listdir(HERE):
        if name.endswith((".odt", ".ott")) and name != "images.odt":
            with zipfile.ZipFile(os.path.join(HERE, name)) as source:
                parts = [(i.filename, source.read(i.filename)) for i in source.infolist() if i.filename not in ("mimetype", "META-INF/manifest.xml")]
                mimetype = source.read("mimetype").decode("ascii")
            write(name, parts, mimetype=mimetype, stored=True, folder=SEEDS)
    with open(os.path.join(SEEDS, "flat.fodt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(flat_document)
    for name in ("styles.odt", "lists.odt", "tables.odt", "pages.odt", "fields.odt"):
        with zipfile.ZipFile(os.path.join(HERE, name)) as source:
            flat_seed = flatten(source.read("content.xml").decode("utf-8"), source.read("styles.xml").decode("utf-8"))
        with open(os.path.join(SEEDS, name.replace(".odt", ".fodt")), "w", encoding="utf-8", newline="\n") as f:
            f.write(flat_seed)
    small = [("content.xml", content(p("Image " + frame(image("Pictures/red.png"))))), ("Pictures/red.png", png(4, 2, (255, 0, 0)))]
    write("image.odt", small, stored=True, folder=SEEDS)


minimal()
template()
document_styles()
lists()
tables()
pages()
fields()
images()
seeds(flat())

"""Generates the hand-made RTF fixtures of the RTF tests, one per feature area, and copies of the small ones as fuzzing
seeds (../../Fuzz/Seeds/rtf). RTF is plain text, so the documents are written here directly; the pictures are made with
Pillow. Run with a venv that has Pillow: python -m venv venv && venv/bin/pip install pillow; from this folder:
venv/bin/python make-rtf.py. The files are deterministic. Then run make-expected.py for expected.json."""
import io
import os

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SEEDS = os.path.join(HERE, "..", "..", "Fuzz", "Seeds", "rtf")

FONTS = (r"{\fonttbl{\f0\fswiss\fcharset0 Arial;}{\f1\froman\fcharset0 Times New Roman;}{\f2\fmodern\fcharset0 Courier New;}"
         r"{\f3\ftech\fcharset2 Symbol;}{\f4\fswiss\fcharset204 Arial Cyr;}{\f5\fnil\fcharset128 MS Gothic;}"
         r"{\f6\fswiss\fcharset238 Arial CE;}{\f7\fnil\fcharset2 Wingdings;}}")
COLORS = r"{\colortbl;\red0\green0\blue0;\red255\green0\blue0;\red0\green128\blue0;\red0\green0\blue255;\red255\green255\blue0;\red220\green220\blue220;}"


def document(body, styles="", extra="", header=r"\ansi\ansicpg1252\deff0\deftab720"):
    """An RTF document: the header with the font and color tables, optional style sheet and lists, then the body."""
    return "{\\rtf1" + header + FONTS + COLORS + styles + extra + "\n" + body + "\n}"


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


def hexlines(data):
    text = data.hex()
    return "\n".join(text[i:i + 128] for i in range(0, len(text), 128))


def minimal():
    return r"{\rtf1\ansi\deff0{\fonttbl{\f0 Times New Roman;}}\f0\fs24 Hello, minimal RTF document.\par}"


def formatting():
    styles = (r"{\stylesheet{\s0\ql\sa120\fs22 Normal;}"
              r"{\s1\sbasedon0\snext0\keepn\sb240\sa60\b\fs32\cf4 heading 1;}"
              r"{\s2\sbasedon1\snext0\i\fs26 heading 2;}"
              r"{\s3\sbasedon0\li720\ri720\i Quote;}"
              r"{\*\cs10\additive\b\cf2 Strong Red;}}")
    body = "\n".join([
        r"{\*\defchp\fs22}{\*\defpap\sa120}",
        r"{\info{\title Formatting fixture}{\author Test}}",
        r"\pard\plain\s1\sb240\sa60\keepn\b\fs32\cf4 Formatting\par",
        r"\pard\plain\s0\sa120\fs22 Plain text, {\b bold}, {\i italic}, {\ul underlined}, {\uldb double}, {\uld dotted}, {\uldash dashed}, {\ulwave wavy}, "
        r"{\strike struck}, {\cf2 red}, {\highlight5 highlighted}, {\chcbpat6 shaded}, E=mc{\super 2} and H{\sub 2}O, {\fs40 large}, {\f2 Courier New}, "
        r"{\scaps small caps}, {\caps all caps} and {\cs10 a character style}.\par",
        r"\pard\plain\s0\sa120\fs22 Hidden text follows:{\v  invisible words} done.\par",
        r"\pard\plain\s2\sb240\sa60\keepn\b\i\fs26\cf4 Paragraphs\par",
        r"\pard\plain\s0\qc\sa120\fs22 Centered paragraph.\par",
        r"\pard\plain\s0\qr\sa120\fs22 Right-aligned paragraph.\par",
        r"\pard\plain\s0\qj\sa120\fs22 Justified paragraph: " + "words that wrap across several lines " * 6 + r"\par",
        r"\pard\plain\s0\li1440\fi-720\sa120\fs22 Indented paragraph with a hanging indent: " + "more text " * 14 + r"\par",
        r"\pard\plain\s0\sl480\slmult1\sa120\fs22 Double spaced paragraph: " + "line spacing " * 12 + r"\par",
        r"\pard\plain\s0\sl-360\slmult0\sa120\fs22 Exactly 18 points: " + "exact lines " * 12 + r"\par",
        r"\pard\plain\s0\sa120\brdrt\brdrs\brdrw15\brdrcf2\brdrb\brdrdb\brdrw15\brdrl\brdrs\brdrw15\brdrr\brdrs\brdrw15\cbpat6\fs22 A paragraph with a border and shading.\par",
        r"\pard\plain\s3\li720\ri720\sa120\i\fs22 Quote style paragraph.\par",
        r"\pard\plain\s0\sa120\tx2880\tqr\tldot\tx8640\fs22 Name:\tab Value\tab Right\par",
        r"\pard\plain\s0\sa120\tqc\tx4320\fs22 Mid\tab Centered\par",
        r"\pard\plain\s0\sa120\tqdec\tx4320\fs22 Price\tab 12.50\par",
        r"\pard\plain\s0\sa120\fs22 Specials: \emdash\endash\lquote q\rquote \ldblquote dq\rdblquote \bullet a\~b c\_d \{braces\} back\\slash.\par",
        r"\pard\plain\s0\sa120\fs22 Unicode: caf\u233\'e9 na\uc2\u239\'3f\'3fve \uc1\u8364? euro and {\f3\u-3913?} private.\par",
        r"\pard\plain\s0\sa120\fs22 Code pages: caf\'e9 {\f4 \'cf\'f0\'e8\'e2\'e5\'f2} {\f6 \'8ee\'9a} {\f5 \'82\'b1\'82\'f1\'82\'c9\'82\'bf\'82\'cd} {\f3 \'b7}.\par",
        r"\pard\plain\s0\sa120\fs22 Breaks: line\line next line.\par",
        r"\pard\plain\s0\keep\sa120\fs22 Kept together and \expndtw40 spaced\expndtw0  and {\up6 raised} and {\dn6 lowered}.\par",
        r"\pard\plain\s0\sa120\fs22\pagebb New page before this paragraph.\par",
        r"\pard\plain\s0\sa120\fs22 Revisions: kept {\deleted deleted words }and {\revised inserted} words.\par",
        r"\pard\plain\s0\sa120\fs22 Last paragraph without a mark.",
    ])
    return document(body, styles)


def lists():
    lists_table = (
        r"{\*\listtable"
        r"{\list\listtemplateid1{\listlevel\levelnfc0\levelstartat1\levelfollow0{\leveltext\'02\'00.;}{\levelnumbers\'01;}\fi-360\li720}"
        r"{\listlevel\levelnfc2\levelstartat1\levelfollow1{\leveltext\'03(\'01);}{\levelnumbers\'02;}\fi-360\li1440}"
        r"{\listlevel\levelnfc3\levelstartat1\levelfollow0{\leveltext\'02\'02.;}{\levelnumbers\'01;}\fi-360\li2160}"
        r"{\listlevel\levelnfc0\levellegal\levelstartat1\levelfollow0{\leveltext\'06\'00.\'01.\'03.;}{\levelnumbers\'01\'03\'05;}\fi-720\li2880}\listid100}"
        r"{\list\listtemplateid2\listsimple{\listlevel\levelnfc23\levelfollow0{\leveltext\'01\u-3913 ?;}\f3\fi-360\li720}\listid200}"
        r"{\list\listtemplateid3{\listlevel\levelnfc4\levelstartat1\levelfollow0{\leveltext\'02\'00);}{\levelnumbers\'01;}\fi-360\li720}\listid300}}"
        r"{\*\listoverridetable{\listoverride\listid100\listoverridecount0\ls1}{\listoverride\listid200\listoverridecount0\ls2}"
        r"{\listoverride\listid300\listoverridecount1{\lfolevel\listoverridestartat\levelstartat5}\ls3}{\listoverride\listid100\listoverridecount0\ls4}}")
    def item(text, ls, level=0, listtext=""):
        indent = 720 * (level + 1)
        label = r"{\listtext\pard\plain " + listtext + r"\tab}" if listtext else ""
        return label + rf"\pard\plain\fi-360\li{indent}\ls{ls}\ilvl{level}\sa60\fs22 {text}\par"
    body = "\n".join([
        r"\pard\plain\sa120\b\fs28 Lists\par",
        item("First item", 1, 0, "1."),
        item("Second item", 1, 0, "2."),
        item("Nested item", 1, 1, "(i)"),
        item("Another nested item", 1, 1, "(ii)"),
        item("Lettered item", 1, 2, "A."),
        item("Legal item", 1, 3, "2.2.1."),
        item("Back to the top level", 1, 0, "3."),
        item("Nested again restarts", 1, 1, "(i)"),
        r"\pard\plain\sa120\fs22 A paragraph between lists.\par",
        item("Bullet one", 2, 0, r"{\f3 \'b7}"),
        item("Bullet two", 2, 0, r"{\f3 \'b7}"),
        item("Letter from five", 3, 0, "e)"),
        item("Letter six", 3, 0, "f)"),
        item("Same list continues", 4, 0, "4."),
        r"\pard\plain\sa120\fs22 Old-style numbering:\par",
        r"{\pntext\f0 1.\tab}\pard\plain{\*\pn\pnlvlbody\pndec\pnstart1\pnindent360{\pntxta .}}\fi-360\li720\sa60\fs22 Old first\par",
        r"{\pntext\f0 2.\tab}\pard\plain{\*\pn\pnlvlbody\pndec\pnstart1\pnindent360{\pntxta .}}\fi-360\li720\sa60\fs22 Old second\par",
        r"\pard\plain{\*\pn\pnlvlbody\pnucrm\pnstart3\pnindent360{\pntxta )}}\fi-360\li720\sa60\fs22 Computed roman three\par",
        r"\pard\plain{\*\pn\pnlvlbody\pnucrm\pnstart3\pnindent360{\pntxta )}}\fi-360\li720\sa60\fs22 Computed roman four\par",
        r"\pard\plain{\*\pn\pnlvlblt\pnf3\pnindent360{\pntxtb \'b7}}\fi-360\li720\sa60\fs22 Old bullet\par",
        r"{\listtext\pard\plain 9)\tab}\pard\plain\fi-360\li720\ls77\sa60\fs22 Label from the list text\par",
    ])
    return document(body, extra=lists_table)


def tables(rows=70):
    def cell_borders():
        return r"\clbrdrt\brdrs\brdrw10\clbrdrl\brdrs\brdrw10\clbrdrb\brdrs\brdrw10\clbrdrr\brdrs\brdrw10"
    def row(cells, widths, header=False, extra="", props=None):
        definition = r"\trowd\trgaph108\trleft-108" + (r"\trhdr" if header else "") + extra
        x = -108
        for i, w in enumerate(widths):
            x += w
            definition += (props[i] if props else "") + cell_borders() + rf"\cellx{x}"
        content = "".join(rf"\pard\intbl\plain\fs22 {c}\cell " for c in cells)
        return definition + "\n" + content + r"\row" + "\n"
    body = [r"\pard\plain\sa120\b\fs28 Tables\par"]
    body.append(row(["Description", "Quantity", "Price"], [4000, 2000, 2000], header=True, props=[r"\clcbpat6", r"\clcbpat6", r"\clcbpat6"]))
    for i in range(1, rows):
        body.append(row([f"Item number {i}", str(i), f"{i * 1.5:.2f}"], [4000, 2000, 2000]))
    body.append(r"\pard\plain\sa120\fs22 Merged cells:\par")
    widths = [2000, 2000, 2000, 2000]
    body.append(row(["Merged across", "", "C", "D"], widths, props=[r"\clmgf", r"\clmrg", "", ""]))
    body.append(row(["Merged down", "B2", r"\qr Right", "D2"], widths, props=[r"\clvmgf", "", "", r"\clvertalb\clcbpat5"]))
    body.append(row(["", "Bottom", "C3", "D3"], widths, props=[r"\clvmrg", "", "", ""]))
    body.append(r"\pard\plain\sa120\fs22 A nested table:\par")
    nested = (r"\pard\intbl\itap1\plain\fs22 Outer A\par"
              r"\pard\intbl\itap2\plain\fs22 Inner 1\nestcell{\nonesttables\par}\pard\intbl\itap2\plain\fs22 Inner 2\nestcell{\nonesttables\par}"
              r"\pard\intbl\itap2{\*\nesttableprops\trowd\trgaph108" + cell_borders() + r"\cellx1500" + cell_borders() + r"\cellx3000\nestrow}{\nonesttables\par}"
              r"\pard\intbl\itap1\plain\fs22 After inner\cell \pard\intbl\itap1\plain\fs22 Outer B\cell "
              r"\pard\intbl\itap1{\trowd\trgaph108\trleft-108" + cell_borders() + r"\cellx4000" + cell_borders() + r"\cellx8000\row}" + "\n")
    body.append(nested)
    body.append(r"\pard\plain\sa120\fs22 After the tables.\par")
    return document("\n".join(body))


def sections():
    header = (r"{\headerf\pard\plain\qc\fs20 First page header\par}"
              r"{\headerl\pard\plain\fs20 Even page header\par}"
              r"{\headerr\pard\plain\qr\fs20 Odd page header\par}"
              r"{\footerl\pard\plain\fs20 Even page {\field{\*\fldinst PAGE}{\fldrslt 2}}\par}"
              r"{\footerr\pard\plain\qr\fs20 Page {\field{\*\fldinst PAGE}{\fldrslt 1}} of {\field{\*\fldinst NUMPAGES}{\fldrslt 6}}\par}")
    body = "\n".join([
        r"\paperw12240\paperh15840\margl1440\margr1440\margt1440\margb1440\facingp",
        r"\sectd\titlepg\pgnstarts1\pgnrestart" + header,
        r"\pard\plain\sa120\fs22 Section one, page one.\par",
        r"\pard\plain\sa120\fs22 Page two.\page Page three.\page Page four.\par",
        r"\sect\sectd\lndscpsxn\pgwsxn15840\pghsxn12240\marglsxn1440\margrsxn1440\margtsxn1080\margbsxn1080",
        r"{\header\pard\plain\fs20 Landscape header\par}{\footer\pard\plain\fs20 Landscape page \chpgn\par}",
        r"\pard\plain\sa120\fs22 A landscape section.\par",
        r"\sect\sectd\pgnstarts1\pgnrestart\pgnlcrm",
        r"{\header\pard\plain\fs20 Roman header\par}{\footer\pard\plain\fs20 Section page {\field{\*\fldinst PAGE}{\fldrslt i}} of {\field{\*\fldinst SECTIONPAGES}{\fldrslt 1}}\par}",
        r"\pard\plain\sa120\fs22 A portrait section numbered in roman numerals.\par",
        r"\sect\sectd\sbknone\pgnlcrm",
        r"\pard\plain\sa120\fs22 A continuous section on the same page.\par",
        r"\sect\sectd\sbknone\cols2",
        r"\pard\plain\sa120\fs22 Two columns, laid out as one.\par",
    ])
    return document(body)


def fields():
    image = png(80, 40, (40, 120, 220))
    photo = jpeg(60, 60, (200, 60, 60))
    body = "\n".join([
        r"\pard\plain\sa120\b\fs28 Contents\par",
        r'\pard\plain\tqr\tldot\tx9360\sa60\fs22 {\field{\*\fldinst HYPERLINK \\l "intro"}{\fldrslt Introduction}}\tab {\field{\*\fldinst PAGEREF intro \\h}{\fldrslt 9}}\par',
        r'\pard\plain\tqr\tldot\tx9360\sa60\fs22 {\field{\*\fldinst HYPERLINK \\l "details"}{\fldrslt Details}}\tab {\field{\*\fldinst PAGEREF details \\h}{\fldrslt 9}}\par',
        r"\pard\plain\sa120\keepn\b\fs26\outlinelevel0 {\*\bkmkstart intro}Introduction{\*\bkmkend intro}\par",
        r'\pard\plain\sa120\fs22 Visit {\field{\*\fldinst {HYPERLINK "https://example.com/docs"}}{\fldrslt {\cf4\ul the example site}}} or the {\field{\*\fldinst HYPERLINK \\l "details"}{\fldrslt details}}.\par',
        r"\pard\plain\sa120\fs22 A sentence with a footnote{\super\chftn}{\footnote\pard\plain\fs18{\super\chftn} The footnote text.} and an endnote{\super\chftn}{\footnote\ftnalt\pard\plain\fs18{\super\chftn} The endnote text.}.\par",
        r'\pard\plain\sa120\fs22 A date field shows its result: {\field{\*\fldinst DATE \\@ "yyyy-MM-dd"}{\fldrslt 2026-01-02}}.\par',
        r"\pard\plain\sa120\fs22 A PNG picture: {\pict\pngblip\picw80\pich40\picwgoal1600\pichgoal800" + "\n" + hexlines(image) + "}.\\par",
        r"\pard\plain\sa120\fs22 A scaled JPEG: {\pict\jpegblip\picw60\pich60\picwgoal1200\pichgoal1200\picscalex50\picscaley50" + "\n" + hexlines(photo) + "}.\\par",
        r"\pard\plain\sa120\fs22 Binary data: {\pict\pngblip\picwgoal800\pichgoal400\bin" + str(len(image)) + " ",
    ]) .encode("latin-1") + image + "\n".join([
        r"}.\par",
        r"\pard\plain\sa120\fs22 Shape picture: {\*\shppict{\pict\pngblip\picwgoal600\pichgoal300 " + hexlines(image) + r"}}{\nonshppict{\pict\wmetafile8\picwgoal600\pichgoal300 0100}}.\par",
        r"\pard\plain\sa120\fs22 A metafile is reported: {\pict\emfblip\picwgoal600\pichgoal300 01000000}.\par",
        r"\pard\plain\sa120\fs22 An object shows its result: {\object\objemb{\*\objclass Paint}{\*\objdata 0102}{\result {Object result text}}}.\par",
        r"\pard\plain\sa120\fs22 A comment{\*\atnid A}{\*\atnauthor A}\chatn{\*\annotation\pard\plain Comment text that is left out.} is left out.\par",
        r"\pard\plain\sa120\fs22 A drawing object {\*\do\dobxcolumn\dprect\dpx0\dpy0\dpxsize100\dpysize100}is left out.\par",
        r"\pard\plain\sa120\fs22 Shape text: {\shp{\*\shpinst{\sp{\sn shapeType}{\sv 202}}{\shptxt\pard\plain\fs22 Text in a text box\par}}{\shprslt Fallback text}}after.\par",
        r"\pard\plain\pagebb\sa120\keepn\b\fs26\outlinelevel0 {\*\bkmkstart details}Details{\*\bkmkend details}\par",
        r"\pard\plain\sa120\fs22 The details paragraph.\par",
    ]).encode("latin-1")
    return document("{BODY}").encode("latin-1").replace(b"{BODY}", body)


def unicode_text():
    body = "\n".join([
        r"\pard\plain\sa120\fs22 Russian: \'cf\'f0\'e8\'e2\'e5\'f2, \'ec\'e8\'f0!\par",
        r"\pard\plain\sa120\fs22 Unicode escapes: \uc1\u1055?\u1088?\u1080?\u1074?\u1077?\u1090?\par",
        r"\pard\plain\sa120\fs22 {\upr{Ansi fallback}{\*\ud{Unicode version}}}\par",
    ])
    return "{\\rtf1\\ansi\\ansicpg1251\\deff0{\\fonttbl{\\f0\\fswiss\\fcharset0 Arial;}}\n" + body + "\n}"


def write(name, content, seed=False):
    data = content if isinstance(content, bytes) else content.encode("latin-1")
    with open(os.path.join(HERE, name), "wb") as f:
        f.write(data)
    if seed:
        os.makedirs(SEEDS, exist_ok=True)
        with open(os.path.join(SEEDS, name), "wb") as f:
            f.write(data)


write("minimal.rtf", minimal(), seed=True)
write("formatting.rtf", formatting(), seed=True)
write("lists.rtf", lists(), seed=True)
write("tables.rtf", tables())
write("sections.rtf", sections(), seed=True)
write("fields.rtf", fields(), seed=True)
write("unicode.rtf", unicode_text(), seed=True)
os.makedirs(SEEDS, exist_ok=True)
with open(os.path.join(SEEDS, "tables-small.rtf"), "wb") as f:
    f.write(tables(rows=3).encode("latin-1"))

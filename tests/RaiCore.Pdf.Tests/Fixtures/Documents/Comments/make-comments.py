"""Generates the comment fixtures of the Documents tests: comments.docx (python-docx, with a reply added as Word 2013
writes it: w14:paraId on the comments' last paragraphs and a commentsExtended part), comments.odt (hand-written ODF XML)
and comments.rtf (hand-written RTF). Each has comments with authors, initials and dates, a comment of two paragraphs,
a reply, a comment whose text spans two paragraphs, overlapping comments, a comment without a range (a point), a
comment in a table and one about a long text. Also writes small uncompressed copies as fuzzing seeds
(../../Fuzz/Seeds/docx, odt and rtf). Run from this folder with a venv that has python-docx:
python -m venv venv && venv/bin/pip install python-docx && venv/bin/python make-comments.py. The files are
deterministic: fixed dates, no random data. make-expected.py then writes what independent readers find in them."""
import datetime
import io
import os
import zipfile

import docx
from docx.opc.packuri import PackURI
from docx.opc.part import Part
from docx.oxml.ns import qn

HERE = os.path.dirname(os.path.abspath(__file__))
ZIP_DATE = (2026, 1, 1, 0, 0, 0)
W14 = "http://schemas.microsoft.com/office/word/2010/wordml"
W15 = "http://schemas.microsoft.com/office/word/2012/wordml"
LONG = ("This sentence is the long text a comment is about, longer than the eighty characters "
        "that the list of comments quotes")


def deterministic(data):
    """A ZIP package rewritten with fixed dates, in the same order."""
    source = zipfile.ZipFile(io.BytesIO(data))
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as target:
        for info in source.infolist():
            item = zipfile.ZipInfo(info.filename, ZIP_DATE)
            item.compress_type = zipfile.ZIP_DEFLATED
            target.writestr(item, source.read(info.filename))
    return out.getvalue()


def make_docx():
    document = docx.Document()
    p1 = document.add_paragraph("The first paragraph has a comment on ")
    words = p1.add_run("these words")
    p1.add_run(" and a reply to it.")
    first = document.add_comment(words, text="First paragraph of the comment.", author="Ann Author", initials="AA")
    first.add_paragraph("Second paragraph of the comment.")
    reply = document.add_comment(words, text="I agree with Ann.", author="Bob Builder", initials="BB")

    p2 = document.add_paragraph("Before the span. ")
    span_start = p2.add_run("Spanning starts here")
    p3 = document.add_paragraph()
    span_end = p3.add_run("and ends here")
    p3.add_run(", after the span.")
    span = document.add_comment([span_start, span_end], text="This comment spans two paragraphs.", author="Carla")

    p4 = document.add_paragraph("Overlap: ")
    alpha = p4.add_run("alpha ")
    beta = p4.add_run("beta")
    gamma = p4.add_run(" gamma")
    p4.add_run(" end.")
    overlap1 = document.add_comment([alpha, beta], text="On alpha beta.", author="Dan", initials="D")
    overlap2 = document.add_comment([beta, gamma], text="On beta gamma.", author="Eve", initials="E")

    table = document.add_table(rows=1, cols=2)
    table.cell(0, 0).text = "Plain cell"
    cell = table.cell(0, 1).paragraphs[0]
    cell_text = cell.add_run("cell text")
    in_table = document.add_comment(cell_text, text="A comment in a table.", author="Fay", initials="FF")

    p5 = document.add_paragraph()
    long_run = p5.add_run(LONG)
    p5.add_run(".")
    long = document.add_comment(long_run, text="About a long text.", author="Gus", initials="GG")

    dates = ["2026-01-02T09:30:00Z", "2026-01-03T10:00:00Z", "2026-01-04T11:15:00Z", "2026-01-05T08:00:00Z",
             "2026-01-05T08:05:00Z", "2026-01-06T12:00:00Z", "2026-01-07T13:45:00Z"]
    comments = [first, reply, span, overlap1, overlap2, in_table, long]
    for comment, date in zip(comments, dates):
        comment._comment_elm.set(qn("w:date"), date)

    # The reply: each comment's last paragraph gets an id, and commentsExtended names the reply's parent.
    for index, comment in enumerate(comments):
        comment.paragraphs[-1]._p.set("{%s}paraId" % W14, "0000%04X" % (index + 1))
    extended = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
                f'<w15:commentsEx xmlns:w15="{W15}">')
    for index in range(len(comments)):
        parent = ' w15:paraIdParent="00000001"' if index == 1 else ""
        extended += f'<w15:commentEx w15:paraId="0000{index + 1:04X}"{parent} w15:done="0"/>'
    extended += "</w15:commentsEx>"
    part = Part(PackURI("/word/commentsExtended.xml"),
                "application/vnd.openxmlformats-officedocument.wordprocessingml.commentsExtended+xml",
                extended.encode("utf-8"), document.part.package)
    document.part.relate_to(part, "http://schemas.microsoft.com/office/2011/relationships/commentsExtended")

    fixed = datetime.datetime(2026, 1, 1)
    document.core_properties.created = document.core_properties.modified = document.core_properties.last_printed = fixed
    document.core_properties.author = document.core_properties.last_modified_by = "RaiCore.Pdf tests"
    document.core_properties.title = "Comments fixture"
    buffer = io.BytesIO()
    document.save(buffer)
    return deterministic(buffer.getvalue())


ODF_NAMESPACES = (
    'xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" '
    'xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" '
    'xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" '
    'xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" '
    'xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" '
    'xmlns:dc="http://purl.org/dc/elements/1.1/" '
    'xmlns:meta="urn:oasis:names:tc:opendocument:xmlns:meta:1.0" '
    'xmlns:loext="urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0"'
)


def annotation(creator, date, text, name=None, initials=None, parent=None):
    attributes = (f' office:name="{name}"' if name else "") + (f' loext:parent-name="{parent}"' if parent else "")
    paragraphs = "".join(f"<text:p>{p}</text:p>" for p in text.split("\n"))
    initials = f"<meta:creator-initials>{initials}</meta:creator-initials>" if initials else ""
    return (f"<office:annotation{attributes}><dc:creator>{creator}</dc:creator><dc:date>{date}</dc:date>"
            f"{initials}{paragraphs}</office:annotation>")


def make_odt():
    body = (
        "<text:p>The first paragraph has a comment on "
        + annotation("Olga Odf", "2026-02-01T09:00:00.123456789", "First paragraph of the comment.\nSecond paragraph of the comment.",
                     name="c1", initials="OO")
        + annotation("Pia", "2026-02-02T10:30:00", "A reply to Olga.", name="c2", parent="c1", initials="PP")
        + 'these words<office:annotation-end office:name="c1"/><office:annotation-end office:name="c2"/> and a reply to it.</text:p>'
        + "<text:p>A point comment"
        + annotation("Quinn", "2026-02-03T11:00:00", "A comment without a range.", initials="Q")
        + " stands here.</text:p>"
        + "<text:p>Before the span. "
        + annotation("Rob", "2026-02-04T12:00:00", "This comment spans two paragraphs.", name="c3")
        + 'Spanning starts here</text:p><text:p>and ends here<office:annotation-end office:name="c3"/>, after the span.</text:p>'
        + "<table:table table:name=\"T\"><table:table-column table:number-columns-repeated=\"2\"/><table:table-row>"
        + "<table:table-cell><text:p>Plain cell</text:p></table:table-cell><table:table-cell><text:p>"
        + annotation("Sue", "2026-02-05T13:00:00", "A comment in a table.", name="c4", initials="SS")
        + 'cell text<office:annotation-end office:name="c4"/></text:p></table:table-cell></table:table-row></table:table>'
    )
    content = ('<?xml version="1.0" encoding="UTF-8"?>\n'
               f'<office:document-content {ODF_NAMESPACES} office:version="1.3"><office:body><office:text>'
               f"{body}</office:text></office:body></office:document-content>")
    manifest = ('<?xml version="1.0" encoding="UTF-8"?>\n'
                '<manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.3">'
                '<manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.text"/>'
                '<manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>'
                "</manifest:manifest>")
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as package:
        package.writestr(zipfile.ZipInfo("mimetype", ZIP_DATE), "application/vnd.oasis.opendocument.text", zipfile.ZIP_STORED)
        for name, data in (("content.xml", content), ("META-INF/manifest.xml", manifest)):
            item = zipfile.ZipInfo(name, ZIP_DATE)
            item.compress_type = zipfile.ZIP_DEFLATED
            package.writestr(item, data)
    return out.getvalue()


def dttm(year, month, day, hour, minute):
    """A DTTM ([MS-DOC] 2.9.66) as RTF writes it: a signed 32-bit number."""
    value = minute | (hour << 6) | (day << 11) | (month << 16) | ((year - 1900) << 20)
    return value - (1 << 32) if value >= 1 << 31 else value


def rtf_comment(tag, initials, author, date, text, parent=None):
    """A comment's details, its reference mark and its text (as Word writes them)."""
    reference = f"{{\\*\\atnref {tag}}}" if tag else ""
    parent = f"{{\\*\\atnparent {parent}}}" if parent else ""
    paragraphs = "\\par ".join(text.split("\n"))
    return (f"{{\\*\\atnid {initials}}}{{\\*\\atnauthor {author}}}\\chatn "
            f"{{\\*\\annotation{reference}{parent}{{\\*\\atndate {date}}}\\pard\\plain\\fs20 {{\\chatn }}{paragraphs}}}")


def make_rtf():
    parts = [
        "{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\fswiss Arial;}}\\fs22\n",
        "\\pard The first paragraph has a comment on {\\*\\atrfstart 101}these words{\\*\\atrfend 101}",
        rtf_comment("101", "RR", "Rita Rtf", dttm(2026, 3, 1, 9, 15), "First paragraph of the comment.\nSecond paragraph of the comment."),
        rtf_comment("102", "SS", "Sam", dttm(2026, 3, 2, 10, 45), "A reply to Rita.", parent="101"),
        " and a reply to it.\\par\n",
        "\\pard A point comment",
        rtf_comment(None, "TT", "Tom", dttm(2026, 3, 3, 11, 0), "A comment without a range."),
        " stands here.\\par\n",
        "\\pard Before the span. {\\*\\atrfstart 103}Spanning starts here\\par\n",
        "\\pard and ends here{\\*\\atrfend 103}",
        rtf_comment("103", "UU", "Uma", dttm(2026, 3, 4, 12, 30), "This comment spans two paragraphs."),
        ", after the span.\\par\n",
        "\\pard Overlap: {\\*\\atrfstart 104}alpha {\\*\\atrfstart 105}beta{\\*\\atrfend 104}",
        rtf_comment("104", "V", "Vic", dttm(2026, 3, 5, 8, 0), "On alpha beta."),
        " gamma{\\*\\atrfend 105}",
        rtf_comment("105", "W", "Wes", dttm(2026, 3, 5, 8, 5), "On beta gamma."),
        " end.\\par\n}",
    ]
    return "".join(parts).encode("ascii")


def stored(entries):
    """A ZIP package of (name, bytes) entries, uncompressed, so that fuzzing mutations reach the XML."""
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w", zipfile.ZIP_STORED) as package:
        for name, data in entries:
            package.writestr(zipfile.ZipInfo(name, ZIP_DATE), data)
    return out.getvalue()


def docx_seed(data):
    """The comment parts of comments.docx in a small package of their own (no styles, theme or thumbnail)."""
    source = zipfile.ZipFile(io.BytesIO(data))
    types = ('<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
             '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
             '<Default Extension="xml" ContentType="application/xml"/>'
             '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
             "</Types>")
    rels = ('<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>'
            "</Relationships>")
    document_rels = ('<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                     '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments" Target="comments.xml"/>'
                     '<Relationship Id="rId2" Type="http://schemas.microsoft.com/office/2011/relationships/commentsExtended" Target="commentsExtended.xml"/>'
                     "</Relationships>")
    return stored([("[Content_Types].xml", types), ("_rels/.rels", rels), ("word/document.xml", source.read("word/document.xml")),
                   ("word/_rels/document.xml.rels", document_rels), ("word/comments.xml", source.read("word/comments.xml")),
                   ("word/commentsExtended.xml", source.read("word/commentsExtended.xml"))])


def main():
    seeds = os.path.join(HERE, "..", "..", "Fuzz", "Seeds")
    docx_data, odt_data, rtf_data = make_docx(), make_odt(), make_rtf()
    odt_seed = stored([(item.filename, zipfile.ZipFile(io.BytesIO(odt_data)).read(item.filename))
                       for item in zipfile.ZipFile(io.BytesIO(odt_data)).infolist()])
    for path, data in (("comments.docx", docx_data), ("comments.odt", odt_data), ("comments.rtf", rtf_data),
                       (os.path.join(seeds, "docx", "comments.docx"), docx_seed(docx_data)),
                       (os.path.join(seeds, "odt", "comments.odt"), odt_seed),
                       (os.path.join(seeds, "rtf", "comments.rtf"), rtf_data)):
        with open(os.path.join(HERE, path), "wb") as file:
            file.write(data)


if __name__ == "__main__":
    main()

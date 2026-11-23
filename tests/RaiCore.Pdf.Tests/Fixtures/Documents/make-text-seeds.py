"""Writes the fuzzing seeds of the text extraction target (../Fuzz/Seeds/doctext): small documents in each format with
what only text extraction reads (comments, hidden text, deleted text of tracked changes, field codes) next to headers,
notes, lists, tables and text boxes. Packages are stored uncompressed, so that mutations reach the XML. Run from this
folder with any Python 3: python make-text-seeds.py"""
import os
import zipfile

OUT = os.path.join("..", "Fuzz", "Seeds", "doctext")
W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
PR = "http://schemas.openxmlformats.org/package/2006/relationships"


def stored_zip(path, parts):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as z:
        for name, content in parts:
            z.writestr(zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0)), content)


def part(root, content):
    return f'<w:{root} xmlns:w="{W}" xmlns:r="{R}">{content}</w:{root}>'


def docx():
    def p(text, ppr=""):
        return f'<w:p>{ppr}<w:r><w:t xml:space="preserve">{text}</w:t></w:r></w:p>'

    body = (
        p("Title", '<w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr>')
        + '<w:p><w:r><w:t xml:space="preserve">Shown </w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:t>hidden</w:t></w:r>'
        + '<w:del w:id="1" w:author="A"><w:r><w:delText>deleted</w:delText></w:r></w:del><w:ins w:id="2" w:author="A"><w:r><w:t>inserted</w:t></w:r></w:ins>'
        + '<w:r><w:footnoteReference w:id="1"/></w:r><w:r><w:commentReference w:id="0"/></w:r></w:p>'
        + '<w:p><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r>'
        + '<w:r><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r><w:fldSimple w:instr=" AUTHOR "><w:r><w:t>Ann</w:t></w:r></w:fldSimple>'
        + '<w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText> HYPERLINK "https://example.com" </w:instrText></w:r>'
        + '<w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>link</w:t></w:r></w:p>'
        + '<w:p><w:r><w:t>still the link</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r>'
        + '<w:r><w:pict><v:shape xmlns:v="urn:schemas-microsoft-com:vml"><v:textbox><w:txbxContent>' + p("Boxed") + '</w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>'
        + '<w:tbl><w:tblGrid><w:gridCol w:w="2000"/><w:gridCol w:w="2000"/></w:tblGrid><w:tr><w:tc>' + p("A") + p("A2") + '</w:tc><w:tc>' + p("B")
        + '</w:tc></w:tr></w:tbl>'
        + '<w:sectPr><w:headerReference w:type="default" r:id="rH"/><w:footerReference w:type="first" r:id="rF"/><w:titlePg/></w:sectPr>'
    )
    numbering = part("numbering", '<w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/>'
                     '<w:lvlText w:val="%1."/></w:lvl></w:abstractNum><w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>')
    rels = (f'<Relationships xmlns="{PR}">'
            + "".join(f'<Relationship Id="{i}" Type="{R}/{t}" Target="{target}"/>' for i, t, target in [
                ("rH", "header", "header1.xml"), ("rF", "footer", "footer1.xml"), ("rN", "footnotes", "footnotes.xml"),
                ("rC", "comments", "comments.xml"), ("rU", "numbering", "numbering.xml")])
            + "</Relationships>")
    stored_zip(os.path.join(OUT, "text-extras.docx"), [
        ("[Content_Types].xml", '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>'),
        ("_rels/.rels", f'<Relationships xmlns="{PR}"><Relationship Id="rId1" Type="{R}/officeDocument" Target="word/document.xml"/></Relationships>'),
        ("word/document.xml", f'<w:document xmlns:w="{W}" xmlns:r="{R}"><w:body>{body}</w:body></w:document>'),
        ("word/_rels/document.xml.rels", rels),
        ("word/header1.xml", part("hdr", p("Header"))),
        ("word/footer1.xml", part("ftr", p("First footer"))),
        ("word/footnotes.xml", part("footnotes", '<w:footnote w:id="1"><w:p><w:r><w:footnoteRef/></w:r><w:r><w:t> Note</w:t></w:r></w:p></w:footnote>')),
        ("word/comments.xml", part("comments", '<w:comment w:id="0"><w:p><w:r><w:t>Remark</w:t></w:r></w:p></w:comment>')),
        ("word/numbering.xml", numbering),
    ])


def rtf():
    text = (r"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}{\header \pard Header\par}{\footer \pard Footer\par}" "\n"
            r"{\*\listtable{\list\listid1{\listlevel\levelnfc0{\leveltext\'02\'00.;}{\levelnumbers\'01;}}}}"
            r"{\*\listoverridetable{\listoverride\listid1\ls1}}" "\n"
            r"\pard\ls1 Item\par" "\n"
            r"\pard Shown{\v  hidden}{\deleted  deleted}{\field{\*\fldinst PAGE}{\fldrslt 1}}"
            r"{\chftn{\footnote\pard{\chftn} Note\par}}{\*\atnid A}\chatn{\*\annotation{\*\atnref 1}\pard Remark\par}\par" "\n"
            r"\pard{\field{\*\fldinst HYPERLINK ""https://example.com""}{\fldrslt link}}"
            r"{\shp{\*\shpinst{\shptxt \pard Boxed\par}}}\par" "\n"
            r"\trowd\cellx2000\cellx4000\pard\intbl A\par A2\cell B\cell\row\pard End\par}")
    with open(os.path.join(OUT, "text-extras.rtf"), "w", encoding="ascii", newline="\n") as f:
        f.write(text)


ODF = ('xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" '
       'xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" '
       'xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:dc="http://purl.org/dc/elements/1.1/" '
       'xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0"')
ODT_AUTOMATIC = '<style:style style:name="H" style:family="text"><style:text-properties text:display="none"/></style:style>'
ODT_BODY = (
    '<text:tracked-changes><text:changed-region text:id="ct1"><text:deletion><office:change-info><dc:creator>A</dc:creator></office:change-info>'
    '<text:p>deleted</text:p></text:deletion></text:changed-region></text:tracked-changes>'
    '<text:list><text:list-item><text:p>Item</text:p></text:list-item></text:list>'
    '<text:p>Shown<text:span text:style-name="H"> hidden</text:span><text:change text:change-id="ct1"/>'
    '<text:note text:note-class="footnote"><text:note-citation>1</text:note-citation><text:note-body><text:p>Note</text:p></text:note-body></text:note>'
    '<office:annotation><dc:creator>Ann</dc:creator><text:p>Remark</text:p></office:annotation>'
    '<text:hidden-text text:string-value="cond" text:is-hidden="true"/> page <text:page-number>1</text:page-number></text:p>'
    '<text:p>Frame<draw:frame text:anchor-type="paragraph"><draw:text-box><text:p>Boxed</text:p></draw:text-box></draw:frame></text:p>'
    '<table:table><table:table-column table:number-columns-repeated="2"/><table:table-row><table:table-cell><text:p>A</text:p><text:p>A2</text:p>'
    '</table:table-cell><table:table-cell><text:p>B</text:p></table:table-cell></table:table-row></table:table>')


def odt():
    content = (f'<office:document-content {ODF}><office:automatic-styles>{ODT_AUTOMATIC}</office:automatic-styles>'
               f'<office:body><office:text>{ODT_BODY}</office:text></office:body></office:document-content>')
    stored_zip(os.path.join(OUT, "text-extras.odt"), [("mimetype", "application/vnd.oasis.opendocument.text"), ("content.xml", content)])
    flat = (f'<?xml version="1.0" encoding="UTF-8"?><office:document {ODF} office:mimetype="application/vnd.oasis.opendocument.text">'
            f'<office:automatic-styles>{ODT_AUTOMATIC}</office:automatic-styles><office:body><office:text>{ODT_BODY}</office:text></office:body></office:document>')
    with open(os.path.join(OUT, "text-extras.fodt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(flat)


os.makedirs(OUT, exist_ok=True)
docx()
rtf()
odt()

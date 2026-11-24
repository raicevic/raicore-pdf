"""Writes expected.json: the comments that independent readers find in the comment fixtures, for the tests of
DocumentConverterOptions.Comments. Run from this folder with a venv that has python-docx, odfpy, striprtf and olefile:
python -m venv venv && venv/bin/pip install python-docx odfpy striprtf olefile && venv/bin/python make-expected.py.

Each file maps to its comments in the order of their references: author, initials, date ("yyyy-MM-dd HH:mm", as the
file stores it), text (paragraphs on lines of their own), "about" (the text the comment is about, white space
collapsed, or null without a range) and "parent" (the index of the comment a reply answers, or null).

- DOCX: python-docx's comments (author, initials, date, text) and its XML (lxml) for the ranges, the order of the
  references and the replies (commentsExtended).
- ODT: odfpy's tree (office:annotation, dc:creator, dc:date, meta:creator-initials, office:annotation-end).
- RTF: striprtf for the text of the comments and of the ranges, after a split of the RTF into its annotation
  destinations here.
- DOC: doc_comments.py (olefile and a walk of the [MS-DOC] structures)."""
import io
import json
import os
import re
import sys
import zipfile

import docx
from lxml import etree
from odf import opendocument
from odf.element import Element, Text
from striprtf.striprtf import rtf_to_text

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # doc_comments.py, also when run with python -I.
import doc_comments  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURES = os.path.join(HERE, "..", "..")
W = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
W14 = "{http://schemas.microsoft.com/office/word/2010/wordml}"
W15 = "{http://schemas.microsoft.com/office/word/2012/wordml}"

FILES = [
    "Documents/Comments/comments.docx",
    "Documents/Comments/comments.odt",
    "Documents/Comments/comments.rtf",
    "Tika/testComment.docx",
    "Tika/testComment.doc",
    "Tika/testComment.rtf",
    "Tika/testRTF_annotation_spacing.rtf",
    "Tika/testODFwithOOo3.odt",
    "Poi/comment.docx",
    "Documents/Doc/footnote.doc",
    "Documents/Doc/testWORD_numbered_list.doc",
]


def collapse(text):
    return " ".join(text.split()) if text is not None else None


def date_of(value):
    """An XML date and time as "yyyy-MM-dd HH:mm" (the clock time as stored), or None."""
    match = re.match(r"(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})", value or "")
    return f"{match[1]}-{match[2]}-{match[3]} {match[4]}:{match[5]}" if match else None


def docx_comments(path):
    document = docx.Document(path)
    found = {}
    for comment in document.comments:
        found[str(comment.comment_id)] = {
            "author": comment.author or None,
            "initials": comment.initials or None,
            "date": comment.timestamp.strftime("%Y-%m-%d %H:%M") if comment.timestamp else None,
            "text": "\n".join(p.text for p in comment.paragraphs).strip("\n"),
            "about": None,
            "parent": None,
        }
    package = zipfile.ZipFile(path)
    body = etree.fromstring(package.read("word/document.xml"))
    order, open_ranges, about = [], {}, {}
    for element in body.iter():
        if element.tag == W + "commentRangeStart":
            open_ranges[element.get(W + "id")] = []
        elif element.tag == W + "commentRangeEnd":
            identifier = element.get(W + "id")
            if identifier in open_ranges:
                about[identifier] = " ".join(open_ranges.pop(identifier))
        elif element.tag == W + "commentReference":
            identifier = element.get(W + "id")
            if identifier in found and identifier not in order:
                order.append(identifier)
        elif element.tag == W + "t":
            for parts in open_ranges.values():
                parts.append(element.text or "")
        elif element.tag == W + "p":
            for parts in open_ranges.values():
                parts.append(" ")  # Paragraphs end with a space between them.
    for identifier, text in about.items():
        if identifier in found:
            found[identifier]["about"] = collapse("".join(text))
    # Replies: commentsExtended names the parent by the id of its last paragraph.
    if "word/commentsExtended.xml" in package.namelist():
        comments = etree.fromstring(package.read("word/comments.xml"))
        by_paragraph = {}
        for comment in comments.iter(W + "comment"):
            paragraphs = comment.findall(W + "p")
            if paragraphs and paragraphs[-1].get(W14 + "paraId"):
                by_paragraph[paragraphs[-1].get(W14 + "paraId")] = comment.get(W + "id")
        extended = etree.fromstring(package.read("word/commentsExtended.xml"))
        for item in extended.iter(W15 + "commentEx"):
            parent = item.get(W15 + "paraIdParent")
            if parent and item.get(W15 + "paraId") in by_paragraph and parent in by_paragraph:
                found[by_paragraph[item.get(W15 + "paraId")]]["parent"] = by_paragraph[parent]
    return [dict(found[i], parent=order.index(found[i]["parent"]) if found[i]["parent"] in order else None) for i in order]


def load_odt(path):
    """odfpy's document; embedded objects (which it refuses when they have a DTD, as formulas do) are left out."""
    try:
        return opendocument.load(path)
    except Exception:
        source = zipfile.ZipFile(path)
        parts = io.BytesIO()
        with zipfile.ZipFile(parts, "w") as target:
            target.writestr("mimetype", source.read("mimetype"))
            target.writestr("content.xml", source.read("content.xml"))
            target.writestr("META-INF/manifest.xml",
                            '<manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0">'
                            '<manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.text"/>'
                            '<manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>'
                            "</manifest:manifest>")
        parts.seek(0)
        return opendocument.load(parts)


def odt_comments(path):
    document = load_odt(path)
    office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0"
    dc = "http://purl.org/dc/elements/1.1/"
    text_ns = "urn:oasis:names:tc:opendocument:xmlns:text:1.0"
    results, names, open_ranges, order = [], {}, {}, []
    ends = set()

    def collect_ends(node):
        for child in node.childNodes:
            if isinstance(child, Element):
                if child.qname == (office, "annotation-end"):
                    ends.add(child.getAttrNS(office, "name"))
                collect_ends(child)

    def plain(node):
        if isinstance(node, Text):
            return node.data
        if not isinstance(node, Element):
            return ""
        if node.qname == (text_ns, "s"):
            return " " * int(node.getAttrNS(text_ns, "c") or 1)
        if node.qname == (text_ns, "tab"):
            return "\t"
        return "".join(plain(child) for child in node.childNodes)

    def walk(node):
        for child in node.childNodes:
            if isinstance(child, Text):
                for parts in open_ranges.values():
                    parts.append(child.data)
            elif isinstance(child, Element):
                if child.qname == (office, "annotation"):
                    creator = [c for c in child.childNodes if isinstance(c, Element) and c.qname == (dc, "creator")]
                    date = [c for c in child.childNodes if isinstance(c, Element) and c.qname == (dc, "date")]
                    initials = [c for c in child.childNodes if isinstance(c, Element) and c.qname[1] == "creator-initials"]
                    paragraphs = [plain(c) for c in child.childNodes if isinstance(c, Element) and c.qname[0] == text_ns]
                    parent = None
                    for (_, local), value in child.attributes.items():
                        if local == "parent-name":
                            parent = value
                    comment = {
                        "author": plain(creator[0]).strip() or None if creator else None,
                        "initials": plain(initials[0]).strip() or None if initials else None,
                        "date": date_of(plain(date[0])) if date else None,
                        "text": "\n".join(paragraphs).strip("\n"),
                        "about": None,
                        "parent": parent,
                    }
                    results.append(comment)
                    name = child.getAttrNS(office, "name")
                    if name:
                        names[name] = comment
                    if name and name in ends:
                        open_ranges[name] = []
                    else:
                        order.append(comment)
                elif child.qname == (office, "annotation-end"):
                    name = child.getAttrNS(office, "name")
                    if name in open_ranges:
                        names[name]["about"] = collapse("".join(open_ranges.pop(name)))
                        order.append(names[name])
                elif child.qname == (text_ns, "s"):
                    for parts in open_ranges.values():
                        parts.append(" " * int(child.getAttrNS(text_ns, "c") or 1))
                else:
                    walk(child)
                    if child.qname in ((text_ns, "p"), (text_ns, "h")):
                        for parts in open_ranges.values():
                            parts.append(" ")

    collect_ends(document.text)
    walk(document.text)
    for comment in order:
        parent = names.get(comment["parent"]) if comment["parent"] else None
        comment["parent"] = order.index(parent) if parent in order else None
    return order


def rtf_groups(rtf, keyword):
    """The (start, end) of each group whose destination is the keyword (after {\\*)."""
    spans = []
    for match in re.finditer(r"\{\\\*\\" + keyword + r"(?![a-z])", rtf):
        depth, i = 0, match.start()
        while i < len(rtf):
            c = rtf[i]
            if c == "\\":
                i += 2
                continue
            if c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        spans.append((match.start(), i + 1))
    return spans


def value_of(group, keyword):
    match = re.search(r"\{\\\*\\" + keyword + r" ?([^}]*)\}", group)
    return match[1].strip() if match else None


def rtf_comments(path):
    rtf = open(path, "rb").read().decode("latin-1")
    header = rtf[:rtf.find("\\pard")] if "\\pard" in rtf else ""
    comments = []
    for start, end in rtf_groups(rtf, "annotation"):
        group = rtf[start:end]
        before = rtf[:start]
        # The details just before the annotation: its initials and author.
        ids = rtf_groups(before, "atnid")
        authors = rtf_groups(before, "atnauthor")
        initials = value_of(before[ids[-1][0]:ids[-1][1]], "atnid") if ids else None
        author = value_of(before[authors[-1][0]:authors[-1][1]], "atnauthor") if authors else None
        date = value_of(group, "atndate")
        inner = re.sub(r"\{\\\*\\(atnref|atndate|atnparent)[^}]*\}", "", group[len("{\\*\\annotation"):-1])
        text = rtf_to_text("{\\rtf1 " + header + inner + "}").replace("\r", "")
        comments.append({
            "author": author or None,
            "initials": initials or None,
            "date": doc_comments.dttm(int(date) & 0xFFFFFFFF) if date else None,
            "text": "\n".join(line.strip() for line in text.split("\n")).strip("\n"),
            "about": None,
            "parent": value_of(group, "atnparent"),
            "tag": value_of(group, "atnref"),
        })
    # The ranges: the body with the comment destinations taken out and the range marks turned into text.
    body = rtf
    for start, end in sorted(rtf_groups(rtf, "annotation") + rtf_groups(rtf, "atnid") + rtf_groups(rtf, "atnauthor"), reverse=True):
        body = body[:start] + body[end:]
    body = re.sub(r"\{\\\*\\atrfstart ([^}]*)\}", lambda m: "\\'ab" + "S" + m[1].strip() + "\\'bb", body)
    body = re.sub(r"\{\\\*\\atrfend ([^}]*)\}", lambda m: "\\'ab" + "E" + m[1].strip() + "\\'bb", body)
    plain = rtf_to_text(body).replace("\r", "")
    for comment in comments:
        tag = comment.pop("tag")
        if tag:
            match = re.search("«S" + re.escape(tag) + "»(.*?)«E" + re.escape(tag) + "»", plain, re.S)
            if match:
                comment["about"] = collapse(re.sub("«[SE][^»]*»", "", match[1]))
    tags = {}
    for start, end in rtf_groups(rtf, "annotation"):
        tags[value_of(rtf[start:end], "atnref")] = len(tags)
    for comment in comments:
        comment["parent"] = tags.get(comment["parent"]) if comment["parent"] else None
    return comments


def doc(path):
    comments = sorted(doc_comments.read(path), key=lambda c: c["reference"])
    for comment in comments:
        del comment["reference"]
    return comments


def main():
    expected = {}
    for name in FILES:
        path = os.path.join(FIXTURES, name)
        extension = os.path.splitext(name)[1]
        reader = {".docx": docx_comments, ".odt": odt_comments, ".rtf": rtf_comments, ".doc": doc}[extension]
        expected[name] = reader(path)
    with open(os.path.join(HERE, "expected.json"), "w", encoding="utf-8") as file:
        json.dump(expected, file, indent=1, ensure_ascii=False)
        file.write("\n")
    json.dump(expected, sys.stdout, indent=1, ensure_ascii=False)


if __name__ == "__main__":
    main()

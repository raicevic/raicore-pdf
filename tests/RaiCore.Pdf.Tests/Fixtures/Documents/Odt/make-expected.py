"""Records what an independent reader finds in the OpenDocument text fixtures: the paragraph texts of each document's
body, in order, read with odfpy (flat .fodt documents, which odfpy does not open, and packages whose embedded objects it refuses, with the
standard library's XML
parser). Writes expected.json, keyed by the path under the Fixtures folder: the generated fixtures (Documents/Odt) and
the Apache Tika samples (Tika).

Run from this folder with a venv that has odfpy: python -m venv venv && venv/bin/pip install odfpy &&
venv/bin/python -I make-expected.py. Downloaded documents are only parsed as data.

What is recorded, item by item: a string per paragraph or heading of the body (also in lists, sections and index
bodies), and a list of cell texts per table row (repeated rows repeated). Left out, as the PDF does not show them in
the text flow: deleted text (tracked changes), comments, notes (shown at the end of their section), hidden sections and
hidden text, the ruby text over a base, and fields whose value depends on the layout (page numbers, page counts and
page references). The text of text boxes and shapes follows the paragraph they are anchored in."""
import json
import os
import re
import xml.etree.ElementTree as ET
import zipfile

from odf.opendocument import load

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURES = os.path.normpath(os.path.join(HERE, "..", ".."))
TEXT = "urn:oasis:names:tc:opendocument:xmlns:text:1.0"
TABLE = "urn:oasis:names:tc:opendocument:xmlns:table:1.0"
DRAW = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0"
OFFICE = "urn:oasis:names:tc:opendocument:xmlns:office:1.0"
STYLE = "urn:oasis:names:tc:opendocument:xmlns:style:1.0"
SHAPES = {"rect", "line", "polyline", "polygon", "regular-polygon", "path", "circle", "ellipse", "g", "connector",
          "caption", "measure", "custom-shape"}
INDEXES = {"table-of-content", "illustration-index", "table-index", "object-index", "user-index", "alphabetical-index", "bibliography"}


class Node:
    """An element: namespace, local name, attributes by (namespace, name), and children (nodes and strings)."""

    def __init__(self, ns, name, attributes, children):
        self.ns, self.name, self.attributes, self.children = ns, name, attributes, children

    def attr(self, ns, name):
        return self.attributes.get((ns, name))

    def elements(self):
        return [c for c in self.children if isinstance(c, Node)]

    def find(self, ns, name):
        return next((c for c in self.elements() if c.ns == ns and c.name == name), None)


def from_odfpy(element):
    children = []
    for child in element.childNodes:
        if child.nodeType == child.TEXT_NODE:
            children.append(str(child.data))
        elif child.nodeType == child.ELEMENT_NODE:
            children.append(from_odfpy(child))
    ns, name = element.qname
    return Node(ns, name, {k: str(v) for k, v in element.attributes.items()}, children)


def from_etree(element):
    def split(tag):
        return tuple(tag[1:].split("}", 1)) if tag.startswith("{") else ("", tag)

    children = [element.text] if element.text else []
    for child in element:
        children.append(from_etree(child))
        if child.tail:
            children.append(child.tail)
    ns, name = split(element.tag)
    return Node(ns, name, {split(k): v for k, v in element.attrib.items()}, children)


def open_document(path):
    """The body text (office:text) and the text styles that hide text, by name."""
    if path.endswith(".fodt"):
        root = from_etree(ET.parse(path).getroot())
        body = root.find(OFFICE, "body").find(OFFICE, "text")
        style_roots = [root.find(OFFICE, "styles"), root.find(OFFICE, "automatic-styles")]
    else:
        try:
            document = load(path)
            body = from_odfpy(document.text)
            style_roots = [from_odfpy(document.styles), from_odfpy(document.automaticstyles)]
        except Exception:  # odfpy also loads embedded objects, and refuses those with a DTD (formulas): read the parts.
            with zipfile.ZipFile(path) as package:
                content = from_etree(ET.fromstring(package.read("content.xml")))
                common = from_etree(ET.fromstring(package.read("styles.xml"))).find(OFFICE, "styles")
            body = content.find(OFFICE, "body").find(OFFICE, "text")
            style_roots = [common, content.find(OFFICE, "automatic-styles")]
    hidden = set()
    parents = {}
    for root in style_roots:
        for style in (root.elements() if root else []):
            if style.ns == STYLE and style.name == "style":
                name = style.attr(STYLE, "name")
                properties = style.find(STYLE, "text-properties")
                if properties is not None and properties.attr(TEXT, "display") == "none":
                    hidden.add(name)
                parents[name] = style.attr(STYLE, "parent-style-name")
    for name in list(parents):
        seen, current = set(), name
        while current and current not in seen:
            if current in hidden:
                hidden.add(name)
            seen.add(current)
            current = parents.get(current)
    return body, hidden


class Reader:
    def __init__(self, hidden):
        self.hidden = hidden

    def text(self, node, anchored):
        """The text of a paragraph's content; text boxes and shapes it anchors go to anchored."""
        parts = []
        for child in node.children:
            if isinstance(child, str):
                parts.append(child)
                continue
            ns, name = child.ns, child.name
            if ns == TEXT:
                if name == "s":
                    parts.append(" " * int(child.attr(TEXT, "c") or 1))
                elif name == "tab":
                    parts.append("\t")
                elif name == "line-break":
                    parts.append("\n")
                elif name in ("note", "tracked-changes", "ruby-text", "hidden-text", "page-number", "page-count", "bookmark",
                              "bookmark-start", "bookmark-end", "soft-page-break"):
                    continue
                elif name in ("bookmark-ref", "reference-ref", "sequence-ref", "note-ref") and child.attr(TEXT, "reference-format") == "page":
                    continue
                elif name == "span" and child.attr(TEXT, "style-name") in self.hidden:
                    continue
                else:
                    parts.append(self.text(child, anchored))
            elif ns == DRAW:
                anchored.append(child)
            elif ns == OFFICE:
                continue  # Comments.
            else:
                parts.append(self.text(child, anchored))
        return "".join(parts)

    def blocks(self, container, out):
        for child in container.elements():
            ns, name = child.ns, child.name
            if ns == TEXT and name in ("p", "h"):
                anchored = []
                out.append(re.sub(r"[ \r\n]+", " ", self.text(child, anchored)).strip())
                for drawing in anchored:
                    self.drawing(drawing, out)
            elif ns == TEXT and name == "list":
                for item in child.elements():
                    if item.ns == TEXT and item.name in ("list-item", "list-header"):
                        self.blocks(item, out)
            elif ns == TEXT and name in ("numbered-paragraph", "index-body", "index-title"):
                self.blocks(child, out)
            elif ns == TEXT and name == "section":
                if child.attr(TEXT, "display") != "none":
                    self.blocks(child, out)
            elif ns == TEXT and name in INDEXES:
                body = child.find(TEXT, "index-body")
                if body is not None:
                    self.blocks(body, out)
            elif ns == TABLE and name == "table":
                self.table(child, out)
            elif ns == DRAW:
                self.drawing(child, out)

    def drawing(self, drawing, out):
        """The text of a frame's text boxes, a shape, or the drawings in a group or link."""
        if drawing.name == "frame":
            for box in drawing.elements():
                if box.ns == DRAW and box.name == "text-box":
                    self.blocks(box, out)
        elif drawing.name in SHAPES or drawing.name == "a":
            self.blocks(drawing, out)

    def table(self, table, out):
        for row in self.rows(table):
            cells = []
            for cell in row.elements():
                if cell.ns == TABLE and cell.name == "table-cell":
                    inner = []
                    self.blocks(cell, inner)
                    flat = [t if isinstance(t, str) else " ".join(t) for t in inner]
                    text = " ".join(" ".join(flat).split())
                    cells.extend([text] * min(int(cell.attr(TABLE, "number-columns-repeated") or 1), 64))
            cells = [c for c in cells if c]
            for _ in range(min(int(row.attr(TABLE, "number-rows-repeated") or 1), 100)):
                if len(cells) == 1:
                    out.append(cells[0])
                elif cells:
                    out.append(cells)

    def rows(self, container):
        for child in container.elements():
            if child.ns == TABLE and child.name == "table-row":
                yield child
            elif child.ns == TABLE and child.name in ("table-header-rows", "table-rows", "table-row-group"):
                yield from self.rows(child)


def expected(path):
    body, hidden = open_document(path)
    out = []
    Reader(hidden).blocks(body, out)
    return [item for item in out if item]


def main():
    result = {}
    for folder in ("Documents/Odt", "Tika"):
        for name in sorted(os.listdir(os.path.join(FIXTURES, folder))):
            path = os.path.join(FIXTURES, folder, name)
            if not name.endswith((".odt", ".ott", ".fodt")) or not (name.endswith(".fodt") or zipfile.is_zipfile(path)):
                continue
            if name.endswith(".odt") and zipfile.is_zipfile(path) and b"encryption-data" in zipfile.ZipFile(path).read("META-INF/manifest.xml"):
                continue  # Encrypted: not readable.
            result[f"{folder}/{name}"] = expected(path)
    with open(os.path.join(HERE, "expected.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(result, f, ensure_ascii=False, indent=1)
        f.write("\n")


main()

"""Records what an independent RTF reader (striprtf, pure Python) finds in the RTF fixtures: the hand-made ones in this
folder and the Apache Tika samples in ../../Tika. Run with a venv that has striprtf: python -m venv venv &&
venv/bin/pip install striprtf; from this folder: venv/bin/python make-expected.py.

Writes expected.json: per document (by its path under Fixtures), "text" lists the body's paragraphs in order (a table
row as a list of its cells, which may come in any order), and "anywhere" the text of headers, footers and notes, which
the PDF has elsewhere (on every page, after the section). Before striprtf reads a document, groups whose text the
converter shows elsewhere or not at all are taken out: headers, footers, notes, the writer's list labels (the converter
counts its own), pictures, hidden and deleted text, comments and shape fallbacks."""
import glob
import json
import os
import re

from striprtf.striprtf import rtf_to_text

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURES = os.path.normpath(os.path.join(HERE, "..", ".."))

ELSEWHERE = ("header", "headerl", "headerr", "headerf", "footer", "footerl", "footerr", "footerf", "footnote")
LEFT_OUT = ("pict", "listtext", "pntext", "v", "deleted", "annotation", "nonshppict", "shprslt", "nonesttables", "atnid", "atnauthor")


def group_end(rtf, start):
    """The index after the group that starts at rtf[start] == '{' (escaped braces and \\bin data skipped)."""
    depth = 0
    i = start
    while i < len(rtf):
        c = rtf[i]
        if c == "\\":
            m = re.match(r"\\bin(\d+) ?", rtf[i:])
            if m:
                i += len(m.group(0)) + int(m.group(1))
                continue
            i += 2
            continue
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return len(rtf)


def take_out(rtf):
    """The document without the groups above, and the groups whose text is shown elsewhere."""
    pattern = re.compile(r"\{(?:\\\*)?\\(" + "|".join(ELSEWHERE + LEFT_OUT) + r")(?![a-z])(-?\d+)?")
    out, elsewhere, i = [], [], 0
    while True:
        m = pattern.search(rtf, i)
        if not m:
            out.append(rtf[i:])
            break
        word = m.group(1)
        if word == "v" and m.group(2) == "0":
            out.append(rtf[i:m.end()])
            i = m.end()
            continue
        end = group_end(rtf, m.start())
        out.append(rtf[i:m.start()])
        if word in ELSEWHERE:
            elsewhere.append(rtf[m.end():end - 1])
        i = end
    return "".join(out), elsewhere


def text_of(rtf):
    text = rtf_to_text(rtf, errors="ignore")
    return text.encode("utf-16", "surrogatepass").decode("utf-16", "replace")


def header_of(rtf):
    """The document's code page and font table, for reading parts on their own."""
    page = re.search(r"\\ansicpg\d+", rtf)
    header = "{\\rtf1\\ansi" + (page.group(0) if page else "")
    m = re.search(r"\{\\fonttbl", rtf)
    return header + (rtf[m.start():group_end(rtf, m.start())] if m else "")


def paragraphs(text):
    items = []
    for line in text.replace("\r", "").split("\n"):
        line = line.replace("\u00a0", " ").strip()
        if not line:
            continue
        if "|" in line:
            cells = [c.strip() for c in line.split("|") if c.strip()]
            if cells:
                items.append(cells)
        else:
            items.append(line)
    return items


def main():
    expected = {}
    files = sorted(glob.glob(os.path.join(HERE, "*.rtf"))) + sorted(glob.glob(os.path.join(FIXTURES, "Tika", "*.rtf")))
    for path in files:
        rtf = open(path, "rb").read().decode("latin-1")
        body, elsewhere = take_out(rtf)
        anywhere = []
        for part in elsewhere:
            anywhere.extend(p for p in paragraphs(text_of(header_of(rtf) + part + "}")) if isinstance(p, str))
        key = os.path.relpath(path, FIXTURES).replace(os.sep, "/")
        expected[key] = {"text": paragraphs(text_of(body)), "anywhere": anywhere}
    with open(os.path.join(HERE, "expected.json"), "w", encoding="utf-8") as f:
        json.dump(expected, f, ensure_ascii=False, indent=1)
        f.write("\n")


main()

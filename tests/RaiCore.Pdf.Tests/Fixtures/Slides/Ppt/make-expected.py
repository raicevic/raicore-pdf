"""Records in expected.json what an independent reader finds in the PowerPoint 97-2003 files of this folder, for the
PptToPdf tests (Slides/PptToPdfTests.cs).

There is no complete .ppt reader for Python, so the reader here is deliberately small and separate from the C#
converter: olefile opens the compound file; then only records are walked ([MS-PPT] 2.3): the "Current User" stream's
offset of the current edit, the user edit chain and its persist directories (newest last), the document's slide list
(SlidePersistAtom order) and, per slide, the TextCharsAtom / TextBytesAtom text that follows its slide list entry and
the text atoms anywhere in its drawing. It also records the slide size (DocumentAtom), whether each slide is hidden
(SlideShowSlideInfoAtom) and the title and author of the summary information, and classifies files it cannot read
(encrypted, PowerPoint 95 or 4.0).

Next to that, "quoted" holds expectations copied from Apache POI's and Apache Tika's own unit tests for these files
(sources and lines below), at the commits the files come from.

Run with a venv that has olefile, from this folder, isolated: venv/bin/python -I make-expected.py"""
import glob
import json
import os
import struct

import olefile

HERE = os.path.dirname(os.path.abspath(__file__))
ENCRYPTED = 0xF3D1C4DF

POI = ("https://github.com/apache/poi/blob/f66927954a29cf1f5b86614fdf1816e6321c8aea/"
       "poi-scratchpad/src/test/java/org/apache/poi/hslf/")
TIKA = ("https://github.com/apache/tika/blob/6ef8f501b1610fcb36d275332d2337e3113de74d/"
        "tika-parsers/tika-parsers-standard/tika-parsers-standard-modules/tika-parser-microsoft-module/src/test/java/"
        "org/apache/tika/parser/microsoft/PowerPointParserTest.java")

# Text the slides contain (in order), as POI's and Tika's tests assert it.
QUOTED = {
    "basic_test_ppt_file.ppt": {
        "source": POI + "extractor/TestExtractor.java#L61-L70 (EXPECTED_PAGE1, EXPECTED_PAGE2)",
        "slides": [["This is a test title", "This is a test subtitle", "This is on page 1"],
                   ["This is the title on page 2", "This is page two", "It has several blocks of text",
                    "None of them have formatting"]],
    },
    "with_textbox.ppt": {
        "source": POI + "extractor/TestExtractor.java#L97-L101 (expectText2)",
        "slides": [["Hello, World!!!", "I am just a poor boy", "This is Times New Roman", "Plain Text"]],
    },
    "bug58144-headers-footers-2003.ppt": {
        "source": POI + "model/TestHeadersFooters.java#L238-L246 (footer 'Confidential', text 'Test')",
        "slides": [["Test", "Confidential"]],
    },
    "testPPT.ppt": {
        "source": TIKA + "#L81-L93 (title, creator, content)",
        "title": "Sample Powerpoint Slide",
        "author": "Keith Bennett",
        "slides": [["Sample Powerpoint Slide", "Powerpoint X for Mac"]],
    },
    "testPPT_groups.ppt": {
        "source": TIKA + "#L366-L391 (testGroups)",
        "slides": [["Text box1", "WordArt1", "WordArt2", "Ungrouped text box", "Text box2", "Text box3", "Text box4",
                    "Text box5", "tika", "MyTitle"]],
    },
    "testPPT_various.ppt": {
        "source": TIKA + "#L97-L121 (testVarious)",
        "slides": [["[1] This is a footnote.", "Here is a text box", "(Kramer)", "Row 1 Col 1", "Row 2 Col 3",
                    "Row 1 column 1", "Row 2 column 2"]],
    },
    "Password_Protected-hello.ppt": {"source": "an encrypted presentation (POI's HSLF crypto tests)", "error": "encrypted"},
}


def records(data, start, end):
    off = start
    while off + 8 <= end:
        vi, typ, ln = struct.unpack_from("<HHI", data, off)
        yield vi & 0xF, vi >> 4, typ, off + 8, min(off + 8 + ln, end)
        off += 8 + ln


def persist_map(doc, offset):
    chain, seen = [], set()
    while offset and offset not in seen and offset + 8 <= len(doc):
        seen.add(offset)
        _, typ, _ = struct.unpack_from("<HHI", doc, offset)
        if typ != 0x0FF5:
            break
        last_edit, dir_off, doc_ref = struct.unpack_from("<III", doc, offset + 16)
        chain.append((dir_off, doc_ref))
        offset = last_edit
    persist = {}
    if chain and offset:
        # A broken chain: the persist directories of the stream's top-level records first.
        for _, _, typ, body, stop in records(doc, 0, len(doc)):
            if typ == 0x1772:
                read_directory(doc, body, stop, persist)
    for dir_off, _ in reversed(chain):
        _, _, ln = struct.unpack_from("<HHI", doc, dir_off)
        read_directory(doc, dir_off + 8, dir_off + 8 + ln, persist)
    return persist, (chain[0][1] if chain else 1)


def read_directory(doc, pos, end, persist):
    while pos + 4 <= end:
        word = struct.unpack_from("<I", doc, pos)[0]
        pos += 4
        for i in range(word >> 20):
            if pos + 4 > end:
                break
            persist[(word & 0xFFFFF) + i] = struct.unpack_from("<I", doc, pos)[0]
            pos += 4


def text_of(doc, typ, body, stop):
    return doc[body:stop].decode("utf-16-le" if typ == 0x0FA0 else "cp1252", "replace")


def texts_in(doc, start, end, depth=0):
    out = []
    for ver, _, typ, body, stop in records(doc, start, end):
        if typ in (0x0FA0, 0x0FA8):
            out.append(text_of(doc, typ, body, stop))
        elif ver == 0xF and depth < 50:
            out.extend(texts_in(doc, body, stop, depth + 1))
    return out


def describe(path):
    ole = olefile.OleFileIO(path)
    names = ["/".join(n) for n in ole.listdir()]
    if "PowerPoint Document" not in names:
        return {"error": "old" if "PP40" in names or "Header" in names else "not a presentation"}
    cu = ole.openstream("Current User").read()
    _, typ, _, _, token, offset = struct.unpack_from("<HHIIII", cu)
    if typ != 0x0FF6:
        return {"error": "old"}
    if token == ENCRYPTED:
        return {"error": "encrypted"}
    doc = ole.openstream("PowerPoint Document").read()
    persist, doc_ref = persist_map(doc, offset)
    doc_off = persist[doc_ref]
    _, _, ln = struct.unpack_from("<HHI", doc, doc_off)
    size = None
    slides = []
    for _, inst, typ, body, stop in records(doc, doc_off + 8, doc_off + 8 + ln):
        if typ == 0x03E9:
            x, y = struct.unpack_from("<ii", doc, body)
            size = [x / 8, y / 8]
        if typ != 0x0FF0 or inst != 0:
            continue
        current = None
        for _, _, ctyp, cbody, cstop in records(doc, body, stop):
            if ctyp == 0x03F3:
                current = {"persist": struct.unpack_from("<I", doc, cbody)[0], "texts": []}
                slides.append(current)
            elif current is not None and ctyp in (0x0FA0, 0x0FA8):
                current["texts"].append(text_of(doc, ctyp, cbody, cstop))
    result = []
    for slide in slides:
        texts, hidden = list(slide["texts"]), False
        off = persist.get(slide["persist"])
        if off is None or off + 8 > len(doc) or struct.unpack_from("<HHI", doc, off)[1] != 0x03EE:
            continue
        _, _, ln = struct.unpack_from("<HHI", doc, off)
        for _, _, ctyp, cbody, cstop in records(doc, off + 8, off + 8 + ln):
            if ctyp == 0x040C:
                texts.extend(texts_in(doc, cbody, cstop))
            elif ctyp == 0x03F9 and cstop - cbody >= 12:
                hidden = bool(struct.unpack_from("<H", doc, cbody + 10)[0] & 0x4)
        result.append({"hidden": hidden, "texts": texts})
    meta = ole.get_metadata()
    page = (meta.codepage or 1252) & 0xFFFF  # olefile reads it as a signed 16-bit value
    codepage = {65001: "utf-8", 1200: "utf-16-le", 10000: "mac_roman"}.get(page, "cp%d" % page)
    return {
        "size": size,
        "title": meta.title.decode(codepage, "replace") if meta.title else None,
        "author": meta.author.decode(codepage, "replace") if meta.author else None,
        "slides": result,
    }


if __name__ == "__main__":
    expected = {}
    for path in sorted(glob.glob(os.path.join(HERE, "*.ppt"))):
        name = os.path.basename(path)
        expected[name] = describe(path)
        if name in QUOTED:
            expected[name]["quoted"] = QUOTED[name]
    with open(os.path.join(HERE, "expected.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(expected, f, indent=1, ensure_ascii=True, sort_keys=True)
        f.write("\n")
    print(len(expected), "presentations")
